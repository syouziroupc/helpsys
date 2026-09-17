import worker from '../worker/education-gemini.js';

function assert(condition, message) {
  if (!condition) throw new Error(message);
}

let lastBody = null;
const originalFetch = globalThis.fetch;
globalThis.fetch = async (_url, options) => {
  lastBody = JSON.parse(options.body);
  return new Response(JSON.stringify({
    candidates: [{ content: { parts: [{ text: JSON.stringify({
      status: 'hint',
      message: '画面の左下にあるWindowsキーを探してください。',
      nextHintLevel: 2
    }) }] } }]
  }), { status: 200, headers: { 'content-type': 'application/json' } });
};

try {
  const test = await worker.fetch(new Request('https://education.test/v1/education/assist', {
    method: 'POST', headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ stage: 'test', lessonId: 'x', lessonTitle: 'テスト', objective: '確認' })
  }), { GEMINI_API_KEY: 'test-key' });
  const testBody = await test.json();
  assert(testBody.status === 'blocked', 'test stage must not reveal hints');

  const response = await worker.fetch(new Request('https://education.test/v1/education/assist', {
    method: 'POST', headers: { 'content-type': 'application/json' },
    body: JSON.stringify({
      stage: 'practice',
      lessonId: 'windows',
      lessonTitle: '連絡先 alice@example.com',
      objective: '電話 090-1234-5678 を含む例からWindowsを学ぶ',
      message: 'password=hunter2 を例にしない',
      hintLevel: 1
    })
  }), { GEMINI_API_KEY: 'test-key' });

  const body = await response.json();
  assert(response.status === 200 && body.status === 'hint', 'practice hint must succeed');
  const serialized = JSON.stringify(lastBody);
  for (const forbidden of ['alice@example.com', '090-1234-5678', 'hunter2'])
    assert(!serialized.includes(forbidden), `Education payload leaked: ${forbidden}`);
  assert(lastBody?.generationConfig?.thinkingConfig?.thinkingLevel === 'low', 'Education must use Gemini low thinking');

  console.log('HelpSys Stable Education Gemini self-test passed.');
} finally {
  globalThis.fetch = originalFetch;
}
