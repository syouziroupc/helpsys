import worker from './index.js';

const baseSystem = {
  ForegroundProcess: 'explorer', ForegroundTitle: '', ForegroundProcessId: 1, TaskbarVisible: false, RunningApps: [], Browser: null
};

const requestBody = {
  request: '普通の操作をしたい',
  history: [],
  systemContext: baseSystem,
  elements: [{ id: 'u1', name: 'テスト', automationId: 'test', className: 'Button', controlType: 'Button', processName: 'explorer', interactable: true, enabled: true, keyboardFocusable: true, focused: false, password: false, x: 100, y: 100, width: 64, height: 64 }]
};

const expected = { status: 'target', targetId: 'u1', action: 'left_click', instruction: 'ここを左クリックしてください。', question: null, key: null, confidence: 0.93 };

await runCase('traditional root tool_calls', { tool_calls: [{ name: 'return_guidance', arguments: expected }] });
await runCase('chat completions tool_calls', { choices: [{ message: { tool_calls: [{ type: 'function', function: { name: 'return_guidance', arguments: JSON.stringify(expected) } }] } }] });
await runCase('defensive text JSON', { choices: [{ message: { content: JSON.stringify(expected) } }] });
await runInvalidTargetCase();
await runContextTargetRejectionCase();
await runDoubleClickNormalizationCase();
await runHiddenTaskbarExcelCase();
await runBrowserNewTabCase();
await runProfileChoiceCase();
await runVisionCase();
await runSponsoredVisionRejectionCase();
console.log('HelpSys Worker self-test passed.');

async function runCase(name, modelResponse) {
  const response = await invoke('/v1/guide', requestBody, modelResponse);
  const json = await response.json();
  assert(response.status === 200, `${name}: expected HTTP 200, got ${response.status}`);
  assert(json.status === 'target', `${name}: expected target, got ${json.status}`);
  assert(json.targetId === 'u1', `${name}: expected u1, got ${json.targetId}`);
  assert(!json.instruction.includes('クリック'), `${name}: beginner wording must avoid click jargon`);
}

async function runInvalidTargetCase() {
  const response = await invoke('/v1/guide', requestBody, { tool_calls: [{ name: 'return_guidance', arguments: { ...expected, targetId: 'invented-control', confidence: 0.99 } }] });
  const json = await response.json();
  assert(json.status === 'not_found', 'invented target must be rejected');
}

async function runContextTargetRejectionCase() {
  const body = { ...requestBody, elements: [...requestBody.elements, { id: 'c1', name: '説明文', controlType: 'Text', processName: 'explorer', interactable: false, enabled: true }] };
  const response = await invoke('/v1/guide', body, { tool_calls: [{ name: 'return_guidance', arguments: { ...expected, targetId: 'c1', confidence: 0.99 } }] });
  const json = await response.json();
  assert(json.status === 'not_found', 'context-only UI text must never become a target');
}

async function runDoubleClickNormalizationCase() {
  const body = { ...requestBody, elements: [{ ...requestBody.elements[0], controlType: 'ListItem' }] };
  const response = await invoke('/v1/guide', body, { tool_calls: [{ name: 'return_guidance', arguments: { ...expected, instruction: 'ここをダブルクリックしてください。', action: 'left_click' } }] });
  const json = await response.json();
  assert(json.action === 'double_click', 'desktop-like double-click wording must normalize');
  assert(json.instruction.includes('2回押'), 'double press must be described physically');
  assert(!json.instruction.includes('ダブルクリック'), 'double-click jargon must be removed');
}

async function runHiddenTaskbarExcelCase() {
  const body = {
    request: 'エクセルを開きたい', history: [], systemContext: { ...baseSystem, ForegroundProcess: 'chrome', ForegroundTitle: 'Google Chrome', RunningApps: ['chrome'] },
    elements: [{ id: 'u9', name: 'ごみ箱', controlType: 'ListItem', processName: 'explorer', interactable: true, enabled: true, x: 20, y: 20, width: 60, height: 60 }]
  };
  const response = await invoke('/v1/guide', body, { tool_calls: [{ name: 'return_guidance', arguments: expected }] });
  const json = await response.json();
  assert(json.status === 'target' && json.action === 'press_key' && json.key === 'Windows', 'missing Excel must use Windows key, not an unrelated visible object');
  assert(json.targetId === null, 'keyboard guidance must not invent a screen target');
}

