import io
import json
import struct
import time
import urllib.error
import urllib.request
import wave

BASE = 'https://helpsys.syouziroupc.workers.dev'


def request_json(path, *, method='GET', body=None, headers=None, timeout=30):
    last = None
    for attempt in range(4):
        try:
            req = urllib.request.Request(
                BASE + path,
                data=body,
                headers=headers or {},
                method=method,
            )
            with urllib.request.urlopen(req, timeout=timeout) as res:
                text = res.read().decode('utf-8', 'replace')
                return res.status, json.loads(text)
        except urllib.error.HTTPError as exc:
            detail = exc.read().decode('utf-8', 'replace')[:1200]
            last = RuntimeError(f'{path} HTTP {exc.code}: {detail}')
        except (urllib.error.URLError, TimeoutError, ValueError, json.JSONDecodeError) as exc:
            last = exc
        if attempt < 3:
            time.sleep(3)
    raise RuntimeError(f'{path} failed after retries: {last}')


def make_silence_wav(seconds=1.0, sample_rate=16000):
    frames = int(seconds * sample_rate)
    buf = io.BytesIO()
    with wave.open(buf, 'wb') as wav:
        wav.setnchannels(1)
        wav.setsampwidth(2)
        wav.setframerate(sample_rate)
        wav.writeframes(struct.pack('<' + 'h' * frames, *([0] * frames)))
    return buf.getvalue()


status, health = request_json('/health')
if status != 200 or health.get('ok') is not True or health.get('service') != 'helpsys':
    raise RuntimeError(f'health contract failed: HTTP {status} {health}')
print('PRODUCTION_HEALTH=PASS', health.get('model'), health.get('visionModel'))

quality_payload = {
    'request': '画面を確認し、操作対象がなければ推測せずnot_foundにしてください。',
    'history': [],
    'systemContext': {
        'foregroundProcess': 'explorer',
        'foregroundProcessId': 1,
        'foregroundTitle': 'Desktop',
        'taskbarVisible': True,
        'runningApps': [],
        'browser': None,
    },
    'evidence': {
        'screenshotAvailable': True,
        'uiAutomationCount': 0,
        'focusedElementIds': [],
        'foregroundProcess': 'explorer',
        'foregroundTitle': 'Desktop',
        'browserUrl': None,
        'browserDomain': None,
        'browserAddressFieldFocused': False,
        'taskbarVisible': True,
        'runningApps': [],
        'recentTargetIds': [],
        'sources': ['screenshot', 'foreground-window'],
    },
    'recoveryMode': False,
    'routeIssue': None,
    'elements': [],
    'image': 'data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=',
    'imageWidth': 1,
    'imageHeight': 1,
}
quality_body = json.dumps(quality_payload, ensure_ascii=False).encode('utf-8')
status, quality = request_json(
    '/v1/quality-guide',
    method='POST',
    body=quality_body,
    headers={
        'content-type': 'application/json',
        'accept': 'application/json',
        'user-agent': 'HelpSys-production-live-probe/1.0',
        'x-helpsys-request-id': 'github-production-live-probe',
    },
    timeout=35,
)
if status != 200 or quality.get('status') not in {'target', 'clarify', 'done', 'not_found'}:
    raise RuntimeError(f'quality live inference contract failed: HTTP {status} {quality}')
if quality.get('error'):
    raise RuntimeError(f'quality live inference returned API error: {quality}')
print('PRODUCTION_QUALITY_INFERENCE=PASS', quality.get('status'), quality.get('confidence'))

wav = make_silence_wav()
status, transcript = request_json(
    '/v1/transcribe',
    method='POST',
    body=wav,
    headers={
        'content-type': 'audio/wav',
        'accept': 'application/json',
        'user-agent': 'HelpSys-production-live-probe/1.0',
        'x-helpsys-request-id': 'github-production-asr-probe',
    },
    timeout=35,
)
if status != 200 or 'text' not in transcript or 'model' not in transcript:
    raise RuntimeError(f'transcribe live contract failed: HTTP {status} {transcript}')
if transcript.get('error'):
    raise RuntimeError(f'transcribe live returned API error: {transcript}')
print('PRODUCTION_TRANSCRIBE=PASS', transcript.get('model'), repr(transcript.get('text')))
