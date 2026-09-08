import json
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer


class Handler(BaseHTTPRequestHandler):
    def log_message(self, _format, *_args):
        pass

    def _json(self, payload, status=200):
        body = json.dumps(payload, ensure_ascii=False).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        if self.path == "/health":
            self._json({"ok": True, "service": "helpsys-smoke-mock"})
        else:
            self._json({"error": "not_found"}, 404)

    def do_POST(self):
        try:
            length = int(self.headers.get("Content-Length", "0"))
            payload = json.loads(self.rfile.read(length) or b"{}")
        except Exception:
            self._json({"error": "invalid_json"}, 400)
            return

        if self.path == "/v1/guide":
            elements = payload.get("elements") or []
            foreground = ((payload.get("systemContext") or {}).get("foregroundProcess") or "").lower()

            eligible = [
                item
                for item in elements
                if item.get("interactable") is not False
                and item.get("enabled") is not False
                and float(item.get("width") or 0) >= 8
                and float(item.get("height") or 0) >= 8
                and item.get("id")
            ]
            target = next(
                (item for item in eligible if str(item.get("processName") or "").lower() == foreground),
                eligible[0] if eligible else None,
            )

            if target is None:
                self._json(
                    {
                        "status": "not_found",
                        "targetId": None,
                        "action": "none",
                        "instruction": "テスト対象がありません。",
                        "question": None,
                        "key": None,
                        "confidence": 0,
                    }
                )
                return

            self._json(
                {
                    "status": "target",
                    "targetId": str(target["id"]),
                    "action": "left_click",
                    "instruction": "青い枠の場所で、マウスの左ボタンを1回押してください。",
                    "question": None,
                    "key": None,
                    "confidence": 0.99,
                }
            )
            return

        if self.path == "/v1/vision-guide":
            self._json(
                {
                    "status": "not_found",
                    "label": None,
                    "instruction": "",
                    "question": None,
                    "x": 0,
                    "y": 0,
                    "width": 0,
                    "height": 0,
                    "confidence": 0,
                }
            )
            return

        self._json({"error": "not_found"}, 404)


if __name__ == "__main__":
    ThreadingHTTPServer(("127.0.0.1", 8765), Handler).serve_forever()
