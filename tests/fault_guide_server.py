import json
import time
from pathlib import Path
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

MODE_FILE = Path('artifacts/fault-mode.txt')

class Handler(BaseHTTPRequestHandler):
    protocol_version = 'HTTP/1.1'
    def log_message(self, _format, *_args):
        pass

    def _send(self, status, body, content_type='application/json'):
        raw = body.encode('utf-8')
        self.send_response(status)
        self.send_header('Content-Type', content_type)
        self.send_header('Content-Length', str(len(raw)))
        self.end_headers()
        try:
            self.wfile.write(raw)
        except (BrokenPipeError, ConnectionResetError):
            pass

    def do_GET(self):
        if self.path == '/health':
            self._send(200, '{"ok":true}')
        else:
            self._send(404, '{"error":"not_found"}')

    def do_POST(self):
        length = int(self.headers.get('Content-Length', '0') or '0')
        if length:
            self.rfile.read(length)
        mode = MODE_FILE.read_text(encoding='utf-8').strip() if MODE_FILE.exists() else 'http500'
        if mode == 'invalidjson':
            self._send(200, '{broken-json', 'application/json')
            return
        if mode == 'slow':
            time.sleep(12)
            self._send(503, json.dumps({'error': 'delayed_failure'}))
            return
        self._send(500, json.dumps({'error': 'injected_failure'}))

if __name__ == '__main__':
    Path('artifacts').mkdir(exist_ok=True)
    ThreadingHTTPServer(('127.0.0.1', 8766), Handler).serve_forever()
