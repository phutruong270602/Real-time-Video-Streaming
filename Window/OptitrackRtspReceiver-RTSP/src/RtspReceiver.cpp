#include "OptitrackRtspReceiver/RtspReceiver.h"

extern "C" {
#include <libavcodec/codec_id.h>
#include <libavformat/avformat.h>
#include <libavutil/error.h>
#include <libavutil/time.h>
}

#include <exception>
#include <limits>
#include <memory>
#include <utility>

namespace optitrack::rtsp {
namespace {

using FormatContextPtr = std::unique_ptr<AVFormatContext, void (*)(AVFormatContext*)>;

void close_format_context(AVFormatContext* context)
{
    if (context != nullptr) {
        avformat_close_input(&context);
    }
}

std::string ffmpeg_error(int error_code)
{
    char buffer[AV_ERROR_MAX_STRING_SIZE]{};
    av_strerror(error_code, buffer, sizeof(buffer));
    return buffer;
}

bool valid_rtsp_url(const std::string& url)
{
    return url.rfind("rtsp://", 0) == 0 || url.rfind("rtsps://", 0) == 0;
}

} // namespace

struct RtspReceiver::OpenedStream {
    FormatContextPtr context{nullptr, close_format_context};
    int video_stream_index = -1;
    StreamInfo info;
};

RtspReceiver::~RtspReceiver()
{
    stop();
}

bool RtspReceiver::start(const ReceiverOptions& options, std::string& error)
{
    std::lock_guard<std::mutex> lifecycle_lock(lifecycle_mutex_);
    const auto generation = cancellation_generation_.load();
    stop_locked();
    error.clear();

    {
        std::lock_guard<std::mutex> queue_lock(queue_mutex_);
        queue_.clear();
    }
    {
        std::lock_guard<std::mutex> state_lock(state_mutex_);
        last_error_.clear();
        stream_info_ = {};
    }

    if (!valid_rtsp_url(options.url)) {
        error = "URL must use the rtsp:// or rtsps:// scheme.";
        set_error(error);
        return false;
    }
    if (options.queue_capacity == 0) {
        error = "Queue capacity must be greater than zero.";
        set_error(error);
        return false;
    }
    if (options.network_timeout.count() <= 0 ||
        options.network_timeout.count() > std::numeric_limits<int>::max() / 1000) {
        error = "Network timeout must be a positive duration representable in microseconds.";
        set_error(error);
        return false;
    }
    if (options.reconnect_delay.count() < 0 ||
        (options.reconnect && options.reconnect_delay.count() == 0)) {
        error = "Reconnect delay must be positive when reconnection is enabled.";
        set_error(error);
        return false;
    }

    options_ = options;
    active_generation_ = generation;
    stop_requested_ = false;
    connected_ = false;
    running_ = false;
    packets_received_ = 0;
    bytes_received_ = 0;
    packets_dropped_ = 0;
    static const int network_init_result = avformat_network_init();
    if (network_init_result < 0) {
        error = "FFmpeg network initialization failed: " + ffmpeg_error(network_init_result);
        set_error(error);
        stop_requested_ = true;
        return false;
    }

    OpenedStream stream;
    if (!open_stream(stream, error)) {
        if (cancellation_generation_.load() != generation) {
            error = "RTSP receiver start was cancelled.";
        }
        set_error(error);
        stop_requested_ = true;
        return false;
    }
    if (cancellation_generation_.load() != generation) {
        error = "RTSP receiver start was cancelled.";
        set_error(error);
        stop_requested_ = true;
        return false;
    }

    {
        std::lock_guard<std::mutex> state_lock(state_mutex_);
        stream_info_ = stream.info;
        last_error_.clear();
    }

    try {
        running_ = true;
        connected_ = true;
        worker_ = std::thread(&RtspReceiver::receive_loop, this, std::move(stream));
    } catch (const std::exception& exception) {
        running_ = false;
        connected_ = false;
        stop_requested_ = true;
        error = std::string("Could not start RTSP receive thread: ") + exception.what();
        set_error(error);
        return false;
    }

    error.clear();
    return true;
}

void RtspReceiver::stop()
{
    cancellation_generation_++;
    stop_requested_ = true;
    reconnect_changed_.notify_all();
    queue_changed_.notify_all();

    std::lock_guard<std::mutex> lifecycle_lock(lifecycle_mutex_);
    stop_locked();
}

void RtspReceiver::stop_locked()
{
    stop_requested_ = true;
    reconnect_changed_.notify_all();
    queue_changed_.notify_all();

    if (worker_.joinable()) {
        worker_.join();
    }

    connected_ = false;
    running_ = false;
}

bool RtspReceiver::try_pop(EncodedVideoPacket& packet)
{
    std::lock_guard<std::mutex> lock(queue_mutex_);
    if (queue_.empty()) {
        return false;
    }

    packet = std::move(queue_.front());
    queue_.pop_front();
    return true;
}

bool RtspReceiver::wait_pop(EncodedVideoPacket& packet, std::chrono::milliseconds timeout)
{
    std::unique_lock<std::mutex> lock(queue_mutex_);
    queue_changed_.wait_for(lock, timeout, [this] {
        return !queue_.empty() || !running_.load();
    });

    if (queue_.empty()) {
        return false;
    }

    packet = std::move(queue_.front());
    queue_.pop_front();
    return true;
}

bool RtspReceiver::is_running() const noexcept
{
    return running_;
}

bool RtspReceiver::is_connected() const noexcept
{
    return connected_;
}

ReceiverStatistics RtspReceiver::statistics() const noexcept
{
    return {
        packets_received_.load(),
        bytes_received_.load(),
        packets_dropped_.load()
    };
}

StreamInfo RtspReceiver::stream_info() const
{
    std::lock_guard<std::mutex> lock(state_mutex_);
    return stream_info_;
}

std::string RtspReceiver::last_error() const
{
    std::lock_guard<std::mutex> lock(state_mutex_);
    return last_error_;
}

int RtspReceiver::interrupt_callback(void* opaque)
{
    const auto* receiver = static_cast<const RtspReceiver*>(opaque);
    return receiver->stop_requested_.load() ||
        receiver->cancellation_generation_.load() != receiver->active_generation_.load();
}

bool RtspReceiver::open_stream(OpenedStream& stream, std::string& error)
{
    AVFormatContext* context = avformat_alloc_context();
    if (context == nullptr) {
        error = "Could not allocate FFmpeg format context.";
        return false;
    }

    context->interrupt_callback.callback = &RtspReceiver::interrupt_callback;
    context->interrupt_callback.opaque = this;

    AVDictionary* options = nullptr;
    const char* transport = options_.transport == Transport::Tcp ? "tcp" : "udp";
    const auto timeout_us = std::to_string(options_.network_timeout.count() * 1000);
    av_dict_set(&options, "rtsp_transport", transport, 0);
    av_dict_set(&options, "max_delay", "500000", 0);
    av_dict_set(&options, "buffer_size", "4194304", 0);
    av_dict_set(&options, "timeout", timeout_us.c_str(), 0);
    av_dict_set(&options, "rw_timeout", timeout_us.c_str(), 0);
    av_dict_set(&options, "reorder_queue_size", "500", 0);

    const int open_result = avformat_open_input(&context, options_.url.c_str(), nullptr, &options);
    av_dict_free(&options);
    if (open_result < 0) {
        if (context != nullptr) {
            avformat_close_input(&context);
        }
        error = "Could not open RTSP stream: " + ffmpeg_error(open_result);
        return false;
    }
    stream.context.reset(context);

    const int info_result = avformat_find_stream_info(context, nullptr);
    if (info_result < 0) {
        error = "Could not read RTSP stream information: " + ffmpeg_error(info_result);
        return false;
    }

    const int video_index = av_find_best_stream(
        context,
        AVMEDIA_TYPE_VIDEO,
        -1,
        -1,
        nullptr,
        0);
    if (video_index < 0) {
        error = "RTSP stream has no usable video stream: " + ffmpeg_error(video_index);
        return false;
    }

    const AVCodecParameters* codec_parameters = context->streams[video_index]->codecpar;
    if (codec_parameters->codec_id != AV_CODEC_ID_H264) {
        error = std::string("Expected H.264 video, received ") +
            avcodec_get_name(codec_parameters->codec_id) + ".";
        return false;
    }

    stream.video_stream_index = video_index;
    std::vector<std::uint8_t> codec_extradata;
    if (codec_parameters->extradata != nullptr && codec_parameters->extradata_size > 0) {
        codec_extradata.assign(
            codec_parameters->extradata,
            codec_parameters->extradata + codec_parameters->extradata_size);
    }
    stream.info = {
        avcodec_get_name(codec_parameters->codec_id),
        codec_parameters->width,
        codec_parameters->height,
        video_index,
        std::move(codec_extradata)
    };
    return true;
}

void RtspReceiver::receive_loop(OpenedStream stream)
{
    AVPacket* packet = av_packet_alloc();
    if (packet == nullptr) {
        set_error("Could not allocate FFmpeg packet.");
        connected_ = false;
        running_ = false;
        queue_changed_.notify_all();
        return;
    }

    try {
        while (!stop_requested_) {
            const int read_result = av_read_frame(stream.context.get(), packet);
            if (read_result >= 0) {
                if (packet->stream_index == stream.video_stream_index && packet->size > 0) {
                    EncodedVideoPacket frame;
                    frame.data.assign(packet->data, packet->data + packet->size);
                    frame.timestamp_ms = av_gettime() / 1000;
                    frame.pts = packet->pts;
                    frame.dts = packet->dts;
                    frame.duration = packet->duration;
                    frame.stream_index = packet->stream_index;
                    frame.codec_id = stream.context->streams[stream.video_stream_index]->codecpar->codec_id;
                    frame.width = stream.info.width;
                    frame.height = stream.info.height;
                    frame.key_frame = (packet->flags & AV_PKT_FLAG_KEY) != 0;

                    packets_received_++;
                    bytes_received_ += static_cast<std::uint64_t>(packet->size);
                    enqueue(std::move(frame));
                }
                av_packet_unref(packet);
                continue;
            }

            av_packet_unref(packet);
            if (stop_requested_) {
                break;
            }

            connected_ = false;
            set_error("RTSP packet read failed: " + ffmpeg_error(read_result));
            stream.context.reset();
            if (!options_.reconnect) {
                break;
            }

            bool reconnected = false;
            while (!stop_requested_) {
                if (!wait_for_reconnect_delay() || stop_requested_) {
                    break;
                }

                OpenedStream replacement;
                std::string error;
                if (!open_stream(replacement, error)) {
                    if (stop_requested_) {
                        break;
                    }
                    set_error("RTSP reconnect failed: " + error);
                    continue;
                }

                stream = std::move(replacement);
                {
                    std::lock_guard<std::mutex> lock(state_mutex_);
                    stream_info_ = stream.info;
                    last_error_.clear();
                }
                connected_ = true;
                reconnected = true;
                break;
            }
            if (!reconnected) {
                break;
            }
        }
    } catch (const std::exception& exception) {
        set_error(std::string("RTSP receive failed: ") + exception.what());
    } catch (...) {
        set_error("RTSP receive failed with an unknown error.");
    }

    av_packet_free(&packet);
    connected_ = false;
    running_ = false;
    queue_changed_.notify_all();
}

void RtspReceiver::enqueue(EncodedVideoPacket packet)
{
    {
        std::lock_guard<std::mutex> lock(queue_mutex_);
        if (queue_.size() >= options_.queue_capacity) {
            queue_.pop_front();
            packets_dropped_++;
        }
        queue_.push_back(std::move(packet));
    }
    queue_changed_.notify_one();
}

void RtspReceiver::set_error(std::string error)
{
    std::lock_guard<std::mutex> lock(state_mutex_);
    last_error_ = std::move(error);
}

bool RtspReceiver::wait_for_reconnect_delay()
{
    std::unique_lock<std::mutex> lock(state_mutex_);
    return !reconnect_changed_.wait_for(
        lock,
        options_.reconnect_delay,
        [this] { return stop_requested_.load(); });
}

} // namespace optitrack::rtsp
