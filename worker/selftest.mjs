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
await runVisionCase();
console.log('HelpSys Worker self-test passed.');

async function runCase(name, modelResponse) {
  const response = await invoke('/v1/guide', requestBody, modelResponse);
  const json = await response.json();
  assert(response.status === 200, `${name}: expected HTTP 200, got ${response.status}`);
  assert(json.status === 'target', `${name}: expected target, got ${json.status}`);
  assert(json.targetId === 'u1', `${name}: expected u1, got ${json.targetId}`);
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
  assert(json.status === 'not_found', 'context-only UI text must never become a click target');
}

async function runDoubleClickNormalizationCase() {
  const body = { ...requestBody, elements: [{ ...requestBody.elements[0], controlType: 'ListItem' }] };
  const response = await invoke('/v1/guide', body, { tool_calls: [{ name: 'return_guidance', arguments: { ...expected, instruction: 'Google Chromeをダブルクリックしてください。', action: 'left_click' } }] });
  const json = await response.json();
  assert(json.action === 'double_click', 'desktop-like double-click wording must normalize to double_click');
  assert(json.instruction.includes('2回クリック'), 'double-click instruction must be beginner readable');
}

async function runVisionCase() {
  const modelResponse = { tool_calls: [{ name: 'return_vision_guidance', arguments: { status: 'target', label: 'Chrome', instruction: 'ここを左クリックしてください。', question: null, x: 800, y: 850, width: 55, height: 70, confidence: 0.91 } }] };
  const response = await invoke('/v1/vision-guide', { request: 'YouTubeを見たい', history: [], image: 'data:image/png;base64,AAAA' }, modelResponse);
  const json = await response.json();
  assert(response.status === 200, `vision: expected HTTP 200, got ${response.status}`);
  assert(json.status === 'target', `vision: expected target, got ${json.status}`);
  assert(json.x === 800 && json.width === 55, 'vision coordinates were not preserved');
}

function invoke(path, body, modelResponse) {
  return worker.fetch(new Request(`https://unit.test${path}`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify(body) }), {
    HELPSYS_MODEL: '@cf/google/gemma-4-26b-a4b-it',
    AI: { run: async () => modelResponse }
  });
}

function assert(condition, message) { if (!condition) throw new Error(message); }
