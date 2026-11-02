#include "OptitrackRtspReceiver/RtspReceiver.h"

#include <atomic>
#include <chrono>
#include <csignal>
#include <iomanip>
#include <iostream>
#include <string>
#include <thread>

namespace {

std::atomic<bool> stop_requested{false};

void handle_signal(int)
{
    stop_requested = true;
}

} // namespace

int main(int argc, char* argv[])
{
    if (argc < 2 || argc > 3) {
        std::cerr << "Usage: optitrack_rtsp_receiver_cli <rtsp-url> [udp|tcp]\n";
        return 2;
    }

    optitrack::rtsp::ReceiverOptions options;
    options.url = argv[1];
    if (argc == 3) {
        const std::string transport = argv[2];
        if (transport == "tcp") {
            options.transport = optitrack::rtsp::Transport::Tcp;
        } else if (transport != "udp") {
            std::cerr << "Transport must be either udp or tcp.\n";
            return 2;
        }
    }

    optitrack::rtsp::RtspReceiver receiver;
    std::string error;
    if (!receiver.start(options, error)) {
        std::cerr << "Could not start receiver: " << error << '\n';
        return 1;
    }

    const auto info = receiver.stream_info();
    std::cout << "Connected: " << info.codec_name << ' '
              << info.width << 'x' << info.height
              << ", stream " << info.stream_index << '\n'
              << "Receiving encoded packets. Press Ctrl+C to stop.\n";

    std::signal(SIGINT, handle_signal);
    std::signal(SIGTERM, handle_signal);

    auto last_report = std::chrono::steady_clock::now();
    auto previous_stats = receiver.statistics();
    std::uint64_t consumed_packets = 0;
    optitrack::rtsp::EncodedVideoPacket packet;

    while (!stop_requested && receiver.is_running()) {
        if (receiver.wait_pop(packet, std::chrono::milliseconds(250))) {
            ++consumed_packets;
        }

        const auto now = std::chrono::steady_clock::now();
        if (now - last_report >= std::chrono::seconds(1)) {
            const auto stats = receiver.statistics();
            const auto elapsed = std::chrono::duration<double>(now - last_report).count();
            const auto received_fps =
                static_cast<double>(stats.packets_received - previous_stats.packets_received) / elapsed;
            const auto received_mbps =
                static_cast<double>(stats.bytes_received - previous_stats.bytes_received) * 8.0 /
                elapsed / 1'000'000.0;

            std::cout << std::fixed << std::setprecision(1)
                      << "connected=" << (receiver.is_connected() ? "yes" : "no")
                      << " receive_fps=" << received_fps
                      << " bitrate_mbps=" << received_mbps
                      << " packets=" << stats.packets_received
                      << " consumed=" << consumed_packets
                      << " dropped=" << stats.packets_dropped;
            const auto last_error = receiver.last_error();
            if (!last_error.empty()) {
                std::cout << " last_error=\"" << last_error << '"';
            }
            std::cout << '\n';

            previous_stats = stats;
            last_report = now;
        }
    }

    receiver.stop();
    std::cout << "Receiver stopped.\n";
    return 0;
}
