import '../tests/commander-stability-contract.mjs';
import quality from './quality-guide.js';

function assert(condition, message) {
  if (!condition) throw new Error(message);
}

let nextDecision;
let lastInvocation;
let value;
const env = {
  AI: {
    async run(model, args) {
      lastInvocation = { model, args };
      return { tool_calls: [{ name: 'return_quality_guidance', arguments: nextDecision }] };
    }
  }
};

async function ask(body, expectModel = true) {
  lastInvocation = null;
  const request = new Request('https://example.test/v1/quality-guide', {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ image: 'data:image/png;base64,AAAA', history: [], ...body })
  });
  const response = await quality.fetch(request, env, {});
  assert(response.status === 200, `unexpected quality response ${response.status}`);
  const value = await response.json();
  if (expectModel) {
    assert(lastInvocation?.args?.image?.startsWith('data:image/png;base64,'), 'quality planner must send the screenshot to the model');
    assert(lastInvocation?.args?.store === false, 'quality planner must explicitly disable model-side storage when supported.');
  } else {
    assert(lastInvocation === null, 'deterministic choice handling must not call the model');
  }
  return value;
}

function payload() {
  return JSON.parse(lastInvocation?.args?.messages?.[1]?.content || '{}');
}


// Candidate ranking regression: a task-relevant control must survive even when it appears
// after a large amount of unrelated full-desktop UIA noise.
const noisyElements = Array.from({ length: 220 }, (_, i) => ({
  id: `noise-${i}`, name: `unrelated control ${i}`, controlType: 'Button',
  processName: 'background', interactable: true, enabled: true
}));
noisyElements.push({
  id: 'late-settings', name: '設定', automationId: 'Settings', controlType: 'Button',
  processName: 'SearchHost', interactable: true, enabled: true
});
nextDecision = {
  status: 'target', targetId: 'late-settings', action: 'left_click',
  instruction: '設定を1回押してください。', question: null, key: null,
  confidence: 0.98, x: 0, y: 0, width: 0, height: 0,
  screenConfirmed: true, visualEvidence: '設定が見える', observedDomain: null, sponsored: false
};
value = await ask({
  request: '設定を開いて',
  systemContext: { foregroundProcess: 'SearchHost', foregroundProcessId: 30, runningApps: [] },
  elements: noisyElements
});
assert(value.status === 'target' && value.targetId === 'late-settings',
  'goal-ranked compaction must retain a relevant control even when it occurs after the old positional cutoff');
assert(payload().uiElements.some(x => x.id === 'late-settings'),
  'task-relevant late UIA target must reach the multimodal planner');
assert(payload().uiElements.length <= 160,
  'goal-ranked UIA payload must remain bounded after ranking');

const baseDone = {
  status: 'done', targetId: null, action: 'none', instruction: '目的の画面です。', question: null, key: null,
  confidence: 0.97, x: 0, y: 0, width: 0, height: 0,
  screenConfirmed: true, visualEvidence: 'Excelのウィンドウが画面前面に見える', observedDomain: null, sponsored: false
};

