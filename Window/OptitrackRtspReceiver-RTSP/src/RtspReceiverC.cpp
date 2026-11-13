#include "OptitrackRtspReceiver/RtspReceiverC.h"
#include "OptitrackRtspReceiver/RtspReceiver.h"

extern "C" {
#include <libavcodec/avcodec.h>
#include <libavutil/error.h>
#include <libavutil/imgutils.h>
#include <libavutil/mem.h>
#include <libswscale/swscale.h>
}

#include <algorithm>
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstdio>
#include <ctime>
#include <cstring>
#include <deque>
#include <exception>
#include <filesystem>
#include <fstream>
#include <iomanip>
#include <limits>
#include <mutex>
#include <new>
#include <stdexcept>
#include <string>
#include <thread>
#include <utility>
#include <vector>

#ifndef OPTITRACK_RTSP_LOG_FILE
#define OPTITRACK_RTSP_LOG_FILE "log/rtsp_receiver.log"
#endif

#ifndef OPTITRACK_RTSP_DECODED_FRAME_LOG_FILE
#define OPTITRACK_RTSP_DECODED_FRAME_LOG_FILE "log/decoded_frames.log"
#endif

#ifdef _WIN32
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#endif

namespace {

constexpr std::size_t maximum_review_queue_bytes = 1024ULL * 1024 * 1024;
constexpr std::size_t maximum_pending_review_bytes = 512ULL * 1024 * 1024;
constexpr int review_jpeg_quantizer = 2;

struct ReviewFrame {
    std::vector<std::uint8_t> jpeg;
    int width = 0;
    int height = 0;
    std::uint64_t sequence = 0;
    std::int64_t timestamp_ms = 0;
};

struct PendingReviewFrame {
    std::vector<std::uint8_t> bgra;
    int width = 0;
    int height = 0;
    int stride = 0;
    std::int64_t timestamp_ms = 0;
};

void log_event(const std::string& event) noexcept
{
    static std::mutex log_mutex;
    static bool failure_reported = false;
    std::lock_guard<std::mutex> lock(log_mutex);

    try {
        static std::ofstream output = [] {
            const std::filesystem::path path(OPTITRACK_RTSP_LOG_FILE);
            std::filesystem::create_directories(path.parent_path());
            return std::ofstream(path, std::ios::out | std::ios::app);
        }();
        if (!output) {
            throw std::runtime_error("Could not open RTSP receiver log file.");
        }

        const auto now = std::chrono::system_clock::now();
        const auto milliseconds = std::chrono::duration_cast<std::chrono::milliseconds>(
            now.time_since_epoch()) % 1000;
        const auto time = std::chrono::system_clock::to_time_t(now);
        std::tm local_time{};
#ifdef _WIN32
        localtime_s(&local_time, &time);
#else
        localtime_r(&time, &local_time);
#endif
        output << std::put_time(&local_time, "%Y-%m-%d %H:%M:%S")
               << '.' << std::setfill('0') << std::setw(3) << milliseconds.count()
               << " [thread " << std::this_thread::get_id() << "] "
               << event << '\n';
        output.flush();
        if (!output) {
            throw std::runtime_error("Could not write RTSP receiver log file.");
        }
    } catch (const std::exception& exception) {
        if (!failure_reported) {
            failure_reported = true;
#ifdef _WIN32
            const auto message = std::string("Optitrack RTSP log error: ") + exception.what() + "\n";
            OutputDebugStringA(message.c_str());
#else
            std::fprintf(stderr, "Optitrack RTSP log error: %s\n", exception.what());
#endif
        }
    } catch (...) {
        if (!failure_reported) {
            failure_reported = true;
#ifdef _WIN32
            OutputDebugStringA("Optitrack RTSP log error: unknown failure.\n");
#else
            std::fprintf(stderr, "Optitrack RTSP log error: unknown failure.\n");
#endif
        }
    }
}

void log_decoded_frame(const std::string& event) noexcept
{
    static std::mutex log_mutex;
    static bool failure_reported = false;
    static std::uint64_t frame_log_count = 0;
    std::lock_guard<std::mutex> lock(log_mutex);

    try {
        static std::ofstream output = [] {
            const std::filesystem::path path(OPTITRACK_RTSP_DECODED_FRAME_LOG_FILE);
            if (!path.parent_path().empty()) {
                std::filesystem::create_directories(path.parent_path());
            }
            return std::ofstream(path, std::ios::out | std::ios::app);
        }();
        if (!output) {
            throw std::runtime_error("Could not open decoded frame log file.");
        }

        const auto now = std::chrono::system_clock::now();
        const auto milliseconds = std::chrono::duration_cast<std::chrono::milliseconds>(
            now.time_since_epoch()) % 1000;
        const auto time = std::chrono::system_clock::to_time_t(now);
        std::tm local_time{};
#ifdef _WIN32
        localtime_s(&local_time, &time);
#else
        localtime_r(&time, &local_time);
#endif
        output << std::put_time(&local_time, "%Y-%m-%d %H:%M:%S")
               << '.' << std::setfill('0') << std::setw(3) << milliseconds.count()
               << " [thread " << std::this_thread::get_id() << "] "
               << event << '\n';
        ++frame_log_count;
        if (frame_log_count == 1 || frame_log_count % 30 == 0) {
            output.flush();
        }
        if (!output) {
            throw std::runtime_error("Could not write decoded frame log file.");
        }
    } catch (const std::exception& exception) {
        if (!failure_reported) {
            failure_reported = true;
#ifdef _WIN32
            const auto message = std::string("Optitrack decoded frame log error: ") +
                exception.what() + "\n";
            OutputDebugStringA(message.c_str());
#else
            std::fprintf(stderr, "Optitrack decoded frame log error: %s\n", exception.what());
#endif
        }
    } catch (...) {
        if (!failure_reported) {
            failure_reported = true;
#ifdef _WIN32
            OutputDebugStringA("Optitrack decoded frame log error: unknown failure.\n");
#else
            std::fprintf(stderr, "Optitrack decoded frame log error: unknown failure.\n");
#endif
        }
    }
}

std::string redact_url(const char* url)
{
    std::string safe_url(url);
    const auto query_start = safe_url.find_first_of("?#");
    if (query_start != std::string::npos) {
        safe_url.erase(query_start);
    }

    const auto authority_start = safe_url.find("://");
    if (authority_start != std::string::npos) {
        const auto host_start = authority_start + 3;
        const auto host_end = safe_url.find('/', host_start);
        const auto authority_end = host_end == std::string::npos ? safe_url.size() : host_end;
        const auto credentials_end = safe_url.rfind('@', authority_end);
        if (credentials_end != std::string::npos && credentials_end >= host_start) {
            safe_url.replace(host_start, credentials_end - host_start, "***");
        }
    }
    return safe_url;
}

struct NativeReceiver {
    optitrack::rtsp::RtspReceiver receiver;
    optitrack::rtsp::ReceiverOptions start_options;
    std::mutex lifecycle_mutex;
    std::mutex frame_mutex;
    std::mutex review_frame_mutex;
    std::mutex pending_review_mutex;
    std::condition_variable pending_review_condition;
    std::mutex error_mutex;
    std::thread decoder_thread;
    std::thread review_encoder_thread;
    std::vector<std::uint8_t> latest_frame;
    std::deque<ReviewFrame> review_frames;
    std::deque<PendingReviewFrame> pending_review_frames;
    std::size_t review_queue_bytes = 0;
    std::size_t pending_review_bytes = 0;
    std::uint64_t review_frame_sequence = 0;
    std::uint64_t review_frames_dropped = 0;
    std::uint64_t pending_review_frames_dropped = 0;
    bool stop_review_encoder = true;
    std::atomic<bool> review_mode{false};
    const AVCodec* review_encoder = nullptr;
    AVCodecContext* review_encoder_context = nullptr;
    AVFrame* review_encoder_frame = nullptr;
    SwsContext* review_conversion = nullptr;
    int review_encoder_width = 0;
    int review_encoder_height = 0;
    std::string last_error;
    int width = 0;
    int height = 0;
    int stride = 0;
    std::uint64_t frame_sequence = 0;
    std::int64_t timestamp_ms = 0;
    std::uint64_t frames_decoded = 0;
    std::atomic<bool> stop_decoder{true};

