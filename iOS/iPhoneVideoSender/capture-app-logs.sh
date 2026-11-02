#!/bin/bash
set -euo pipefail

if [[ $# -ne 1 ]]; then
    echo "Usage: bash $(basename "$0") <iPhone name or UDID>" >&2
    exit 2
fi

device="$1"
bundle_id="com.example.iPhoneVideoSender"
source_path="Documents/iPhoneVideoSender.log"
script_dir="$(cd "$(dirname "$0")" && pwd)"
output_dir="$script_dir/log"
output_file="$output_dir/capture-app-logs.log"
temp_dir="$(mktemp -d)"
snapshot="$temp_dir/app.log"
error_file="$temp_dir/devicectl-error.log"
last_size=0
reported_unavailable=false

cleanup() {
    rm -rf "$temp_dir"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

mkdir -p "$output_dir"
printf '\n--- WebRTC app log capture started: %s | device: %s ---\n' \
    "$(date -u '+%Y-%m-%dT%H:%M:%SZ')" "$device" >> "$output_file"
echo "Appending WebRTC app logs to $output_file. Press Ctrl-C to stop."

while true; do
    rm -f "$snapshot" "$error_file"
    if xcrun devicectl device copy from \
        --device "$device" \
        --domain-type appDataContainer \
        --domain-identifier "$bundle_id" \
        --source "$source_path" \
        --destination "$snapshot" \
        > /dev/null 2> "$error_file"; then
        if [[ -f "$snapshot" ]]; then
            current_size="$(wc -c < "$snapshot" | tr -d '[:space:]')"
            if (( current_size < last_size )); then
                printf '\n--- App log file restarted: %s ---\n' \
                    "$(date -u '+%Y-%m-%dT%H:%M:%SZ')" >> "$output_file"
                last_size=0
            fi
            if (( current_size > last_size )); then
                tail -c "+$((last_size + 1))" "$snapshot" >> "$output_file"
                last_size="$current_size"
            fi
        fi
        if [[ "$reported_unavailable" == true ]]; then
            echo "Connected to WebRTC app log on $device."
            reported_unavailable=false
        fi
    else
        if [[ "$reported_unavailable" == false ]]; then
            echo "Waiting for WebRTC app log on $device:" >&2
            cat "$error_file" >&2
            reported_unavailable=true
        fi
    fi
    sleep 2
done