nextDecision = { ...baseDone, screenConfirmed: false, visualEvidence: '' };
value = await ask({
  request: 'Excelを開いて',
  evidence: {
    screenshotAvailable: true,
    uiElementCount: 1,
    interactableCount: 0,
    foregroundProcess: 'excel',
    evidenceSources: ['screenshot', 'ui-automation', 'foreground-window']
  },
  systemContext: { foregroundProcess: 'excel', foregroundProcessId: 10, runningApps: ['excel'] },
  elements: [{ id: 'e1', name: 'Microsoft Excel', controlType: 'Window', processName: 'excel', interactable: false, enabled: true }]
});
assert(value.status === 'not_found', 'done without visible screen confirmation must be rejected');
assert(lastInvocation?.model === '@cf/zai-org/glm-5.3-flash', 'quality planner must default to GLM-5.3 Flash');
assert(lastInvocation?.args?.reasoning_effort === 'low', 'quality planner must use low reasoning effort for normal latency');
assert(lastInvocation?.args?.max_completion_tokens <= 520, 'quality planner output budget must stay compact');
assert(payload().evidenceSummary?.sourceCount === 3, 'quality planner must receive an explicit evidence-source inventory');
assert(payload().evidenceSummary?.uiElementCount === 1, 'quality planner must receive the UIA candidate count');

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
  elements: [{ id: 'b1', name: '設定', automationId: 'Settings', controlType: 'Button', processName: 'SearchHost', interactable: true, enabled: true, toggleState: 'Off', selected: false }]
});
assert(value.status === 'target' && value.targetId === 'b1', 'visible UIA target aligned with screenshot should remain actionable');
assert(payload().uiElements?.[0]?.toggleState === 'Off', 'UIA state must survive compaction into the fused model input');
assert(payload().uiElements?.[0]?.selected === false, 'UIA selection state must survive compaction into the fused model input');

nextDecision = {
  status: 'target', targetId: 'b1', action: 'double_click', instruction: '設定を2回押してください。',
  question: null, key: null, confidence: 0.96, x: 0, y: 0, width: 0, height: 0,
  screenConfirmed: true, visualEvidence: '設定ボタンが画面に見える', observedDomain: null, sponsored: false
};
value = await ask({
  request: '設定を開いて',
  systemContext: { foregroundProcess: 'searchhost', foregroundProcessId: 30, runningApps: [] },
  elements: [{ id: 'b1', name: '設定', automationId: 'Settings', controlType: 'Button', processName: 'SearchHost', interactable: true, enabled: true }]
});
assert(value.status === 'target' && value.action === 'left_click', 'ordinary buttons must be normalized to one left click');
assert(value.instruction.includes('1回'), 'normalized button guidance must explicitly say one click');

nextDecision = {
  status: 'target', targetId: 'b1', action: 'type_text', instruction: '設定に入力してください。',
  question: null, key: null, confidence: 0.97, x: 0, y: 0, width: 0, height: 0,
  screenConfirmed: true, visualEvidence: '設定ボタンが画面に見える', observedDomain: null, sponsored: false
};
value = await ask({
  request: '設定を開いて',
  systemContext: { foregroundProcess: 'searchhost', foregroundProcessId: 30, runningApps: [] },
  elements: [{ id: 'b1', name: '設定', automationId: 'Settings', controlType: 'Button', processName: 'SearchHost', interactable: true, enabled: true, focused: true, keyboardFocusable: true }]
});
assert(value.status === 'not_found', 'type_text on a non-editable control must be rejected');

nextDecision = {
  status: 'target', targetId: 'b1', action: 'none', instruction: '設定を開いてください。',
  question: null, key: null, confidence: 0.98, x: 0, y: 0, width: 0, height: 0,
  screenConfirmed: true, visualEvidence: '設定ボタンが画面に見える', observedDomain: null, sponsored: false
};
value = await ask({
  request: '設定を開いて',
  systemContext: { foregroundProcess: 'searchhost', foregroundProcessId: 30, runningApps: [] },
  elements: [{ id: 'b1', name: '設定', automationId: 'Settings', controlType: 'Button', processName: 'SearchHost', interactable: true, enabled: true }]
});
assert(value.status === 'not_found', 'target decisions without a physical action must be rejected');

nextDecision = {
  status: 'target', targetId: 'b1', action: 'left_click', instruction: '「設定」を1回押してください。',
  question: null, key: null, confidence: 0.95, x: 0, y: 0, width: 0, height: 0,
  screenConfirmed: false, visualEvidence: '', observedDomain: null, sponsored: false
};
value = await ask({
  request: '設定を開いて',
  evidence: {
    screenshotAvailable: true,
    uiElementCount: 1,
    interactableCount: 1,
    foregroundProcess: 'searchhost',
    evidenceSources: ['screenshot', 'ui-automation', 'foreground-window']
  },
  systemContext: { foregroundProcess: 'searchhost', foregroundProcessId: 30, runningApps: [] },
  elements: [{ id: 'b1', name: '設定', automationId: 'Settings', controlType: 'Button', processName: 'SearchHost', interactable: true, enabled: true }]
});
assert(value.status === 'target' && value.targetId === 'b1' && value.screenConfirmed === false,
  'high-confidence real UIA target must survive when the screenshot is ambiguous');

