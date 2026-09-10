import io
import json
import struct
import time
import urllib.error
import urllib.request
import wave

BASE = 'https://helpsys.syouziroupc.workers.dev'
BROWSER_UA = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/142.0.0.0 Safari/537.36'


def raw_status(path, *, user_agent=None, timeout=20):
    headers = {'accept': 'application/json'}
    if user_agent is not None:
        headers['user-agent'] = user_agent
    req = urllib.request.Request(BASE + path, headers=headers, method='GET')
    try:
        with urllib.request.urlopen(req, timeout=timeout) as res:
            return res.status, res.read().decode('utf-8', 'replace')
    except urllib.error.HTTPError as exc:
        return exc.code, exc.read().decode('utf-8', 'replace')


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


# Reproduce the desktop/API-client signature problem first. urllib's default non-browser
# signature is intentionally retained here because Cloudflare Browser Integrity Check treats
# missing/non-standard user agents similarly.
plain_status, plain_body = raw_status('/health')
print('PRODUCTION_NON_BROWSER_HEALTH_STATUS=', plain_status)
if plain_status == 403 and ('1010' in plain_body or 'browser' in plain_body.lower()):
    print('PRODUCTION_BROWSER_INTEGRITY_BLOCK=REPRODUCED')
else:
    print('PRODUCTION_BROWSER_INTEGRITY_BLOCK=NOT_REPRODUCED')

common_headers = {
    'accept': 'application/json',
    'user-agent': BROWSER_UA,
}
status, health = request_json('/health', headers=common_headers)
if status != 200 or health.get('ok') is not True or health.get('service') != 'helpsys':
    raise RuntimeError(f'health contract failed with browser-compatible signature: HTTP {status} {health}')
print('PRODUCTION_HEALTH_WITH_BROWSER_UA=PASS', health.get('model'), health.get('visionModel'))

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
        **common_headers,
        'content-type': 'application/json',
        'x-helpsys-request-id': 'github-production-live-probe',
    },
    timeout=35,
)
if status != 200 or quality.get('status') not in {'target', 'clarify', 'done', 'not_found'}:
    raise RuntimeError(f'quality live inference contract failed: HTTP {status} {quality}')
if quality.get('error'):
    raise RuntimeError(f'quality live inference returned API error: {quality}')
print('PRODUCTION_QUALITY_INFERENCE_WITH_BROWSER_UA=PASS', quality.get('status'), quality.get('confidence'))

wav = make_silence_wav()
status, transcript = request_json(
    '/v1/transcribe',
    method='POST',
    body=wav,
    headers={
        **common_headers,
        'content-type': 'audio/wav',
        'x-helpsys-request-id': 'github-production-asr-probe',
    },
    timeout=35,
)
if status != 200 or 'text' not in transcript or 'model' not in transcript:
    raise RuntimeError(f'transcribe live contract failed: HTTP {status} {transcript}')
if transcript.get('error'):
    raise RuntimeError(f'transcribe live returned API error: {transcript}')
print('PRODUCTION_TRANSCRIBE_WITH_BROWSER_UA=PASS', transcript.get('model'), repr(transcript.get('text')))
