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

const corsProbe = await guard.fetch(new Request('https://example.test/v1/education/assist', {
  method: 'OPTIONS',
  headers: {
    origin: 'https://malicious.example',
    'access-control-request-method': 'POST'
  }
}), {}, {});
assert(corsProbe.headers.get('access-control-allow-origin') === null,
  'Education API must not expose wildcard cross-origin access');
assert(corsProbe.status !== 204,
  'Education API must not advertise an OPTIONS CORS preflight success');

console.log('runtime hardening self-test passed.');