nextDecision = { ...nextDecision, confidence: 0.90 };
value = await ask({
  request: '設定を開いて',
  systemContext: { foregroundProcess: 'searchhost', foregroundProcessId: 30, runningApps: [] },
  elements: [{ id: 'b1', name: '設定', automationId: 'Settings', controlType: 'Button', processName: 'SearchHost', interactable: true, enabled: true }]
});
assert(value.status === 'not_found', 'ambiguous structured target below the stricter high-confidence threshold must be rejected');

const detourElements = [
  { id: 'excel-target', name: 'Excel', controlType: 'Button', processName: 'SearchHost', interactable: true, enabled: true },
  { id: 'close-dialog', name: '閉じる', controlType: 'Button', processName: 'installer', interactable: true, enabled: true }
];
nextDecision = {
  status: 'target', targetId: 'close-dialog', action: 'left_click', instruction: '手前の不要な画面を閉じます。「閉じる」を1回押してください。',
  question: null, key: null, confidence: 0.96, x: 0, y: 0, width: 0, height: 0,
  screenConfirmed: true, visualEvidence: '手前に別のダイアログと閉じるボタンが見える', observedDomain: null, sponsored: false
};
value = await ask({
  request: 'Excelを開いて',
  systemContext: { foregroundProcess: 'installer', foregroundProcessId: 71, runningApps: [] },
  elements: detourElements
});
assert(value.status === 'not_found', 'normal launch route must keep its canonical target guard');

value = await ask({
  request: 'Excelを開いて',
  recoveryMode: true,
  routeIssue: 'ユーザーが別のダイアログを開いた',
  history: [{ step: 1, action: 'failed_left_click', targetName: 'Excel', instruction: 'Excelを押したが画面が変わらなかった' }],
  systemContext: { foregroundProcess: 'installer', foregroundProcessId: 71, runningApps: [] },
  elements: detourElements
});
assert(value.status === 'target' && value.targetId === 'close-dialog',
  'recovery mode must allow a grounded bridge action outside the originally imagined launch route');
assert(payload().recoveryMode === true && payload().routeIssue.includes('別のダイアログ'),
  'recovery mode and route issue must reach the model');
assert(payload().canonicalConstraint?.role === 'route_reference',
  'launch canonical path must become a route reference during recovery');