    ~NativeReceiver()
    {
        stop();
    }

    void set_error(std::string error)
    {
        bool changed = false;
        std::string logged_error;
        {
            std::lock_guard<std::mutex> lock(error_mutex);
            changed = !error.empty() && last_error != error;
            if (changed) {
                logged_error = error;
            }
            last_error = std::move(error);
        }
        if (changed) {
            log_event("ERROR " + logged_error);
        }
    }

    void stop()
    {
        std::lock_guard<std::mutex> lock(lifecycle_mutex);
        stop_locked();
    }

    void stop_locked()
    {
        review_mode = false;
        stop_decoder = true;
        receiver.stop();
        if (decoder_thread.joinable()) {
            decoder_thread.join();
        }
        {
            std::lock_guard<std::mutex> lock(pending_review_mutex);
            stop_review_encoder = true;
            pending_review_frames.clear();
            pending_review_bytes = 0;
        }
        pending_review_condition.notify_all();
        if (review_encoder_thread.joinable()) {
            review_encoder_thread.join();
        }
        {
            std::lock_guard<std::mutex> lock(review_frame_mutex);
            review_frames.clear();
            review_queue_bytes = 0;
        }
        reset_review_encoder();
    }

    void enqueue_review_frame(
        const std::vector<std::uint8_t>& bgra,
        int frame_width,
        int frame_height,
        int frame_stride,
        std::int64_t timestamp)
    {
        if (review_mode) {
            return;
        }

        PendingReviewFrame frame;
        frame.bgra = bgra;
        frame.width = frame_width;
        frame.height = frame_height;
        frame.stride = frame_stride;
        frame.timestamp_ms = timestamp;

        std::uint64_t dropped_frames = 0;
        {
            std::lock_guard<std::mutex> lock(pending_review_mutex);
            if (stop_review_encoder || review_mode) {
                return;
            }
            pending_review_bytes += frame.bgra.size();
            pending_review_frames.push_back(std::move(frame));
            while (pending_review_bytes > maximum_pending_review_bytes &&
                   !pending_review_frames.empty()) {
                pending_review_bytes -= pending_review_frames.front().bgra.size();
                pending_review_frames.pop_front();
                ++pending_review_frames_dropped;
                ++dropped_frames;
            }
        }
        if (dropped_frames != 0) {
            log_event("REVIEW_INPUT_QUEUE dropped_frames=" + std::to_string(dropped_frames) +
                " total_dropped=" + std::to_string(pending_review_frames_dropped) +
                " reason=512_MiB_queue_limit");
        }
        pending_review_condition.notify_one();
    }

