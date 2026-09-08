import worker from './start-state-guard.js';

await desktopChromeBeatsWindows();
await startChromeBeatsSearch();
await openStartNeverPressesWindowsAgain();
await visibleAddressFieldBeatsCtrlL();
console.log('HelpSys visible-first self-test passed.');

async function desktopChromeBeatsWindows() {
  const body = {
    request: 'YouTubeが見たい', history: [],
    systemContext: { ForegroundProcess: 'explorer', ForegroundTitle: '', ForegroundProcessId: 1, TaskbarVisible: true, RunningApps: [], Browser: null },
    elements: [
      { id: 'chrome', name: 'Google Chrome', controlType: 'ListItem', processName: 'explorer', interactable: true, enabled: true, x: 120, y: 100, width: 72, height: 90 },
      { id: 'start', name: 'スタート', controlType: 'Button', processName: 'explorer', interactable: true, enabled: true, x: 900, y: 1030, width: 50, height: 45 }
    ]
  };
  const json = await invoke(body);
  assert(json.status === 'target' && json.targetId === 'chrome', 'visible Chrome must beat Windows/Start fallback');
  assert(json.action === 'double_click', 'desktop Chrome shortcut should use two quick left presses');
}

async function startChromeBeatsSearch() {
  const body = {
    request: 'YouTubeが見たい', history: [],
    systemContext: { ForegroundProcess: 'StartMenuExperienceHost', ForegroundTitle: 'スタート', ForegroundProcessId: 10, TaskbarVisible: true, RunningApps: [], Browser: null },
    elements: [
      { id: 'search', name: 'アプリ、設定、ドキュメントの検索', automationId: 'SearchBox', controlType: 'Edit', processName: 'StartMenuExperienceHost', interactable: true, enabled: true, focused: false, x: 500, y: 200, width: 600, height: 45 },
      { id: 'chrome', name: 'Google Chrome', controlType: 'Button', processName: 'StartMenuExperienceHost', interactable: true, enabled: true, x: 700, y: 500, width: 90, height: 90 }
    ]
  };
  const json = await invoke(body);
  assert(json.status === 'target' && json.targetId === 'chrome', 'visible Chrome in Start must beat the search box');
  assert(json.action === 'left_click', 'Start pinned browser should use one left press');
}

async function openStartNeverPressesWindowsAgain() {
  const body = {
    request: 'YouTubeが見たい', history: [],
    systemContext: { ForegroundProcess: 'StartMenuExperienceHost', ForegroundTitle: 'スタート', ForegroundProcessId: 10, TaskbarVisible: true, RunningApps: [], Browser: null },
    elements: [
      { id: 'context', name: 'ピン留め済み', controlType: 'Text', processName: 'StartMenuExperienceHost', interactable: false, enabled: true, x: 500, y: 300, width: 200, height: 30 }
    ]
  };
  const json = await invoke(body);
  assert(!(json.action === 'press_key' && /windows/i.test(String(json.key || ''))), 'Windows must never be requested while Start is already open');
}

async function visibleAddressFieldBeatsCtrlL() {
  const body = {
    request: 'YouTubeが見たい', history: [],
    systemContext: {
      ForegroundProcess: 'chrome', ForegroundTitle: '新しいタブ - Google Chrome', ForegroundProcessId: 2, TaskbarVisible: true, RunningApps: ['chrome'],
      Browser: { ProcessName: 'chrome', WindowTitle: '新しいタブ - Google Chrome', Url: 'chrome://newtab/', Domain: null, Https: null, AddressFieldFocused: false }
    },
    elements: [
      { id: 'address', name: 'Google で検索するか URL を入力する', automationId: 'address', className: 'Omnibox', controlType: 'Edit', processName: 'chrome', interactable: true, enabled: true, keyboardFocusable: true, focused: false, x: 200, y: 50, width: 900, height: 40 }
    ]
  };
  const json = await invoke(body);
  assert(json.status === 'target' && json.targetId === 'address' && json.action === 'left_click', 'visible address/search field must beat Ctrl+L');
}

async function invoke(body) {
  const response = await worker.fetch(new Request('https://unit.test/v1/guide', {
    method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify(body)
  }), {
    HELPSYS_MODEL: '@cf/google/gemma-4-26b-a4b-it',
    AI: {
      run: async () => ({ tool_calls: [{ name: 'return_guidance', arguments: {
        status: 'target', targetId: null, action: 'press_key', instruction: 'Windowsキーを押してください。', question: null, key: 'Windows', confidence: 0.99
      } }] })
    }
  });
  assert(response.status === 200, `expected HTTP 200, got ${response.status}`);
  return response.json();
}

function assert(condition, message) {
  if (!condition) throw new Error(message);
}