nextDecision = {
  status: 'target', targetId: 'unsafe-close', action: 'left_click', instruction: '警告を閉じます。',
  question: null, key: null, confidence: 0.99, x: 0, y: 0, width: 0, height: 0,
  screenConfirmed: true, visualEvidence: 'Privacy error の警告が見える', observedDomain: 'example.test', sponsored: false
};
value = await ask({
  request: 'このサイトを見たい',
  recoveryMode: true,
  routeIssue: '安全警告の画面へ逸脱した',
  systemContext: { foregroundProcess: 'chrome', foregroundProcessId: 80, foregroundTitle: 'Privacy error', runningApps: ['chrome'], browser: { domain: 'example.test', url: 'https://example.test/path?secret=DO_NOT_FORWARD' } },
  evidence: { browserDomain: 'example.test', browserUrl: 'https://example.test/private?session=DO_NOT_FORWARD_EVIDENCE' },
  elements: [
    { id: 'unsafe-close', name: '閉じる', controlType: 'Button', processName: 'chrome', interactable: true, enabled: true },
    { id: 'warning', name: 'Privacy error 安全ではありません', controlType: 'Text', processName: 'chrome', interactable: false, enabled: true }
  ]
});
assert(value.status === 'not_found', 'recovery mode must never relax a browser safety warning guard');
assert(payload().systemContext?.browser?.url === undefined, 'quality model payload must not contain a full browser URL.');
assert(payload().evidenceSummary?.browserUrl === undefined, 'quality evidence payload must not contain a full browser URL.');
assert(JSON.stringify(payload()).includes('DO_NOT_FORWARD') === false, 'quality model payload must not retain URL secrets.');

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
  status: 'target', targetId: 'secret', action: 'type_text', instruction: '入力してください。',
  question: null, key: null, confidence: 0.96, x: 0, y: 0, width: 0, height: 0,
  screenConfirmed: true, visualEvidence: '入力欄が見える', observedDomain: null, sponsored: false
};
value = await ask({
  request: 'ログインしたい',
  systemContext: { foregroundProcess: 'chrome', foregroundProcessId: 40, runningApps: ['chrome'] },
  elements: [{ id: 'secret', name: 'パスワード', controlType: 'Edit', processName: 'chrome', interactable: true, enabled: true, focused: true, keyboardFocusable: true, password: true, value: 'do-not-send', inputPresent: false }]
});
assert(payload().uiElements?.[0]?.value === undefined, 'raw input values must never enter the quality model payload');
assert(payload().uiElements?.[0]?.inputPresent === false, 'password controls must not expose input-presence state to the model');

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


value = await ask({
  request: 'Gmailを開いてメールを見たい',
  history: [{ step: 0, action: 'clarification_answer', targetName: '正二郎商事', instruction: 'どの名前を使うか教えてください。' }],
  systemContext: { foregroundProcess: 'chrome', foregroundProcessId: 91, foregroundTitle: 'アカウントの選択', runningApps: ['chrome'] },
  elements: [
    { id: 'choice-personal', name: '正二郎', controlType: 'Button', processName: 'chrome', interactable: true, enabled: true },
    { id: 'choice-business', name: '正二郎商事', controlType: 'Button', processName: 'chrome', interactable: true, enabled: true },
    { id: 'choice-other', name: '別のアカウントを使用', controlType: 'Button', processName: 'chrome', interactable: true, enabled: true }
  ]
}, false);
assert(value.status === 'target' && value.targetId === 'choice-business',
  'answered account choice must resolve deterministically on the quality path');

value = await ask({
  request: 'Gmailを開いてメールを見たい',
  systemContext: { foregroundProcess: 'chrome', foregroundProcessId: 92, foregroundTitle: 'Choose an account', runningApps: ['chrome'] },
  elements: [
    { id: 'choice-a', name: 'Personal', controlType: 'Button', processName: 'chrome', interactable: true, enabled: true },
    { id: 'choice-b', name: 'Business', controlType: 'Button', processName: 'chrome', interactable: true, enabled: true },
    { id: 'choice-other', name: 'Use another account', controlType: 'Button', processName: 'chrome', interactable: true, enabled: true }
  ]
}, false);
assert(value.status === 'clarify', 'English account chooser must be recognized as a user choice');

value = await ask({
  request: 'Gmailを開いてメールを見たい',
  history: [{ step: 0, action: 'clarification_answer', targetName: '<email>', instruction: 'どのアカウントを使いますか？' }],
  systemContext: { foregroundProcess: 'chrome', foregroundProcessId: 93, foregroundTitle: 'アカウントの選択', runningApps: ['chrome'] },
  elements: [
    { id: 'choice-a', name: '<email>', controlType: 'Button', processName: 'chrome', interactable: true, enabled: true },
    { id: 'choice-b', name: '<email>', controlType: 'Button', processName: 'chrome', interactable: true, enabled: true }
  ]
}, false);
assert(value.status === 'clarify', 'redacted account identity must never be guessed or auto-selected');

console.log('HelpSys multisource evidence-fusion and route-recovery self-test passed.');

