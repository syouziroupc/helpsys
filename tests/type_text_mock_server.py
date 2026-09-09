import json
from pathlib import Path
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

PORT = 8767
COUNT = Path('artifacts/type-text-request-count.txt')


def field(obj, name, default=None):
    if not isinstance(obj, dict):
        return default
    if name in obj:
        return obj[name]
    pascal = name[:1].upper() + name[1:]
    return obj.get(pascal, default)


class Handler(BaseHTTPRequestHandler):
    protocol_version = 'HTTP/1.1'

    def log_message(self, _format, *_args):
        pass

    def send_json(self, value, status=200):
        raw = json.dumps(value, ensure_ascii=False).encode('utf-8')
        self.send_response(status)
        self.send_header('content-type', 'application/json; charset=utf-8')
        self.send_header('content-length', str(len(raw)))
        self.end_headers()
        self.wfile.write(raw)

    def do_POST(self):
        length = int(self.headers.get('content-length', '0') or 0)
        raw = self.rfile.read(length) if length else b'{}'
        try:
            body = json.loads(raw)
        except Exception:
            self.send_json({'error': 'invalid_json'}, 400)
            return

        if self.path != '/v1/quality-guide':
            self.send_json({'error': 'not_found'}, 404)
            return

        Path('artifacts').mkdir(exist_ok=True)
        count = int(COUNT.read_text(encoding='utf-8') or '0') if COUNT.exists() else 0
        count += 1
        COUNT.write_text(str(count), encoding='utf-8')
        Path('artifacts/type-text-last-request.json').write_text(
            json.dumps(body, ensure_ascii=False, indent=2), encoding='utf-8')

        history = field(body, 'history', []) or []
        if any(str(field(item, 'action', '')).lower() == 'type_text' for item in history):
            self.send_json({
                'status': 'done', 'targetId': None, 'action': 'none',
                'instruction': '入力操作を確認しました。', 'question': None, 'key': None,
                'confidence': 0.99, 'x': 0, 'y': 0, 'width': 0, 'height': 0,
                'screenConfirmed': True, 'visualEvidence': '入力欄の状態変化を確認しました。'
            })
            return

        target = None
        for item in field(body, 'elements', []) or []:
            if str(field(item, 'automationId', '')) == 'ProbeInput':
                target = item
                break
        if target is None:
            self.send_json({
                'status': 'not_found', 'targetId': None, 'action': 'none',
                'instruction': 'ProbeInputがありません。', 'question': None, 'key': None,
                'confidence': 0, 'x': 0, 'y': 0, 'width': 0, 'height': 0,
                'screenConfirmed': False, 'visualEvidence': ''
            })
            return

        self.send_json({
            'status': 'target', 'targetId': str(field(target, 'id')), 'action': 'type_text',
            'instruction': 'キーボードで「EXPECTED-INPUT-42」と入力し、最後に「Enter」と書かれたキーを1回押してください。',
            'question': None, 'key': 'Enter', 'confidence': 0.99,
            'x': 0, 'y': 0, 'width': 0, 'height': 0,
            'screenConfirmed': True, 'visualEvidence': '入力欄が現在画面に見えます。'
        })


if __name__ == '__main__':
    ThreadingHTTPServer(('127.0.0.1', PORT), Handler).serve_forever()
