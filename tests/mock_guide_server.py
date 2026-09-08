import json
from pathlib import Path
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer


def field(obj, name, default=None):
    if not isinstance(obj, dict):
        return default
    if name in obj:
        return obj[name]
    pascal = name[:1].upper() + name[1:]
    return obj.get(pascal, default)


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
            elements = field(payload, "elements", []) or []
            system_context = field(payload, "systemContext", {}) or {}
            foreground = str(field(system_context, "foregroundProcess", "") or "").lower()

            eligible = [
                item
                for item in elements
                if field(item, "interactable", True) is not False
                and field(item, "enabled", True) is not False
                and float(field(item, "width", 0) or 0) >= 8
                and float(field(item, "height", 0) or 0) >= 8
                and field(item, "id")
            ]

            diagnostics = {
                "payloadKeys": list(payload.keys()) if isinstance(payload, dict) else [],
                "systemContextKeys": list(system_context.keys()) if isinstance(system_context, dict) else [],
                "foreground": foreground,
                "foregroundProcessId": field(system_context, "foregroundProcessId"),
                "eligible": [
                    {
                        "id": field(item, "id"),
                        "name": field(item, "name"),
                        "automationId": field(item, "automationId"),
                        "controlType": field(item, "controlType"),
                        "processName": field(item, "processName"),
                    }
                    for item in eligible[:80]
                ],
            }
            Path("artifacts").mkdir(exist_ok=True)
            Path("artifacts/mock-last-request.json").write_text(
                json.dumps(diagnostics, ensure_ascii=False, indent=2), encoding="utf-8"
            )

            target = next(
                (
                    item
                    for item in eligible
                    if str(field(item, "automationId", "") or "").lower() == "smokebutton"
                    or str(field(item, "name", "") or "").lower() == "open test target"
                ),
                None,
            )
            if target is None:
                target = next(
                    (
                        item
                        for item in eligible
                        if str(field(item, "processName", "") or "").lower() == foreground
                    ),
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
                    "targetId": str(field(target, "id")),
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
