#include "Logger.h"

#include <chrono>
#include <cstdio>
#include <ctime>
#include <filesystem>
#include <fstream>
#include <iomanip>
#include <mutex>
#include <stdexcept>
#include <thread>

#ifdef _WIN32
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#endif

#ifndef OPTITRACK_RTSP_CPP_LOG_FILE
#define OPTITRACK_RTSP_CPP_LOG_FILE "log/rtsp_receiver_cpp.log"
#endif

namespace optitrack::rtsp::detail {
namespace {

void log_message(const char* level, const std::string& message) noexcept
{
    static std::mutex log_mutex;
    static bool failure_reported = false;
    std::lock_guard<std::mutex> lock(log_mutex);

    try {
        static std::ofstream output = [] {
            const std::filesystem::path path(OPTITRACK_RTSP_CPP_LOG_FILE);
            if (!path.parent_path().empty()) {
                std::filesystem::create_directories(path.parent_path());
            }
            return std::ofstream(path, std::ios::out | std::ios::app);
        }();
        if (!output) {
            throw std::runtime_error("Could not open C++ RTSP receiver log file.");
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
               << " [" << level << "] [thread " << std::this_thread::get_id()
               << "] " << message << '\n';
        output.flush();
        if (!output) {
            throw std::runtime_error("Could not write C++ RTSP receiver log file.");
        }
    } catch (const std::exception& exception) {
        if (!failure_reported) {
            failure_reported = true;
#ifdef _WIN32
            const auto error = std::string("Optitrack C++ RTSP log error: ") +
                exception.what() + "\n";
            OutputDebugStringA(error.c_str());
#else
            std::fprintf(stderr, "Optitrack C++ RTSP log error: %s\n", exception.what());
#endif
        }
    } catch (...) {
        if (!failure_reported) {
            failure_reported = true;
#ifdef _WIN32
            OutputDebugStringA("Optitrack C++ RTSP log error: unknown failure.\n");
#else
            std::fprintf(stderr, "Optitrack C++ RTSP log error: unknown failure.\n");
#endif
        }
    }
}

} // namespace

void log_info(const std::string& message) noexcept
{
    log_message("INFO", message);
}

void log_warning(const std::string& message) noexcept
{
    log_message("WARN", message);
}

void log_error(const std::string& message) noexcept
{
    log_message("ERROR", message);
}

} // namespace optitrack::rtsp::detail
