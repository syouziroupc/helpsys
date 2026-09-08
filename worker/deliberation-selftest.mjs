import assert from 'node:assert/strict';
import guard from './deliberation-guard.js';

function element(id, overrides = {}) {
  return {
    id,
    name: `項目 ${id}`,
    automationId: id,
    className: 'Button',
    controlType: 'Button',
    processName: 'sampleapp',
    interactable: true,
    enabled: true,
    keyboardFocusable: true,
    focused: false,
    password: false,
    x: 100,
    y: 100,
    width: 120,
    height: 40,
    ...overrides
  };
}

function requestFor(elements, goal = '画面に表示されている項目を使いたい', systemContext = {}) {
  return new Request('https://helpsys.test/v1/guide', {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({
      request: goal,
      history: [],
      systemContext: {
        foregroundProcess: 'sampleapp',
        foregroundTitle: 'Sample',
        taskbarVisible: true,
        runningApps: ['sampleapp'],
        browser: null,
        ...systemContext
      },
      elements
    })
  });
}

async function runCase({ elements, decision, goal, systemContext }) {
  let calls = 0;
  let capturedOptions = null;
  const env = {
    HELPSYS_MODEL: '@cf/google/gemma-4-26b-a4b-it',
    AI: {
      async run(_model, options) {
        calls++;
        capturedOptions = options;
        return { tool_calls: [{ name: 'return_guidance', arguments: decision }] };
      }
    }
  };

  const response = await guard.fetch(requestFor(elements, goal, systemContext), env, {});
  assert.equal(response.status, 200);
  return { result: await response.json(), calls, capturedOptions };
}

const normal = await runCase({
  elements: [element('e1', { name: '続ける', automationId: 'continue' })],
  decision: {
    status: 'target', targetId: 'e1', action: 'left_click',
    instruction: '青い枠の「続ける」を、マウスの左ボタンで1回押してください。',
    question: null, key: null, confidence: 0.93
  }
});
assert.equal(normal.calls, 1, 'structured guidance must invoke Workers AI exactly once');
assert.equal(normal.capturedOptions?.chat_template_kwargs?.enable_thinking, true, 'the single planner call must use reasoning');
assert.equal(normal.result.status, 'target');
assert.equal(normal.result.targetId, 'e1');

// The core planner accepts up to 420 candidates. The final deterministic guard must inspect
// the same range instead of falsely rejecting a valid target after item 240.
const many = Array.from({ length: 300 }, (_, index) => element(`e${index + 1}`));
const late = await runCase({
  elements: many,
  decision: {
    status: 'target', targetId: 'e300', action: 'left_click',
    instruction: '青い枠の項目を、マウスの左ボタンで1回押してください。',
    question: null, key: null, confidence: 0.94
  }
});
assert.equal(late.calls, 1);
assert.equal(late.result.status, 'target');
assert.equal(late.result.targetId, 'e300', 'candidate 300 must survive final validation');

const browserContext = {
  foregroundProcess: 'msedge',
  foregroundTitle: 'Example - Edge',
  runningApps: ['msedge'],
  browser: { processName: 'msedge', windowTitle: 'Example - Edge', url: 'https://example.com', domain: 'example.com', https: true, addressFieldFocused: false }
};

// A page's own search box must never be promoted to the browser address bar merely because
// its accessible name contains "search/検索".
const pageSearch = await runCase({
  elements: [element('search1', {
    name: 'サイト内を検索', automationId: 'site-search', className: 'search-input',
    controlType: 'Edit', processName: 'msedge', keyboardFocusable: true
  })],
  decision: {
    status: 'target', targetId: 'search1', action: 'left_click',
    instruction: '画面上部の、検索や文字を入力できる欄で、マウスの左ボタンを1回押してください。',
    question: null, key: null, confidence: 0.95
  },
  goal: 'インターネットでページを開きたい',
  systemContext: browserContext
});
assert.equal(pageSearch.result.status, 'not_found', 'generic webpage search must be rejected as an address bar');

const omnibox = await runCase({
  elements: [element('address1', {
    name: 'アドレスと検索バー', automationId: 'address-bar', className: 'OmniboxViewViews',
    controlType: 'Edit', processName: 'msedge', keyboardFocusable: true
  })],
  decision: {
    status: 'target', targetId: 'address1', action: 'left_click',
    instruction: '画面上部の、検索や文字を入力できる欄で、マウスの左ボタンを1回押してください。',
    question: null, key: null, confidence: 0.95
  },
  goal: 'インターネットでページを開きたい',
  systemContext: browserContext
});
assert.equal(omnibox.result.status, 'target', 'a real address-bar accessibility hint must remain usable');
assert.equal(omnibox.result.targetId, 'address1');

console.log('deliberation reliability selftest: ok');
