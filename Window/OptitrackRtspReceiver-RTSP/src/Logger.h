#pragma once

#include <string>

namespace optitrack::rtsp::detail {

void log_info(const std::string& message) noexcept;
void log_warning(const std::string& message) noexcept;
void log_error(const std::string& message) noexcept;

} // namespace optitrack::rtsp::detail