    void set_review_mode(bool enabled)
    {
        review_mode = enabled;
        if (enabled) {
            {
                std::lock_guard<std::mutex> lock(pending_review_mutex);
                pending_review_frames.clear();
                pending_review_bytes = 0;
            }
            {
                std::lock_guard<std::mutex> lock(review_frame_mutex);
                review_frames.clear();
                review_queue_bytes = 0;
            }
        }
        pending_review_condition.notify_all();
        log_event(std::string("CONTROL review_mode=") + (enabled ? "true" : "false"));
    }

    void review_encoder_loop()
    {
        while (true) {
            PendingReviewFrame frame;
            {
                std::unique_lock<std::mutex> lock(pending_review_mutex);
                pending_review_condition.wait(lock, [this] {
                    return stop_review_encoder ||
                        (!review_mode && !pending_review_frames.empty());
                });
                if (stop_review_encoder) {
                    return;
                }
                if (review_mode) {
                    continue;
                }
                frame = std::move(pending_review_frames.front());
                pending_review_bytes -= frame.bgra.size();
                pending_review_frames.pop_front();
            }

            if (!review_mode) {
                try {
                    encode_review_frame(
                        frame.bgra.data(),
                        frame.width,
                        frame.height,
                        frame.stride,
                        frame.timestamp_ms);
                } catch (const std::exception& exception) {
                    set_error(std::string("Impact review encoder failed: ") + exception.what());
                } catch (...) {
                    set_error("Impact review encoder failed with an unknown error.");
                }
            }
        }
    }

    void reset_review_encoder()
    {
        sws_freeContext(review_conversion);
        review_conversion = nullptr;
        av_frame_free(&review_encoder_frame);
        avcodec_free_context(&review_encoder_context);
        review_encoder = nullptr;
        review_encoder_width = 0;
        review_encoder_height = 0;
    }

