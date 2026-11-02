#pragma once

#include <chrono>
#include <condition_variable>
#include <cstddef>
#include <cstdint>
#include <deque>
#include <mutex>
#include <string>
#include <thread>
#include <vector>
#include <atomic>

namespace optitrack::rtsp {

enum class Transport {
    Udp,
    Tcp
};

struct ReceiverOptions {
    std::string url;
    Transport transport = Transport::Udp;
    std::size_t queue_capacity = 30;
    std::chrono::milliseconds network_timeout{5000};
    std::chrono::milliseconds reconnect_delay{1000};
    bool reconnect = true;
};

struct EncodedVideoPacket {
    std::vector<std::uint8_t> data;
    std::int64_t timestamp_ms = 0;
    std::int64_t pts = 0;
    std::int64_t dts = 0;
    std::int64_t duration = 0;
    int stream_index = -1;
    int codec_id = 0;
    int width = 0;
    int height = 0;
    bool key_frame = false;
};

struct ReceiverStatistics {
    std::uint64_t packets_received = 0;
    std::uint64_t bytes_received = 0;
    std::uint64_t packets_dropped = 0;
};

struct StreamInfo {
    std::string codec_name;
    int width = 0;
    int height = 0;
    int stream_index = -1;
};

class RtspReceiver final {
public:
    RtspReceiver() = default;
    ~RtspReceiver();

    RtspReceiver(const RtspReceiver&) = delete;
    RtspReceiver& operator=(const RtspReceiver&) = delete;

    bool start(const ReceiverOptions& options, std::string& error);
    void stop();

    bool try_pop(EncodedVideoPacket& packet);
    bool wait_pop(EncodedVideoPacket& packet, std::chrono::milliseconds timeout);

    bool is_running() const noexcept;
    bool is_connected() const noexcept;
    ReceiverStatistics statistics() const noexcept;
    StreamInfo stream_info() const;
    std::string last_error() const;

private:
    struct OpenedStream;

    static int interrupt_callback(void* opaque);
    bool open_stream(OpenedStream& stream, std::string& error);
    void receive_loop(OpenedStream stream);
    void enqueue(EncodedVideoPacket packet);
    void set_error(std::string error);
    bool wait_for_reconnect_delay();
    void stop_locked();

    mutable std::mutex lifecycle_mutex_;
    mutable std::mutex queue_mutex_;
    mutable std::mutex state_mutex_;
    std::condition_variable queue_changed_;
    std::condition_variable reconnect_changed_;
    std::deque<EncodedVideoPacket> queue_;
    std::thread worker_;
    ReceiverOptions options_;
    StreamInfo stream_info_;
    std::string last_error_;
    std::atomic<bool> stop_requested_{true};
    std::atomic<bool> connected_{false};
    std::atomic<bool> running_{false};
    std::atomic<std::uint64_t> cancellation_generation_{0};
    std::atomic<std::uint64_t> active_generation_{0};
    std::atomic<std::uint64_t> packets_received_{0};
    std::atomic<std::uint64_t> bytes_received_{0};
    std::atomic<std::uint64_t> packets_dropped_{0};
};

} // namespace optitrack::rtsp
