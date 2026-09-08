import worker from './index.js';

const requestBody = {
  request: 'YouTubeを見たい',
  history: [],
  elements: [{ id: 'u1', name: 'Google Chrome', automationId: 'Chrome', className: 'Chrome_WidgetWin_1', controlType: 'Button', processName: 'explorer', interactable: true, enabled: true, keyboardFocusable: true, focused: false, password: false, x: 100, y: 100, width: 64, height: 64 }]
};

const expected = { status: 'target', targetId: 'u1', action: 'left_click', instruction: 'ここを左クリックしてください。', question: null, key: null, confidence: 0.93 };

await runCase('traditional root tool_calls', { tool_calls: [{ name: 'return_guidance', arguments: expected }] });
await runCase('chat completions tool_calls', { choices: [{ message: { tool_calls: [{ type: 'function', function: { name: 'return_guidance', arguments: JSON.stringify(expected) } }] } }] });
await runCase('defensive text JSON', { choices: [{ message: { content: JSON.stringify(expected) } }] });
await runInvalidTargetCase();
await runContextTargetRejectionCase();
await runDoubleClickNormalizationCase();
await runExcelAbsentUsesStartCase();
await runExcelSearchTypingCase();
await runExcelNoAnchorAvoidsNonsenseCase();
await runVisionCase();
console.log('HelpSys Worker self-test passed.');

async function runCase(name, modelResponse) {
  const response = await invoke('/v1/guide', requestBody, modelResponse);
  const json = await response.json();
  assert(response.status === 200, `${name}: expected HTTP 200, got ${response.status}`);
  assert(json.status === 'target', `${name}: expected target, got ${json.status}`);
  assert(json.targetId === 'u1', `${name}: expected u1, got ${json.targetId}`);
  assert(!json.instruction.includes('クリック'), `${name}: beginner instruction must not expose click jargon`);
}

async function runInvalidTargetCase() {
  const response = await invoke('/v1/guide', requestBody, { tool_calls: [{ name: 'return_guidance', arguments: { ...expected, targetId: 'invented-control', confidence: 0.99 } }] });
  const json = await response.json();
  assert(json.status === 'not_found', 'invented target must be rejected');
  assert(json.targetId === null, 'invented target id must not escape validation');
}

async function runContextTargetRejectionCase() {
  const body = { ...requestBody, elements: [...requestBody.elements, { id: 'c1', name: 'Chrome はどなたが使用しますか？', controlType: 'Text', processName: 'chrome', interactable: false, enabled: true }] };
  const response = await invoke('/v1/guide', body, { tool_calls: [{ name: 'return_guidance', arguments: { ...expected, targetId: 'c1', confidence: 0.99 } }] });
  const json = await response.json();
  assert(json.status === 'not_found', 'context-only UI text must never become a target');
}

async function runDoubleClickNormalizationCase() {
  const body = { ...requestBody, elements: [{ ...requestBody.elements[0], controlType: 'ListItem' }] };
  const response = await invoke('/v1/guide', body, { tool_calls: [{ name: 'return_guidance', arguments: { ...expected, instruction: 'Google Chromeをダブルクリックしてください。', action: 'left_click' } }] });
  const json = await response.json();
  assert(json.action === 'double_click', 'desktop-like double-click wording must normalize to double_click');
  assert(json.instruction.includes('2回押'), 'two-press instruction must describe the physical action');
  assert(!json.instruction.includes('ダブルクリック') && !json.instruction.includes('クリック'), 'mouse jargon must not reach the user');
}

async function runExcelAbsentUsesStartCase() {
  let modelCalled = false;
  const body = {
    request: 'エクセルを開いて', history: [],
    elements: [
      { id: 'uStart', name: 'スタート', automationId: 'StartButton', controlType: 'Button', processName: 'explorer', interactable: true, enabled: true, keyboardFocusable: true, focused: false, password: false },
      { id: 'uTrash', name: 'ごみ箱', automationId: '', controlType: 'ListItem', processName: 'explorer', interactable: true, enabled: true, keyboardFocusable: true, focused: false, password: false }
    ]
  };
  const response = await invoke('/v1/guide', body, expected, () => { modelCalled = true; });
  const json = await response.json();
  assert(json.status === 'target' && json.targetId === 'uStart', 'Excel absent must route to Windows Start, not an unrelated object');
  assert(!modelCalled, 'known launch route should not spend a model call when Start is available');
  assert(!json.instruction.includes('クリック'), 'Start instruction must use physical beginner wording');
}

async function runExcelSearchTypingCase() {
  const body = {
    request: 'エクセルを開いて', history: [],
    elements: [
      { id: 'uSearch', name: '検索', automationId: 'SearchTextBox', controlType: 'Edit', processName: 'SearchHost', interactable: true, enabled: true, keyboardFocusable: true, focused: true, password: false }
    ]
  };
  const response = await invoke('/v1/guide', body, expected);
  const json = await response.json();
  assert(json.status === 'target' && json.targetId === 'uSearch' && json.action === 'type_text', 'focused Windows search must type Excel');
  assert(json.instruction.includes('Excel') && json.instruction.includes('Enter'), 'search step must state exactly what to type and how to finish');
}

async function runExcelNoAnchorAvoidsNonsenseCase() {
  let modelCalled = false;
  const body = {
    request: 'エクセルを開いて', history: [],
    elements: [
      { id: 'uTrash', name: 'ごみ箱', automationId: '', controlType: 'ListItem', processName: 'explorer', interactable: true, enabled: true, keyboardFocusable: true, focused: false, password: false }
    ]
  };
  const response = await invoke('/v1/guide', body, { ...expected, targetId: 'uTrash', confidence: 0.99 }, () => { modelCalled = true; });
  const json = await response.json();
  assert(json.status === 'not_found', 'when Excel/Start/Search are absent, HelpSys must request visual fallback instead of choosing trash or another unrelated object');
  assert(!modelCalled, 'force-vision launch guard must reject the situation before model inference');
}

async function runVisionCase() {
  const modelResponse = { tool_calls: [{ name: 'return_vision_guidance', arguments: { status: 'target', label: 'Chrome', instruction: 'ここを左クリックしてください。', question: null, x: 800, y: 850, width: 55, height: 70, confidence: 0.91 } }] };
  const response = await invoke('/v1/vision-guide', { request: 'YouTubeを見たい', history: [], image: 'data:image/png;base64,AAAA' }, modelResponse);
  const json = await response.json();
  assert(response.status === 200, `vision: expected HTTP 200, got ${response.status}`);
  assert(json.status === 'target', `vision: expected target, got ${json.status}`);
  assert(json.x === 800 && json.width === 55, 'vision coordinates were not preserved');
  assert(!json.instruction.includes('クリック'), 'vision instructions must also avoid click jargon');
}

function invoke(path, body, modelResponse, onModelCall = null) {
  return worker.fetch(new Request(`https://unit.test${path}`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify(body) }), {
    HELPSYS_MODEL: '@cf/google/gemma-4-26b-a4b-it',
    AI: { run: async () => { onModelCall?.(); return modelResponse; } }
  });
}

function assert(condition, message) { if (!condition) throw new Error(message); }
