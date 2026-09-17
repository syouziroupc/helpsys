import worker from '../worker/stable-gemini.js';

function assert(condition, message) {
  if (!condition) throw new Error(message);
}

const image = 'data:image/jpeg;base64,/9j/4AAQSkZJRgABAQAAAQABAAD/2Q==';
const controls = [{ id: 'u1', name: 'メモ帳', controlType: 'ControlType.ListItem', enabled: true, focused: false, keyboardFocusable: true, x: 50, y: 60, width: 100, height: 80 }];
let upstreamCalls = 0;
let lastUrl = '';
let lastBody = null;
let nextPlan = {
  status: 'target', action: 'double_click', instruction: '「メモ帳」を2回クリックしてください。',
  question: null, targetId: 'u1', key: null, confidence: 0.96,
  x: 50, y: 60, width: 100, height: 80
};

const originalFetch = globalThis.fetch;
globalThis.fetch = async (url, options) => {
  upstreamCalls++;
  lastUrl = String(url);
  lastBody = JSON.parse(options.body);
  return new Response(JSON.stringify({
    candidates: [{ content: { parts: [{ text: JSON.stringify(nextPlan) }] } }]
  }), { status: 200, headers: { 'content-type': 'application/json' } });
};

try {
  const request = new Request('https://stable.test/v1/plan', {
    method: 'POST', headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ goal: 'メモ帳を開く', processName: 'explorer', windowTitle: 'Desktop', browserDomain: null, controls, image })
  });
  const response = await worker.fetch(request, { GEMINI_API_KEY: 'test-key' });
  const body = await response.json();
  assert(response.status === 200, `valid plan failed: ${response.status} ${JSON.stringify(body)}`);
  assert(body.targetId === 'u1' && body.action === 'double_click', 'valid current target must pass');
  assert(upstreamCalls === 1, 'one HelpSys plan must make exactly one Gemini request');
  assert(lastUrl.includes('/models/gemini-3.8-flash:generateContent'), 'worker must use only gemini-3.8-flash');
  assert(lastBody?.generationConfig?.thinkingConfig?.thinkingLevel === 'medium', 'stable planner must use medium thinking');
  assert(lastBody?.generationConfig?.responseMimeType === 'application/json', 'stable planner must require structured JSON output');

  nextPlan = { ...nextPlan, targetId: 'missing' };
  const badResponse = await worker.fetch(new Request('https://stable.test/v1/plan', {
    method: 'POST', headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ goal: 'メモ帳を開く', processName: 'explorer', windowTitle: 'Desktop', controls, image })
  }), { GEMINI_API_KEY: 'test-key' });
  assert(badResponse.status === 502, 'nonexistent UIA target must be rejected');

  const noKey = await worker.fetch(new Request('https://stable.test/v1/plan', {
    method: 'POST', headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ goal: 'メモ帳を開く', controls, image })
  }), {});
  assert(noKey.status === 503, 'missing Gemini key must fail explicitly, never fall back');

  console.log('HelpSys Stable Gemini worker self-test passed.');
} finally {
  globalThis.fetch = originalFetch;
}