    bool encode_review_frame(
        const std::uint8_t* bgra,
        int frame_width,
        int frame_height,
        int frame_stride,
        std::int64_t timestamp)
    {
        if (review_encoder_context == nullptr ||
            review_encoder_width != frame_width ||
            review_encoder_height != frame_height) {
            reset_review_encoder();
            review_encoder = avcodec_find_encoder(AV_CODEC_ID_MJPEG);
            if (review_encoder == nullptr) {
                set_error("FFmpeg MJPEG encoder is unavailable for impact review.");
                return false;
            }

            review_encoder_context = avcodec_alloc_context3(review_encoder);
            review_encoder_frame = av_frame_alloc();
            if (review_encoder_context == nullptr || review_encoder_frame == nullptr) {
                reset_review_encoder();
                set_error("Could not allocate the impact review encoder.");
                return false;
            }
            review_encoder_context->width = frame_width;
            review_encoder_context->height = frame_height;
            review_encoder_context->pix_fmt = AV_PIX_FMT_YUVJ420P;
            review_encoder_context->time_base = AVRational{1, 120};
            review_encoder_context->framerate = AVRational{120, 1};
            review_encoder_context->color_range = AVCOL_RANGE_JPEG;
            review_encoder_context->flags |= AV_CODEC_FLAG_QSCALE;
            review_encoder_context->global_quality = review_jpeg_quantizer * FF_QP2LAMBDA;
            if (avcodec_open2(review_encoder_context, review_encoder, nullptr) < 0) {
                reset_review_encoder();
                set_error("Could not initialize the impact review JPEG encoder.");
                return false;
            }

            review_encoder_frame->format = review_encoder_context->pix_fmt;
            review_encoder_frame->width = frame_width;
            review_encoder_frame->height = frame_height;
            review_encoder_frame->color_range = AVCOL_RANGE_JPEG;
            review_encoder_frame->quality = review_jpeg_quantizer * FF_QP2LAMBDA;
            if (av_frame_get_buffer(review_encoder_frame, 32) < 0) {
                reset_review_encoder();
                set_error("Could not allocate impact review frame storage.");
                return false;
            }

            review_encoder_width = frame_width;
            review_encoder_height = frame_height;
        }

        review_conversion = sws_getCachedContext(
            review_conversion,
            frame_width,
            frame_height,
            AV_PIX_FMT_BGRA,
            frame_width,
            frame_height,
            review_encoder_context->pix_fmt,
            SWS_FAST_BILINEAR,
            nullptr,
            nullptr,
            nullptr);
        if (review_conversion == nullptr || av_frame_make_writable(review_encoder_frame) < 0) {
            set_error("Could not prepare an impact review frame.");
            return false;
        }

        const std::uint8_t* source_data[4]{bgra, nullptr, nullptr, nullptr};
        const int source_linesize[4]{frame_stride, 0, 0, 0};
        if (sws_scale(
                review_conversion,
                source_data,
                source_linesize,
                0,
                frame_height,
                review_encoder_frame->data,
                review_encoder_frame->linesize) != frame_height) {
            set_error("Could not convert a frame for impact review.");
            return false;
        }

        review_encoder_frame->pts = static_cast<std::int64_t>(review_frame_sequence);
        AVPacket* packet = av_packet_alloc();
        if (packet == nullptr) {
            set_error("Could not allocate an impact review packet.");
            return false;
        }

        std::vector<std::uint8_t> jpeg;
        int result = avcodec_send_frame(review_encoder_context, review_encoder_frame);
        if (result >= 0) {
            result = avcodec_receive_packet(review_encoder_context, packet);
        }
        if (result >= 0 && packet->size > 0) {
            jpeg.assign(packet->data, packet->data + packet->size);
        }
        av_packet_free(&packet);
        if (result < 0 || jpeg.empty()) {
            set_error("Could not encode an impact review frame as JPEG.");
            return false;
        }
        if (review_mode) {
            return true;
        }

        ReviewFrame frame;
        frame.jpeg = std::move(jpeg);
        frame.width = frame_width;
        frame.height = frame_height;
        frame.timestamp_ms = timestamp;
        std::uint64_t dropped_frames = 0;
        {
            std::lock_guard<std::mutex> lock(review_frame_mutex);
            if (review_mode) {
                return true;
            }
            frame.sequence = ++review_frame_sequence;
            review_queue_bytes += frame.jpeg.size();
            review_frames.push_back(std::move(frame));
            while (review_queue_bytes > maximum_review_queue_bytes && !review_frames.empty()) {
                review_queue_bytes -= review_frames.front().jpeg.size();
                review_frames.pop_front();
                ++review_frames_dropped;
                ++dropped_frames;
            }
        }
        if (dropped_frames != 0) {
            log_event("REVIEW_QUEUE dropped_frames=" + std::to_string(dropped_frames) +
                " total_dropped=" + std::to_string(review_frames_dropped) +
                " reason=1_GiB_queue_limit");
        }
        return true;
    }

    bool start(std::string& error)
    {
        std::lock_guard<std::mutex> lock(lifecycle_mutex);
        stop_locked();
        {
            std::lock_guard<std::mutex> frame_lock(frame_mutex);
            latest_frame.clear();
            width = 0;
            height = 0;
            stride = 0;
            frame_sequence = 0;
            timestamp_ms = 0;
            frames_decoded = 0;
        }
        {
            std::lock_guard<std::mutex> review_lock(review_frame_mutex);
            review_frames.clear();
            review_queue_bytes = 0;
            review_frame_sequence = 0;
            review_frames_dropped = 0;
        }
        {
            std::lock_guard<std::mutex> pending_lock(pending_review_mutex);
            pending_review_frames.clear();
            pending_review_bytes = 0;
            pending_review_frames_dropped = 0;
            stop_review_encoder = false;
        }
        review_mode = false;
        set_error({});

        if (!receiver.start(start_options, error)) {
            set_error(error);
            return false;
        }

        try {
            review_encoder_thread = std::thread(&NativeReceiver::review_encoder_loop, this);
        } catch (const std::exception& exception) {
            receiver.stop();
            error = std::string("Could not start impact review encoder thread: ") + exception.what();
            set_error(error);
            stop_locked();
            return false;
        }

        stop_decoder = false;
        try {
            decoder_thread = std::thread(&NativeReceiver::decode_loop, this);
        } catch (const std::exception& exception) {
            stop_decoder = true;
            receiver.stop();
            error = std::string("Could not start video decoder thread: ") + exception.what();
            set_error(error);
            stop_locked();
            return false;
        }
        return true;
    }

