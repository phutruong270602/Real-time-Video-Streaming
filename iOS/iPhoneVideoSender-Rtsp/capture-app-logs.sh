#!/bin/bash
set -euo pipefail

if [[ $# -ne 1 ]]; then
    echo "Usage: bash $(basename "$0") <iPhone name or UDID>" >&2
    exit 2
fi

device="$1"
bundle_id="com.example.iPhoneVideoSender.rtsp"
source_path="Documents/iPhoneVideoSender.log"
script_dir="$(cd "$(dirname "$0")" && pwd)"
output_dir="$script_dir/log"
output_file="$output_dir/capture-app-logs.log"
temp_dir="$(mktemp -d)"
snapshot="$temp_dir/app.log"
error_file="$temp_dir/devicectl-error.log"
devices_file="$temp_dir/devices.json"
last_size=0
reported_unavailable=false

cleanup() {
    rm -f "$snapshot" "$error_file" "$devices_file"
    rmdir "$temp_dir"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

mkdir -p "$output_dir"
printf '\n--- RTSP app log capture started: %s | device: %s ---\n' \
    "$(date -u '+%Y-%m-%dT%H:%M:%SZ')" "$device" >> "$output_file"
exec > >(tee -a "$output_file") 2>&1

if ! command -v xcrun >/dev/null 2>&1; then
    echo "ERROR: xcrun was not found. Run this script on the Mac with Xcode installed."
    exit 1
fi

if ! command -v python3 >/dev/null 2>&1; then
    echo "ERROR: python3 was not found; cannot resolve the connected iPhone."
    exit 1
fi

if ! xcrun devicectl list devices --json-output "$devices_file" > /dev/null 2> "$error_file"; then
    echo "ERROR: Could not list Xcode devices:"
    cat "$error_file"
    exit 1
fi

resolved_device="$(
    python3 - "$devices_file" "$device" <<'PY'
import json
import sys

path, requested = sys.argv[1:3]
with open(path, encoding="utf-8") as file:
    devices = json.load(file)["result"]["devices"]

def is_connected(device):
    return (
        device.get("connectionProperties", {}).get("tunnelState") == "connected"
        and device.get("hardwareProperties", {}).get("platform") == "iOS"
        and device.get("hardwareProperties", {}).get("deviceType") == "iPhone"
    )

def identifiers(device):
    properties = device.get("deviceProperties", {})
    hardware = device.get("hardwareProperties", {})
    connection = device.get("connectionProperties", {})
    return {
        properties.get("name", ""),
        hardware.get("udid", ""),
        device.get("identifier", ""),
        *connection.get("potentialHostnames", []),
    }

matches = [item for item in devices if requested in identifiers(item)]
connected_matches = [item for item in matches if is_connected(item)]
if len(connected_matches) == 1:
    print(connected_matches[0]["identifier"])
    sys.exit(0)
if len(connected_matches) > 1:
    print("The requested device name matches multiple connected iPhones.", file=sys.stderr)
    sys.exit(1)

connected_phones = [item for item in devices if is_connected(item)]
if len(connected_phones) == 1:
    selected = connected_phones[0]
    name = selected.get("deviceProperties", {}).get("name", selected["identifier"])
    print(f"Requested device '{requested}' is not connected; using the only connected iPhone '{name}'.", file=sys.stderr)
    print(selected["identifier"])
    sys.exit(0)

if not connected_phones:
    print(f"No connected iPhone found for '{requested}'. Connect/unlock the phone and check `xcrun devicectl list devices`.", file=sys.stderr)
else:
    names = [
        f"{item.get('deviceProperties', {}).get('name', 'iPhone')} ({item['identifier']})"
        for item in connected_phones
    ]
    print(
        f"'{requested}' is unavailable and multiple iPhones are connected. Pass one of: {', '.join(names)}",
        file=sys.stderr,
    )
sys.exit(1)
PY
)" 2> "$error_file" || {
    echo "ERROR: Could not select an iPhone:"
    cat "$error_file"
    exit 1
}

if [[ -s "$error_file" ]]; then
    cat "$error_file"
fi
device="$resolved_device"
echo "Appending RTSP app logs to $output_file. Press Ctrl-C to stop."
echo "Resolved device identifier: $device"
echo "Source: $bundle_id / $source_path"

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
                echo "App log file restarted; resuming capture from the beginning."
                last_size=0
            fi
            if (( current_size > last_size )); then
                tail -c "+$((last_size + 1))" "$snapshot" >> "$output_file"
                echo "Captured app log bytes $((last_size + 1))-$current_size."
                last_size="$current_size"
            fi
        fi
        if [[ "$reported_unavailable" == true ]]; then
            echo "Connected to RTSP app log on $device."
            reported_unavailable=false
        fi
    else
        if [[ "$reported_unavailable" == false ]]; then
            echo "Waiting for RTSP app log on $device:"
            if [[ -s "$error_file" ]]; then
                cat "$error_file"
            else
                echo "No app log file is available yet. Open the RTSP sender app on the iPhone."
            fi
            reported_unavailable=true
        fi
    fi
    sleep 2
done
