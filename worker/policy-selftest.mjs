import worker from './entry.js';

const baseSystem = {
  ForegroundProcess: 'chrome', ForegroundTitle: 'Google Chrome', ForegroundProcessId: 2,
  TaskbarVisible: false, RunningApps: ['chrome'],
  Browser: { ProcessName: 'chrome', WindowTitle: 'Google Chrome', Url: null, Domain: null, Https: null, AddressFieldFocused: false }
};

await runGuestFallback();
await runIdentitySensitiveChoice();
await runAnsweredChoice();
console.log('HelpSys beginner policy self-test passed.');

async function runGuestFallback() {
  const body = profileBody('YouTubeが見たい', []);
  const json = await invoke(body);
  assert(json.status === 'target', 'ordinary browsing should not stop on profile selection');
  assert(json.targetId === 'guest', 'unknown browser user should fall back to guest mode');
  assert(/ゲストモード/.test(json.instruction), 'guest mode must be explained visibly');
}

async function runIdentitySensitiveChoice() {
  const body = profileBody('Gmailを開いてメールを見たい', []);
  const json = await invoke(body);
  assert(json.status === 'clarify', 'identity-sensitive work must still ask which account/profile to use');
}

async function runAnsweredChoice() {
  const body = profileBody('Gmailを開いてメールを見たい', [
    { step: 0, action: 'clarification_answer', target: '正二郎商事', instruction: 'どの名前を使うか教えてください。' }
  ]);
  const json = await invoke(body);
  assert(json.status === 'target' && json.targetId === 'business', 'an explicit user answer must resolve the branch instead of asking again');
}

function profileBody(request, history) {
  return {
    request,
    history,
    systemContext: baseSystem,
    elements: [
      { id: 'context', name: 'Chrome はどなたが使用しますか？', controlType: 'Text', processName: 'chrome', interactable: false, enabled: true },
      { id: 'personal', name: '正二郎', controlType: 'Button', processName: 'chrome', interactable: true, enabled: true },
      { id: 'business', name: '正二郎商事', controlType: 'Button', processName: 'chrome', interactable: true, enabled: true },
      { id: 'guest', name: 'ゲストモード', controlType: 'Button', processName: 'chrome', interactable: true, enabled: true }
    ]
  };
}

async function invoke(body) {
  const response = await worker.fetch(new Request('https://unit.test/v1/guide', {
    method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify(body)
  }), {
    HELPSYS_MODEL: '@cf/google/gemma-4-26b-a4b-it',
    AI: {
      run: async () => ({
        tool_calls: [{
          name: 'return_guidance',
          arguments: { status: 'clarify', targetId: null, action: 'none', instruction: '', question: 'どの名前を使いますか？', key: null, confidence: 0.99 }
        }]
      })
    }
  });
  return response.json();
}

function assert(condition, message) {
  if (!condition) throw new Error(message);
}