    void decode_loop()
    {
        const AVCodec* codec = avcodec_find_decoder(AV_CODEC_ID_H264);
        AVCodecContext* codec_context = codec ? avcodec_alloc_context3(codec) : nullptr;
        AVFrame* decoded_frame = av_frame_alloc();
        AVPacket* packet = av_packet_alloc();
        SwsContext* conversion = nullptr;

        if (codec_context == nullptr || decoded_frame == nullptr || packet == nullptr) {
            set_error("Could not initialize the FFmpeg H.264 decoder.");
            receiver.stop();
            av_packet_free(&packet);
            av_frame_free(&decoded_frame);
            avcodec_free_context(&codec_context);
            return;
        }

        const auto stream_info = receiver.stream_info();
        codec_context->width = stream_info.width;
        codec_context->height = stream_info.height;
        if (!stream_info.codec_extradata.empty()) {
            const auto padding = static_cast<std::size_t>(AV_INPUT_BUFFER_PADDING_SIZE);
            if (stream_info.codec_extradata.size() >
                static_cast<std::size_t>(std::numeric_limits<int>::max()) - padding) {
                set_error("H.264 stream configuration is too large for the decoder.");
                receiver.stop();
                av_packet_free(&packet);
                av_frame_free(&decoded_frame);
                avcodec_free_context(&codec_context);
                return;
            }

            codec_context->extradata = static_cast<std::uint8_t*>(av_mallocz(
                stream_info.codec_extradata.size() + padding));
            if (codec_context->extradata == nullptr) {
                set_error("Could not allocate H.264 stream configuration for the decoder.");
                receiver.stop();
                av_packet_free(&packet);
                av_frame_free(&decoded_frame);
                avcodec_free_context(&codec_context);
                return;
            }
            std::memcpy(
                codec_context->extradata,
                stream_info.codec_extradata.data(),
                stream_info.codec_extradata.size());
            codec_context->extradata_size =
                static_cast<int>(stream_info.codec_extradata.size());
        }

        if (avcodec_open2(codec_context, codec, nullptr) < 0) {
            set_error("Could not initialize the FFmpeg H.264 decoder.");
            receiver.stop();
            av_packet_free(&packet);
            av_frame_free(&decoded_frame);
            avcodec_free_context(&codec_context);
            return;
        }

        log_event("DECODE decoder_started codec=H264");
        std::uint64_t packets_decoded = 0;
        bool waiting_for_key_frame = false;
        auto last_connection_state = receiver.is_connected();
        log_event(std::string("CONNECTION state=") +
            (last_connection_state ? "connected" : "disconnected"));

        try {
            optitrack::rtsp::EncodedVideoPacket encoded;
            while (!stop_decoder) {
                const bool packet_available =
                    receiver.wait_pop(encoded, std::chrono::milliseconds(200));
                const auto connected = receiver.is_connected();
                if (connected != last_connection_state) {
                    last_connection_state = connected;
                    log_event(std::string("CONNECTION state=") +
                        (connected ? "connected" : "disconnected"));
                    if (connected) {
                        const auto info = receiver.stream_info();
                        log_event("STREAM codec=" + info.codec_name +
                            " resolution=" + std::to_string(info.width) + "x" +
                            std::to_string(info.height) +
                            " stream_index=" + std::to_string(info.stream_index));
                    }
                }

                if (!packet_available) {
                    if (!receiver.is_running()) {
                        break;
                    }
                    continue;
                }

                if (review_mode) {
                    if (!waiting_for_key_frame) {
                        avcodec_flush_buffers(codec_context);
                        waiting_for_key_frame = true;
                        log_event("DECODE paused_for_review");
                    }
                    while (receiver.try_pop(encoded)) {
                    }
                    continue;
                }

                if (encoded.data.empty() ||
                    encoded.data.size() > static_cast<std::size_t>(std::numeric_limits<int>::max())) {
                    continue;
                }

                if (waiting_for_key_frame) {
                    if (!encoded.key_frame) {
                        continue;
                    }
                    waiting_for_key_frame = false;
                    log_event("DECODE resumed_at_key_frame");
                }

                ++packets_decoded;
                if (packets_decoded == 1 || packets_decoded % 30 == 0) {
                    log_event("PACKET received count=" + std::to_string(packets_decoded) +
                        " bytes=" + std::to_string(encoded.data.size()) +
                        " timestamp_ms=" + std::to_string(encoded.timestamp_ms) +
                        " pts=" + std::to_string(encoded.pts) +
                        " dts=" + std::to_string(encoded.dts) +
                        " duration=" + std::to_string(encoded.duration) +
                        " stream_index=" + std::to_string(encoded.stream_index) +
                        " resolution=" + std::to_string(encoded.width) + "x" +
                        std::to_string(encoded.height) +
                        " key_frame=" + (encoded.key_frame ? "true" : "false"));
                }

                av_packet_unref(packet);
                if (av_new_packet(packet, static_cast<int>(encoded.data.size())) < 0) {
                    set_error("Could not allocate an H.264 decoder packet.");
                    continue;
                }
                std::memcpy(packet->data, encoded.data.data(), encoded.data.size());

                const int send_result = avcodec_send_packet(codec_context, packet);
                if (send_result == AVERROR(EAGAIN)) {
                    set_error("H.264 decoder was not ready; an encoded packet was dropped.");
                } else if (send_result < 0) {
                    char error[AV_ERROR_MAX_STRING_SIZE]{};
                    av_strerror(send_result, error, sizeof(error));
                    set_error(std::string("H.264 decoder rejected a packet: ") + error);
                    continue;
                }

                while (!stop_decoder && !review_mode) {
                    const int receive_result = avcodec_receive_frame(codec_context, decoded_frame);
                    if (receive_result == AVERROR(EAGAIN) || receive_result == AVERROR_EOF) {
                        break;
                    }
                    if (receive_result < 0) {
                        char error[AV_ERROR_MAX_STRING_SIZE]{};
                        av_strerror(receive_result, error, sizeof(error));
                        set_error(std::string("H.264 frame decode failed: ") + error);
                        break;
                    }

                    if (review_mode) {
                        av_frame_unref(decoded_frame);
                        continue;
                    }

                    const int frame_width = decoded_frame->width;
                    const int frame_height = decoded_frame->height;
                    if (frame_width <= 0 || frame_height <= 0 ||
                        frame_width > std::numeric_limits<int>::max() / 4) {
                        av_frame_unref(decoded_frame);
                        continue;
                    }

                    conversion = sws_getCachedContext(
                        conversion,
                        frame_width,
                        frame_height,
                        static_cast<AVPixelFormat>(decoded_frame->format),
                        frame_width,
                        frame_height,
                        AV_PIX_FMT_BGRA,
                        SWS_BILINEAR,
                        nullptr,
                        nullptr,
                        nullptr);
                    if (conversion == nullptr) {
                        set_error("Could not create the video color conversion context.");
                        av_frame_unref(decoded_frame);
                        continue;
                    }

                    const int frame_stride = frame_width * 4;
                    std::vector<std::uint8_t> bgra(
                        static_cast<std::size_t>(frame_stride) * static_cast<std::size_t>(frame_height));
                    std::uint8_t* destination_data[4]{bgra.data(), nullptr, nullptr, nullptr};
                    int destination_linesize[4]{frame_stride, 0, 0, 0};
                    const int rows = sws_scale(
                        conversion,
                        decoded_frame->data,
                        decoded_frame->linesize,
                        0,
                        frame_height,
                        destination_data,
                        destination_linesize);
                    if (rows == frame_height) {
                        std::uint64_t decoded_count = 0;
                        std::uint64_t sequence = 0;
                        const auto sample_byte_count = std::min<std::size_t>(16, bgra.size());
                        std::string pixel_sample = "pixel_bytes_first_16=[";
                        for (std::size_t i = 0; i < sample_byte_count; ++i) {
                            if (i != 0) {
                                pixel_sample += ',';
                            }
                            pixel_sample += std::to_string(
                                static_cast<unsigned int>(bgra[i]));
                        }
                        pixel_sample += "] pixels_first_4_bgra=[";
                        const auto sample_pixel_count = sample_byte_count / 4;
                        for (std::size_t i = 0; i < sample_pixel_count; ++i) {
                            if (i != 0) {
                                pixel_sample += ',';
                            }
                            const auto offset = i * 4;
                            pixel_sample += "(B=" +
                                std::to_string(static_cast<unsigned int>(bgra[offset])) +
                                ",G=" +
                                std::to_string(static_cast<unsigned int>(bgra[offset + 1])) +
                                ",R=" +
                                std::to_string(static_cast<unsigned int>(bgra[offset + 2])) +
                                ",A=" +
                                std::to_string(static_cast<unsigned int>(bgra[offset + 3])) +
                                ')';
                        }
                        pixel_sample += ']';
                        enqueue_review_frame(
                            bgra,
                            frame_width,
                            frame_height,
                            frame_stride,
                            encoded.timestamp_ms);
                        {
                            std::lock_guard<std::mutex> lock(frame_mutex);
                            latest_frame = std::move(bgra);
                            width = frame_width;
                            height = frame_height;
                            stride = frame_stride;
                            timestamp_ms = encoded.timestamp_ms;
                            ++frame_sequence;
                            ++frames_decoded;
                            decoded_count = frames_decoded;
                            sequence = frame_sequence;
                        }
                        log_decoded_frame("FRAME decoded_count=" +
                            std::to_string(decoded_count) +
                            " sequence=" + std::to_string(sequence) +
                            " resolution=" + std::to_string(frame_width) + "x" +
                            std::to_string(frame_height) +
                            " conversion=decoded_frame_to_BGRA pixel_format=BGRA" +
                            " stride=" + std::to_string(frame_stride) +
                            " frame_bytes=" +
                            std::to_string(static_cast<std::size_t>(frame_stride) *
                                static_cast<std::size_t>(frame_height)) +
                            " timestamp_ms=" + std::to_string(encoded.timestamp_ms) +
                            " pts=" + std::to_string(decoded_frame->pts) +
                            " input_packet_bytes=" + std::to_string(encoded.data.size()) +
                            " " + pixel_sample);
                        if (decoded_count == 1 || decoded_count % 30 == 0) {
                            log_event("DECODE frame_count=" + std::to_string(decoded_count) +
                                " sequence=" + std::to_string(sequence) +
                                " resolution=" + std::to_string(frame_width) + "x" +
                                std::to_string(frame_height) +
                                " stride=" + std::to_string(frame_stride) +
                                " timestamp_ms=" + std::to_string(encoded.timestamp_ms) +
                                " input_bytes=" + std::to_string(encoded.data.size()));
                        }
                        set_error({});
                    } else {
                        set_error("FFmpeg could not convert a decoded video frame to BGRA.");
                    }
                    av_frame_unref(decoded_frame);
                }
            }
        } catch (const std::exception& exception) {
            set_error(std::string("Native video decoder failed: ") + exception.what());
            receiver.stop();
        } catch (...) {
            set_error("Native video decoder failed with an unknown error.");
            receiver.stop();
        }

        const auto stats = receiver.statistics();
        std::uint64_t decoded_frames = 0;
        {
            std::lock_guard<std::mutex> lock(frame_mutex);
            decoded_frames = frames_decoded;
        }
        log_event("DECODE decoder_stopped packets=" + std::to_string(packets_decoded) +
            " frames=" + std::to_string(decoded_frames) +
            " packets_received=" + std::to_string(stats.packets_received) +
            " bytes_received=" + std::to_string(stats.bytes_received) +
            " packets_dropped=" + std::to_string(stats.packets_dropped));
        sws_freeContext(conversion);
        av_packet_free(&packet);
        av_frame_free(&decoded_frame);
        avcodec_free_context(&codec_context);
    }
};

void copy_error(const std::string& message, char* destination, int capacity)
{
    if (destination == nullptr || capacity <= 0) {
        return;
    }
    const auto copied = std::min(message.size(), static_cast<std::size_t>(capacity - 1));
    std::memcpy(destination, message.data(), copied);
    destination[copied] = '\0';
}

} // namespace

