import '../tests/commander-stability-contract.mjs';
import quality from './quality-guide.js';

function assert(condition, message) {
  if (!condition) throw new Error(message);
}

let nextDecision;
let lastInvocation;
const env = {
  AI: {
    async run(model, args) {
      lastInvocation = { model, args };
      return { tool_calls: [{ name: 'return_quality_guidance', arguments: nextDecision }] };
    }
  }
};

async function ask(body) {
  lastInvocation = null;
  const request = new Request('https://example.test/v1/quality-guide', {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ image: 'data:image/png;base64,AAAA', history: [], ...body })
  });
  const response = await quality.fetch(request, env, {});
  assert(response.status === 200, `unexpected quality response ${response.status}`);
  const value = await response.json();
  assert(lastInvocation?.args?.image?.startsWith('data:image/png;base64,'), 'quality planner must send the screenshot to the model');
  return value;
}

const baseDone = {
  status: 'done', targetId: null, action: 'none', instruction: '目的の画面です。', question: null, key: null,
  confidence: 0.97, x: 0, y: 0, width: 0, height: 0,
  screenConfirmed: true, visualEvidence: 'Excelのウィンドウが画面前面に見える', observedDomain: null, sponsored: false
};

nextDecision = { ...baseDone, screenConfirmed: false, visualEvidence: '' };
let value = await ask({
  request: 'Excelを開いて',
  systemContext: { foregroundProcess: 'excel', foregroundProcessId: 10, runningApps: ['excel'] },
  elements: [{ id: 'e1', name: 'Microsoft Excel', controlType: 'Window', processName: 'excel', interactable: false, enabled: true }]
});
assert(value.status === 'not_found', 'done without visible screen confirmation must be rejected');
assert(lastInvocation?.model === '@cf/zai-org/glm-5.3-flash', 'quality planner must default to GLM-5.3 Flash');
assert(lastInvocation?.args?.reasoning_effort === 'low', 'quality planner must use low reasoning effort for normal latency');
assert(lastInvocation?.args?.max_completion_tokens <= 520, 'quality planner output budget must stay compact');

nextDecision = { ...baseDone };
value = await ask({
  request: 'Excelを開いて',
  systemContext: { foregroundProcess: 'notepad', foregroundProcessId: 20, runningApps: ['excel', 'notepad'] },
  elements: [{ id: 'n1', name: '本文', controlType: 'Edit', processName: 'notepad', interactable: true, enabled: true, focused: true, keyboardFocusable: true }]
});
assert(value.status !== 'done', 'background-running Excel must not become done even when nonvisual data says it is running');

nextDecision = { ...baseDone };
value = await ask({
  request: 'Excelを開いて',
  systemContext: { foregroundProcess: 'excel', foregroundProcessId: 10, runningApps: ['excel'] },
  elements: [{ id: 'e1', name: 'Microsoft Excel', controlType: 'Window', processName: 'excel', interactable: false, enabled: true }]
});
assert(value.status === 'done' && value.screenConfirmed === true, 'visibly foreground Excel should allow done');

nextDecision = {
  status: 'target', targetId: 'b1', action: 'left_click', instruction: '青い枠の設定で、マウスの左ボタンを1回押してください。',
  question: null, key: null, confidence: 0.93, x: 0, y: 0, width: 0, height: 0,
  screenConfirmed: true, visualEvidence: '設定ボタンが画面に見える', observedDomain: null, sponsored: false
};
value = await ask({
  request: '設定を開いて',
  systemContext: { foregroundProcess: 'searchhost', foregroundProcessId: 30, runningApps: [] },
  elements: [{ id: 'b1', name: '設定', automationId: 'Settings', controlType: 'Button', processName: 'SearchHost', interactable: true, enabled: true }]
});
assert(value.status === 'target' && value.targetId === 'b1', 'visible UIA target aligned with screenshot should remain actionable');

nextDecision = {
  status: 'target', targetId: 'chrome-desktop', action: 'left_click',
  instruction: '青い枠のインターネットを見るアプリで、マウスの左ボタンを1回押してください。',
  question: null, key: null, confidence: 0.96, x: 0, y: 0, width: 0, height: 0,
  screenConfirmed: true, visualEvidence: 'デスクトップにGoogle Chromeのショートカットが見える', observedDomain: null, sponsored: false
};
value = await ask({
  request: 'youtubeが見たい',
  systemContext: { foregroundProcess: 'explorer', foregroundProcessId: 50, runningApps: ['chrome'], taskbarVisible: true },
  elements: [{
    id: 'chrome-desktop', name: 'Google Chrome', controlType: 'ListItem', processName: 'explorer',
    interactable: true, enabled: true, keyboardFocusable: true, focused: false
  }]
});
assert(value.status === 'target' && value.targetId === 'chrome-desktop', 'Chrome desktop shortcut should remain the selected target');
assert(value.action === 'double_click', 'Explorer desktop shortcut launch must be corrected to double_click');
assert(value.instruction.includes('2回'), 'desktop shortcut instruction must explicitly say two left-button presses');
assert(value.instruction.includes('Google Chrome'), 'guidance should name the actual visible browser instead of a generic internet-app phrase');

nextDecision = {
  status: 'clarify', targetId: null, action: 'none', instruction: '', question: 'パスワードを教えてください。', key: null,
  confidence: 0.99, x: 0, y: 0, width: 0, height: 0,
  screenConfirmed: true, visualEvidence: 'ログイン画面', observedDomain: null, sponsored: false
};
value = await ask({
  request: 'ログインしたい',
  systemContext: { foregroundProcess: 'chrome', foregroundProcessId: 40, runningApps: ['chrome'] },
  elements: []
});
assert(value.status === 'not_found', 'quality planner must never ask HelpSys to receive a secret');

console.log('HelpSys GLM quality-first fused guidance self-test passed.');
