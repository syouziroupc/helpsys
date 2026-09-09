import guard from './reliability-v4-guard.js';

function assert(condition, message) {
  if (!condition) throw new Error(message);
}

function deniedLimiter(counter) {
  return {
    async limit() {
      counter.calls++;
      return { success: false };
    }
  };
}

async function expectRateLimited(path, init, bindingName) {
  const counter = { calls: 0 };
  let aiCalls = 0;
  const env = {
    AI: { async run() { aiCalls++; throw new Error('AI must not run when rate-limited'); } },
    [bindingName]: deniedLimiter(counter)
  };
  const request = new Request(`https://example.test${path}`, init);
  const response = await guard.fetch(request, env, {});
  assert(response.status === 429, `${path} should return 429 before inference`);
  const body = await response.json();
  assert(body.error === 'rate_limited', `${path} should return rate_limited`);
  assert(counter.calls === 1, `${path} should consume one limiter check`);
  assert(aiCalls === 0, `${path} must not call AI after limiter rejection`);
}

await expectRateLimited('/v1/quality-guide', {
  method: 'POST',
  headers: { 'content-type': 'application/json', 'cf-connecting-ip': '203.0.113.20' },
  body: JSON.stringify({ request: '電卓を開いて' })
}, 'GUIDE_RATE_LIMITER');

await expectRateLimited('/v1/guide', {
  method: 'POST',
  headers: { 'content-type': 'application/json', 'cf-connecting-ip': '203.0.113.21' },
  body: JSON.stringify({ request: '電卓を開いて' })
}, 'GUIDE_RATE_LIMITER');

await expectRateLimited('/v1/transcribe', {
  method: 'POST',
  headers: { 'content-type': 'audio/wav', 'cf-connecting-ip': '203.0.113.22' },
  body: new Uint8Array([82, 73, 70, 70])
}, 'ASR_RATE_LIMITER');

await expectRateLimited('/v1/education/assist', {
  method: 'POST',
  headers: { 'content-type': 'application/json', 'cf-connecting-ip': '203.0.113.23' },
  body: JSON.stringify({ stage: 'practice', lessonId: 'x', lessonTitle: 'x', objective: 'x', message: 'x' })
}, 'EDUCATION_RATE_LIMITER');

for (const path of ['/v1/quality-guide', '/v1/guide', '/v1/vision-guide', '/v1/education/assist']) {
  let aiCalls = 0;
  const response = await guard.fetch(new Request(`https://example.test${path}`, {
    method: 'POST',
    headers: { 'content-type': 'text/plain', origin: 'https://malicious.example' },
    body: JSON.stringify({ request: '電卓を開いて' })
  }), { AI: { async run() { aiCalls++; } } }, {});
  assert(response.status === 415, `${path} must reject browser-simple text/plain POST`);
  assert(aiCalls === 0, `${path} must reject invalid media type before AI`);
  assert(response.headers.get('access-control-allow-origin') === null,
    `${path} rejection must not opt into cross-origin access`);
}

for (const path of ['/v1/quality-guide', '/v1/guide', '/v1/vision-guide', '/v1/education/assist', '/v1/transcribe']) {
  const corsProbe = await guard.fetch(new Request(`https://example.test${path}`, {
    method: 'OPTIONS',
    headers: {
      origin: 'https://malicious.example',
      'access-control-request-method': 'POST'
    }
  }), {}, {});
  assert(corsProbe.status === 404, `${path} must reject browser CORS preflight`);
  assert(corsProbe.headers.get('access-control-allow-origin') === null,
    `${path} must not expose wildcard cross-origin access`);
}

let oversizedAiCalls = 0;
const oversized = await guard.fetch(new Request('https://example.test/v1/education/assist', {
  method: 'POST',
  headers: { 'content-type': 'application/json', 'content-length': '50000' },
  body: '{}'
}), { AI: { async run() { oversizedAiCalls++; } } }, {});
assert(oversized.status === 413, 'oversized Education request must be rejected before parsing/inference');
assert(oversizedAiCalls === 0, 'oversized Education request must not reach AI');

console.log('runtime hardening self-test passed.');
