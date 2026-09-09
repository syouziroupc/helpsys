import base64
import json
from pathlib import Path
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

COUNT = Path("artifacts/mock-request-count.txt")


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


def next_count():
    Path("artifacts").mkdir(exist_ok=True)
    count = int(COUNT.read_text(encoding="utf-8") or "0") if COUNT.exists() else 0
    count += 1
    COUNT.write_text(str(count), encoding="utf-8")
    return count


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
            request_count = next_count()
            system_context = field(payload, "systemContext", {}) or {}
            browser_context = field(system_context, "browser", {}) or {}
            foreground = str(field(system_context, "foregroundProcess", "") or "").lower()
            eligible = eligible_elements(payload)
            image = str(field(payload, "image", "") or "")
            has_image = image.startswith("data:image/png;base64,")
            history = field(payload, "history", []) or []

            diagnostics = {
                "requestCount": request_count,
                "path": self.path,
                "payloadKeys": list(payload.keys()) if isinstance(payload, dict) else [],
                "systemContextKeys": list(system_context.keys()) if isinstance(system_context, dict) else [],
                "foreground": foreground,
                "foregroundTitle": field(system_context, "foregroundTitle"),
                "foregroundProcessId": field(system_context, "foregroundProcessId"),
                "browserWindowTitle": field(browser_context, "windowTitle"),
                "browserDomain": field(browser_context, "domain"),
                "captureBounds": field(payload, "captureBounds"),
                "hasScreenshot": has_image,
                "imageWidth": field(payload, "imageWidth"),
                "imageHeight": field(payload, "imageHeight"),
                "recoveryMode": field(payload, "recoveryMode", False) is True,
                "routeIssue": field(payload, "routeIssue"),
                "history": [
                    {
                        "step": field(item, "step"),
                        "action": field(item, "action"),
                        "targetName": field(item, "targetName"),
                        "instruction": field(item, "instruction"),
                    }
                    for item in history[-12:]
                ],
                "eligible": [
                    {
                        "id": field(item, "id"),
                        "name": field(item, "name"),
                        "automationId": field(item, "automationId"),
                        "controlType": field(item, "controlType"),
                        "processName": field(item, "processName"),
                        "value": field(item, "value"),
                        "focused": field(item, "focused"),
                        "toggleState": field(item, "toggleState"),
                        "selected": field(item, "selected"),
                        "expandCollapseState": field(item, "expandCollapseState"),
                        "x": field(item, "x"),
                        "y": field(item, "y"),
                        "width": field(item, "width"),
                        "height": field(item, "height"),
                    }
                    for item in eligible[:120]
                ],
            }
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

            # A same-process modal is intentionally preferred over a control behind it. This gives
            # the desktop smoke test a deterministic recovery target when the modal is present.
            target = next(
                (
                    item
                    for item in eligible
                    if str(field(item, "automationId", "") or "").lower() == "closemodalbutton"
                ),
                None,
            )
            if target is None:
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
                            "inputText": None,
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
                            "inputText": None,
                            "confidence": 0,
                        }
                    )
                return

            target_name = str(field(target, "name", "") or "")
            if self.path == "/v1/quality-guide":
                self._json(
                    {
                        "status": "target",
                        "targetId": str(field(target, "id")),
                        "action": "left_click",
                        "instruction": f"青い枠の{target_name or '場所'}で、マウスの左ボタンを1回押してください。",
                        "question": None,
                        "key": None,
                        "inputText": None,
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
                    "instruction": f"青い枠の{target_name or '場所'}で、マウスの左ボタンを1回押してください。",
                    "question": None,
                    "key": None,
                    "inputText": None,
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