async function askOutlaw(body) {
  lastInvocation = null;
  const request = new Request('https://example.test/v1/quality-guide', {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ image: 'data:image/png;base64,AAAA', history: [], outlawMode: true, ...body })
  });
  const response = await quality.fetch(request, env, {});
  assert(response.status === 200, `unexpected outlaw quality response ${response.status}`);
  return response.json();
}

nextDecision = {
  status: 'target', targetId: 'unsafe-route', action: 'left_click',
  instruction: '青い枠の項目を1回押してください。', question: null, key: null,
  confidence: 0.55, x: 0, y: 0, width: 0, height: 0,
  screenConfirmed: false, visualEvidence: '', observedDomain: null, sponsored: false
};
value = await askOutlaw({
  request: '現在画面から進んで',
  systemContext: { foregroundProcess: 'browser', foregroundProcessId: 44, runningApps: [] },
  elements: [{
    id: 'unsafe-route', name: '続行', controlType: 'Button', processName: 'browser',
    interactable: true, enabled: true, focused: false, keyboardFocusable: true
  }]
});
assert(value.status === 'target' && value.targetId === 'unsafe-route',
  'Outlaw must preserve a real mechanically valid target even when confidence is below normal quality thresholds');
assert(lastInvocation?.args?.messages?.[0]?.content?.includes('operation-first planning component of HelpSys Outlaw'),
  'Outlaw inference must use its dedicated operation-first system prompt');


nextDecision = {
  status: 'target', targetId: 'other-monitor', action: 'left_click',
  instruction: '別画面の項目を押してください。', question: null, key: null,
  confidence: 0.99, x: 0, y: 0, width: 0, height: 0,
  screenConfirmed: false, visualEvidence: '', observedDomain: null, sponsored: false
};
value = await askOutlaw({
  request: '設定を開いて',
  capture: { screenX: 0, screenY: 0, screenWidth: 1920, screenHeight: 1080, imageWidth: 1920, imageHeight: 1080 },
  systemContext: { foregroundProcess: 'app', foregroundProcessId: 99, runningApps: [] },
  elements: [
    { id: 'current-monitor', name: '設定', controlType: 'Button', processName: 'app', interactable: true, enabled: true, x: 200, y: 200, width: 120, height: 40 },
    { id: 'other-monitor', name: '設定', controlType: 'Button', processName: 'app', interactable: true, enabled: true, x: 2200, y: 200, width: 120, height: 40 }
  ]
});
assert(value.status === 'not_found',
  'Outlaw multimodal planner must reject a structured target outside the captured monitor');

nextDecision = {
  status: 'target', targetId: 'current-monitor', action: 'left_click',
  instruction: '設定を押してください。', question: null, key: null,
  confidence: 0.60, x: 0, y: 0, width: 0, height: 0,
  screenConfirmed: false, visualEvidence: '', observedDomain: null, sponsored: false
};
value = await askOutlaw({
  request: '設定を開いて',
  capture: { screenX: 0, screenY: 0, screenWidth: 1920, screenHeight: 1080, imageWidth: 1920, imageHeight: 1080 },
  systemContext: { foregroundProcess: 'app', foregroundProcessId: 99, runningApps: [] },
  elements: [
    { id: 'current-monitor', name: '設定', controlType: 'Button', processName: 'app', interactable: true, enabled: true, x: 200, y: 200, width: 120, height: 40 },
    { id: 'other-monitor', name: '設定', controlType: 'Button', processName: 'app', interactable: true, enabled: true, x: 2200, y: 200, width: 120, height: 40 }
  ]
});
assert(value.status === 'target' && value.targetId === 'current-monitor',
  'Outlaw multimodal planner must keep a mechanically valid target inside the captured monitor');
assert(payload().uiElements.find(x => x.id === 'current-monitor')?.inCapture === true,
  'planner payload must mark current-monitor UIA targets as inside the screenshot');

