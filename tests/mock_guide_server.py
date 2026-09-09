import base64
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


def eligible_elements(payload):
    elements = field(payload, "elements", []) or []
    return [
        item
        for item in elements
        if field(item, "interactable", True) is not False
        and field(item, "enabled", True) is not False
        and float(field(item, "width", 0) or 0) >= 8
        and float(field(item, "height", 0) or 0) >= 8
        and field(item, "id")
    ]


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, _format, *_args):
        pass

    def _json(self, payload, status=200):
        body = json.dumps(payload, ensure_ascii=False).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def _read_body(self):
        transfer_encoding = (self.headers.get("Transfer-Encoding") or "").lower()
        if "chunked" in transfer_encoding:
            chunks = []
            while True:
                size_line = self.rfile.readline().strip()
                if not size_line:
                    continue
                size = int(size_line.split(b";", 1)[0], 16)
                if size == 0:
                    while True:
                        trailer = self.rfile.readline()
                        if trailer in (b"\r\n", b"\n", b""):
                            break
                    break
                chunks.append(self.rfile.read(size))
                ending = self.rfile.read(2)
                if ending != b"\r\n":
                    raise ValueError("invalid chunk terminator")
            return b"".join(chunks)

        length = int(self.headers.get("Content-Length", "0"))
        return self.rfile.read(length) if length > 0 else b""

    def do_GET(self):
        if self.path == "/health":
            self._json({"ok": True, "service": "helpsys-smoke-mock"})
        else:
            self._json({"error": "not_found"}, 404)

    def do_POST(self):
        try:
            raw = self._read_body()
            payload = json.loads(raw or b"{}")
        except Exception:
            self._json({"error": "invalid_json"}, 400)
            return

        if self.path in ("/v1/guide", "/v1/quality-guide"):
            system_context = field(payload, "systemContext", {}) or {}
            foreground = str(field(system_context, "foregroundProcess", "") or "").lower()
            eligible = eligible_elements(payload)
            image = str(field(payload, "image", "") or "")
            has_image = image.startswith("data:image/png;base64,")

            diagnostics = {
                "path": self.path,
                "payloadKeys": list(payload.keys()) if isinstance(payload, dict) else [],
                "systemContextKeys": list(system_context.keys()) if isinstance(system_context, dict) else [],
                "foreground": foreground,
                "foregroundProcessId": field(system_context, "foregroundProcessId"),
                "hasScreenshot": has_image,
                "imageWidth": field(payload, "imageWidth"),
                "imageHeight": field(payload, "imageHeight"),
                "recoveryMode": field(payload, "recoveryMode", False) is True,
                "routeIssue": field(payload, "routeIssue"),
                "eligible": [
                    {
                        "id": field(item, "id"),
                        "name": field(item, "name"),
                        "automationId": field(item, "automationId"),
                        "controlType": field(item, "controlType"),
                        "processName": field(item, "processName"),
                        "value": field(item, "value"),
                        "focused": field(item, "focused"),
                        "x": field(item, "x"),
                        "y": field(item, "y"),
                        "width": field(item, "width"),
                        "height": field(item, "height"),
                    }
                    for item in eligible[:80]
                ],
            }
            Path("artifacts").mkdir(exist_ok=True)
            Path("artifacts/mock-last-request.json").write_text(
                json.dumps(diagnostics, ensure_ascii=False, indent=2), encoding="utf-8"
            )
            if has_image:
                try:
                    Path("artifacts/mock-last-image.png").write_bytes(base64.b64decode(image.split(",", 1)[1], validate=True))
                except Exception:
                    pass

            if self.path == "/v1/quality-guide" and not has_image:
                self._json({"error": "missing_screenshot"}, 400)
                return

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
                if self.path == "/v1/quality-guide":
                    self._json(
                        {
                            "status": "not_found",
                            "targetId": None,
                            "action": "none",
                            "instruction": "テスト対象がありません。",
                            "question": None,
                            "key": None,
                            "confidence": 0,
                            "x": 0,
                            "y": 0,
                            "width": 0,
                            "height": 0,
                            "screenConfirmed": False,
                            "visualEvidence": "",
                        }
                    )
                else:
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

            if self.path == "/v1/quality-guide":
                self._json(
                    {
                        "status": "target",
                        "targetId": str(field(target, "id")),
                        "action": "left_click",
                        "instruction": "青い枠の場所で、マウスの左ボタンを1回押してください。",
                        "question": None,
                        "key": None,
                        "confidence": 0.99,
                        "x": 0,
                        "y": 0,
                        "width": 0,
                        "height": 0,
                        "screenConfirmed": True,
                        "visualEvidence": "テスト対象のボタンが現在画面に見えます。",
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