async function runBrowserNewTabCase() {
  const body = {
    request: 'YouTubeが見たい', history: [],
    systemContext: {
      ForegroundProcess: 'chrome', ForegroundTitle: 'Yahoo! JAPAN - Google Chrome', ForegroundProcessId: 2, TaskbarVisible: false, RunningApps: ['chrome'],
      Browser: { ProcessName: 'chrome', WindowTitle: 'Yahoo! JAPAN - Google Chrome', Url: 'https://www.yahoo.co.jp/', Domain: 'www.yahoo.co.jp', Https: true, AddressFieldFocused: false }
    },
    elements: [{ id: 'u2', name: '検索', controlType: 'Edit', processName: 'chrome', interactable: true, enabled: true, keyboardFocusable: true, focused: false, x: 10, y: 10, width: 300, height: 40 }]
  };
  const response = await invoke('/v1/guide', body, { tool_calls: [{ name: 'return_guidance', arguments: expected }] });
  const json = await response.json();
  assert(json.action === 'press_key' && json.key === 'Ctrl+T', 'website navigation from another page must open a new tab first');
  assert(!/youtube\.com/i.test(json.instruction), 'website guidance must not ask beginners to type a domain directly');
}

async function runProfileChoiceCase() {
  const body = {
    request: 'YouTubeが見たい', history: [],
    systemContext: { ...baseSystem, ForegroundProcess: 'chrome', ForegroundTitle: 'Google Chrome', RunningApps: ['chrome'], Browser: { ProcessName: 'chrome', WindowTitle: 'Google Chrome', Url: null, Domain: null, Https: null, AddressFieldFocused: false } },
    elements: [
      { id: 'c1', name: 'Chrome はどなたが使用しますか？', controlType: 'Text', processName: 'chrome', interactable: false, enabled: true },
      { id: 'u1', name: '正二郎', controlType: 'Button', processName: 'chrome', interactable: true, enabled: true },
      { id: 'u2', name: 'ゲストモード', controlType: 'Button', processName: 'chrome', interactable: true, enabled: true }
    ]
  };
  const response = await invoke('/v1/guide', body, { tool_calls: [{ name: 'return_guidance', arguments: expected }] });
  const json = await response.json();
  assert(json.status === 'clarify', 'profile/account branch must ask instead of choosing');
}

async function runVisionCase() {
  const modelResponse = { tool_calls: [{ name: 'return_vision_guidance', arguments: { status: 'target', label: 'テスト', instruction: 'ここを左クリックしてください。', question: null, x: 800, y: 850, width: 55, height: 70, confidence: 0.91, observedDomain: null, sponsored: false } }] };
  const response = await invoke('/v1/vision-guide', { request: '普通の操作', history: [], systemContext: baseSystem, image: 'data:image/png;base64,AAAA' }, modelResponse);
  const json = await response.json();
  assert(response.status === 200 && json.status === 'target', 'vision basic case must remain valid');
  assert(json.x === 800 && json.width === 55, 'vision coordinates must be preserved');
}

async function runSponsoredVisionRejectionCase() {
  const systemContext = {
    ForegroundProcess: 'chrome', ForegroundTitle: 'youtube - Google 検索', ForegroundProcessId: 2, TaskbarVisible: false, RunningApps: ['chrome'],
    Browser: { ProcessName: 'chrome', WindowTitle: 'youtube - Google 検索', Url: 'https://www.google.com/search?q=youtube', Domain: 'www.google.com', Https: true, AddressFieldFocused: false }
  };
  const modelResponse = { tool_calls: [{ name: 'return_vision_guidance', arguments: { status: 'target', label: 'YouTube', instruction: 'ここを押してください。', question: null, x: 100, y: 200, width: 200, height: 60, confidence: 0.95, observedDomain: 'youtube.com', sponsored: true } }] };
  const response = await invoke('/v1/vision-guide', { request: 'YouTubeが見たい', history: [], systemContext, image: 'data:image/png;base64,AAAA' }, modelResponse);
  const json = await response.json();
  assert(json.status === 'not_found', 'sponsored search result must be rejected for known-site navigation');
}

function invoke(path, body, modelResponse) {
  return worker.fetch(new Request(`https://unit.test${path}`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify(body) }), {
    HELPSYS_MODEL: '@cf/google/gemma-4-26b-a4b-it',
    AI: { run: async () => modelResponse }
  });
}

function assert(condition, message) { if (!condition) throw new Error(message); }