const longHistory = Array.from({ length: 40 }, (_, i) => ({
  step: i,
  action: i === 3 ? 'no_effect_left_click' : i === 6 ? 'outlaw_repeat_blocked' : 'left_click',
  targetName: `target-${i}`,
  instruction: `history-${i}`
}));
nextDecision = {
  status: 'target', targetId: 'hist-target', action: 'left_click',
  instruction: '項目を押してください。', question: null, key: null,
  confidence: 0.7, x: 0, y: 0, width: 0, height: 0,
  screenConfirmed: false, visualEvidence: '', observedDomain: null, sponsored: false
};
value = await askOutlaw({
  request: '続けて',
  history: longHistory,
  systemContext: { foregroundProcess: 'app', foregroundProcessId: 7, runningApps: [] },
  elements: [{ id: 'hist-target', name: '続ける', controlType: 'Button', processName: 'app', interactable: true, enabled: true }]
});
assert(payload().completedSteps.length <= 24,
  'Outlaw planner history must stay bounded even when desktop history contains 64 entries');
assert(payload().completedSteps.some(x => x.action === 'no_effect_left_click'),
  'Outlaw planner must retain older confirmed no-effect history beyond the recent tail');
assert(payload().completedSteps.some(x => x.action === 'outlaw_repeat_blocked'),
  'Outlaw planner must retain older repeat-block evidence beyond the recent tail');
assert(payload().completedSteps.some(x => x.instruction === 'history-39'),
  'Outlaw planner must still retain the most recent operational context');

nextDecision = {
  status: 'target', targetId: 'search-box', action: 'type_text',
  instruction: '入力欄に文字を入力してください。', question: null, key: null,
  confidence: 0.7, x: 0, y: 0, width: 0, height: 0,
  screenConfirmed: false, visualEvidence: '', observedDomain: null, sponsored: false
};
value = await askOutlaw({
  request: '検索を続けて',
  systemContext: {
    foregroundProcess: 'browser', foregroundProcessId: 55, runningApps: [],
    browser: { processName: 'browser', url: 'https://example.test/settings/search?q=helpsys', domain: 'example.test', https: true }
  },
  elements: [{
    id: 'search-box', name: '検索', controlType: 'Edit', processName: 'browser',
    interactable: true, enabled: true, focused: true, keyboardFocusable: true,
    value: '現在の検索語'
  }]
});
assert(payload().systemContext?.browser?.url === 'https://example.test/settings/search?q=helpsys',
  'Outlaw planner must retain the full current browser URL for same-domain page-state grounding');
assert(payload().uiElements.find(x => x.id === 'search-box')?.value === '現在の検索語',
  'Outlaw planner must retain ordinary non-password input values needed to understand the current UI state');

async function askOutlawStructured(body) {
  lastInvocation = null;
  const request = new Request('https://example.test/v1/quality-guide', {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ history: [], outlawMode: true, ...body })
  });
  const response = await quality.fetch(request, env, {});
  assert(response.status === 200, `unexpected structured-only outlaw response ${response.status}`);
  return response.json();
}

nextDecision = {
  status: 'target', targetId: 'structured-only', action: 'left_click',
  instruction: '項目を押してください。', question: null, key: null,
  confidence: 0.45, x: 0, y: 0, width: 0, height: 0,
  screenConfirmed: false, visualEvidence: '', observedDomain: null, sponsored: false
};
value = await askOutlawStructured({
  request: '現在画面から進んで',
  systemContext: { foregroundProcess: 'app', foregroundProcessId: 9, runningApps: [] },
  evidence: { screenshotAvailable: false },
  elements: [{
    id: 'structured-only', name: '続ける', controlType: 'Button', processName: 'app',
    interactable: true, enabled: true
  }]
});
assert(value.status === 'target' && value.targetId === 'structured-only',
  'image-less Outlaw fallback must use the same operation-first validation instead of normal canonical planner guards');
assert(lastInvocation?.args?.image === undefined,
  'structured-only Outlaw inference must not invent a fake screenshot');
