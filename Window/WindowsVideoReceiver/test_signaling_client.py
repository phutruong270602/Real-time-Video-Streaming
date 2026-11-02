import json
import time

try:
    import websocket
except ImportError as exc:
    raise SystemExit(
        "Thiếu thư viện websocket-client. Cài đặt: pip install websocket-client"
    ) from exc

SERVER_URL = "ws://localhost:8080"


def on_message(ws, message):
    print(f"[RECV] {message}")
    if message and 'STATUS' in message:
        print("[INFO] Đã nhận status từ server")


def on_open(ws):
    print(f"[CONNECT] Kết nối tới {SERVER_URL}")
    payload = {"type": "STATUS", "state": "connected"}
    ws.send(json.dumps(payload))
    time.sleep(0.5)
    ws.send(json.dumps({"type": "CAPABILITIES", "resolutions": ["1920x1080"], "fps": [30, 60]}))


def main():
    ws = websocket.WebSocketApp(
        SERVER_URL,
        on_open=on_open,
        on_message=on_message,
        on_error=lambda ws, err: print(f"[ERROR] {err}"),
        on_close=lambda ws, close_status_code, close_msg: print("[CLOSE] Kết nối đóng"),
    )
    print("[INFO] Bắt đầu test signaling client")
    ws.run_forever()


if __name__ == "__main__":
    main()