extern "C" {

void* optitrack_rtsp_create(void)
{
    try {
        auto* receiver = new NativeReceiver();
        log_event("CONTROL create receiver");
        return receiver;
    } catch (...) {
        log_event("ERROR Could not create native RTSP receiver.");
        return nullptr;
    }
}

void optitrack_rtsp_destroy(void* handle)
{
    if (handle != nullptr) {
        log_event("CONTROL destroy requested");
    }
    delete static_cast<NativeReceiver*>(handle);
    if (handle != nullptr) {
        log_event("CONTROL destroy completed");
    }
}

int optitrack_rtsp_start(void* handle, const char* url, int use_tcp, char* error, int error_capacity)
{
    return optitrack_rtsp_start_ex(handle, url, use_tcp, 5000, 1, 1000, 30, error, error_capacity);
}

int optitrack_rtsp_start_ex(
    void* handle,
    const char* url,
    int use_tcp,
    int timeout_ms,
    int reconnect,
    int reconnect_delay_ms,
    int queue_capacity,
    char* error,
    int error_capacity)
{
    const auto safe_url = url == nullptr ? std::string("<null>") : redact_url(url);
    log_event("CONTROL start_ex url=" + safe_url +
        " transport=" + (use_tcp ? "TCP" : "UDP") +
        " timeout_ms=" + std::to_string(timeout_ms) +
        " reconnect=" + std::to_string(reconnect != 0) +
        " reconnect_delay_ms=" + std::to_string(reconnect_delay_ms) +
        " queue_capacity=" + std::to_string(queue_capacity));

    if (handle == nullptr || url == nullptr) {
        log_event("ERROR Receiver handle and RTSP URL are required.");
        copy_error("Receiver handle and RTSP URL are required.", error, error_capacity);
        return 0;
    }

    try {
        if (timeout_ms <= 0 || reconnect_delay_ms < 0 || queue_capacity <= 0) {
            log_event("ERROR Invalid RTSP receiver parameters.");
            copy_error("Timeout and queue capacity must be positive; reconnect delay cannot be negative.",
                error,
                error_capacity);
            return 0;
        }

        if (reconnect != 0 && reconnect_delay_ms == 0) {
            log_event("ERROR Reconnect delay must be positive when automatic reconnect is enabled.");
            copy_error("Reconnect delay must be positive when automatic reconnect is enabled.",
                error,
                error_capacity);
            return 0;
        }

        auto* receiver = static_cast<NativeReceiver*>(handle);
        receiver->start_options = {
            url,
            use_tcp ? optitrack::rtsp::Transport::Tcp : optitrack::rtsp::Transport::Udp,
            static_cast<std::size_t>(queue_capacity),
            std::chrono::milliseconds(timeout_ms),
            std::chrono::milliseconds(reconnect_delay_ms),
            reconnect != 0
        };
        std::string message;
        const bool started = receiver->start(message);
        copy_error(message, error, error_capacity);
        if (started) {
            const auto info = receiver->receiver.stream_info();
            log_event("CONTROL start_ex result=started codec=" + info.codec_name +
                " resolution=" + std::to_string(info.width) + "x" +
                std::to_string(info.height) +
                " stream_index=" + std::to_string(info.stream_index));
        } else {
            log_event("CONTROL start_ex result=failed error=" + message);
        }
        return started ? 1 : 0;
    } catch (const std::exception& exception) {
        log_event(std::string("ERROR start_ex exception: ") + exception.what());
        copy_error(exception.what(), error, error_capacity);
    } catch (...) {
        log_event("ERROR start_ex failed with an unknown exception.");
        copy_error("Unknown native receiver error.", error, error_capacity);
    }
    return 0;
}

void optitrack_rtsp_stop(void* handle)
{
    if (handle != nullptr) {
        auto* receiver = static_cast<NativeReceiver*>(handle);
        log_event(std::string("CONTROL stop requested running=") +
            (receiver->receiver.is_running() ? "true" : "false") +
            " connected=" + (receiver->receiver.is_connected() ? "true" : "false"));
        receiver->stop();
        log_event("CONTROL stop completed");
    }
}

int optitrack_rtsp_get_stats(void* handle, OptitrackRtspStats* stats)
{
    if (handle == nullptr || stats == nullptr) {
        return 0;
    }

    auto* receiver = static_cast<NativeReceiver*>(handle);
    const auto network_stats = receiver->receiver.statistics();
    {
        std::lock_guard<std::mutex> lock(receiver->frame_mutex);
        stats->width = receiver->width;
        stats->height = receiver->height;
        stats->frames_decoded = receiver->frames_decoded;
    }
    stats->running = receiver->receiver.is_running() ? 1 : 0;
    stats->connected = receiver->receiver.is_connected() ? 1 : 0;
    stats->packets_received = network_stats.packets_received;
    stats->bytes_received = network_stats.bytes_received;
    stats->packets_dropped = network_stats.packets_dropped;
    return 1;
}

int optitrack_rtsp_get_frame(
    void* handle,
    uint8_t* destination,
    int destination_capacity,
    int* width,
    int* height,
    int* stride,
    uint64_t* sequence,
    int64_t* timestamp_ms,
    int* required_bytes)
{
    if (handle == nullptr || width == nullptr || height == nullptr || stride == nullptr ||
        sequence == nullptr || timestamp_ms == nullptr || required_bytes == nullptr) {
        return 0;
    }

    auto* receiver = static_cast<NativeReceiver*>(handle);
    std::lock_guard<std::mutex> lock(receiver->frame_mutex);
    if (receiver->latest_frame.empty()) {
        return 0;
    }

    if (receiver->latest_frame.size() > static_cast<std::size_t>(std::numeric_limits<int>::max())) {
        return 0;
    }
    *width = receiver->width;
    *height = receiver->height;
    *stride = receiver->stride;
    *sequence = receiver->frame_sequence;
    *timestamp_ms = receiver->timestamp_ms;
    *required_bytes = static_cast<int>(receiver->latest_frame.size());
    if (destination == nullptr || destination_capacity < *required_bytes) {
        return 2;
    }

    std::memcpy(destination, receiver->latest_frame.data(), receiver->latest_frame.size());
    return 1;
}

int optitrack_rtsp_dequeue_review_frame(
    void* handle,
    uint8_t* destination,
    int destination_capacity,
    int* width,
    int* height,
    uint64_t* sequence,
    int64_t* timestamp_ms,
    int* required_bytes)
{
    if (handle == nullptr || width == nullptr || height == nullptr ||
        sequence == nullptr || timestamp_ms == nullptr || required_bytes == nullptr) {
        return 0;
    }

    auto* receiver = static_cast<NativeReceiver*>(handle);
    std::lock_guard<std::mutex> lock(receiver->review_frame_mutex);
    if (receiver->review_frames.empty()) {
        return 0;
    }

    const auto& frame = receiver->review_frames.front();
    if (frame.jpeg.size() > static_cast<std::size_t>(std::numeric_limits<int>::max())) {
        return 0;
    }
    *width = frame.width;
    *height = frame.height;
    *sequence = frame.sequence;
    *timestamp_ms = frame.timestamp_ms;
    *required_bytes = static_cast<int>(frame.jpeg.size());
    if (destination == nullptr || destination_capacity < *required_bytes) {
        return 2;
    }

    std::memcpy(destination, frame.jpeg.data(), frame.jpeg.size());
    receiver->review_queue_bytes -= frame.jpeg.size();
    receiver->review_frames.pop_front();
    return 1;
}

int optitrack_rtsp_set_review_mode(void* handle, int enabled)
{
    if (handle == nullptr) {
        return 0;
    }

    static_cast<NativeReceiver*>(handle)->set_review_mode(enabled != 0);
    return 1;
}

void optitrack_rtsp_get_last_error(void* handle, char* error, int error_capacity)
{
    if (handle == nullptr) {
        copy_error("Receiver handle is null.", error, error_capacity);
        return;
    }

    auto* receiver = static_cast<NativeReceiver*>(handle);
    std::lock_guard<std::mutex> lock(receiver->error_mutex);
    copy_error(receiver->last_error, error, error_capacity);
}

} // extern "C"
