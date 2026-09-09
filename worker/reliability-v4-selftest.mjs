import guard, { guardSecretClarification } from './reliability-v4-guard.js';

function assert(condition, message) {
  if (!condition) throw new Error(message);
}

async function guide(body) {
  const request = new Request('https://example.test/v1/guide', {
    method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify(body)
  });
  const response = await guard.fetch(request, {}, {});
  assert(response.status === 200, `unexpected status ${response.status}`);
  return response.json();
}

const background = await guide({
  request: 'Excelを開いて',
  history: [],
  systemContext: { foregroundProcess: 'notepad', foregroundProcessId: 22, runningApps: ['excel', 'notepad'], taskbarVisible: true },
  elements: [{ id: 'u1', name: '本文', automationId: 'Editor', className: 'Edit', controlType: 'Edit', processName: 'notepad', interactable: true, enabled: true, keyboardFocusable: true, focused: true, password: false, x: 10, y: 10, width: 400, height: 300 }]
});
assert(background.status !== 'done', 'background Excel must not be treated as completed');
assert(background.action === 'press_key' && /windows/i.test(background.key || ''), 'background app should be brought forward through a safe route');

const foreground = await guide({
  request: 'Excelを開いて',
  history: [],
  systemContext: { foregroundProcess: 'excel', foregroundProcessId: 44, runningApps: ['excel'], taskbarVisible: true },
  elements: [{ id: 'u1', name: 'Microsoft Excel', automationId: 'Main', className: 'XLMAIN', controlType: 'Window', processName: 'excel', interactable: false, enabled: true, keyboardFocusable: false, focused: false, password: false, x: 0, y: 0, width: 1000, height: 700 }]
});
assert(foreground.status === 'done', 'foreground Excel should remain completed');

console.log('reliability v4 guard self-test passed.');

const secretClarify = guardSecretClarification({
  status: 'clarify', question: 'パスワードを入力してください。', instruction: '', confidence: 0.9
});
assert(secretClarify?.status === 'not_found', 'secret clarification must be blocked');
const normalClarify = guardSecretClarification({
  status: 'clarify', question: 'どのフォルダーを開きたいですか？', instruction: '', confidence: 0.9
});
assert(normalClarify === null, 'ordinary clarification must remain allowed');
console.log('reliability v4 secret-clarification self-test passed.');

let aiCalls = 0;
const educationEnv = {
  AI: {
    async run() {
      aiCalls++;
      return {
        tool_calls: [{
          name: 'return_education_assist',
          arguments: { status: 'hint', message: 'まず、画面の見出しを確認してください。', nextHintLevel: 2 }
        }]
      };
    }
  }
};

const educationRequest = new Request('https://example.test/v1/education/assist', {
  method: 'POST',
  headers: { 'content-type': 'application/json' },
  body: JSON.stringify({
    stage: 'practice',
    lessonId: 'mouse-1',
    lessonTitle: 'マウスの基本',
    objective: '目的の場所を自分で選ぶ',
    message: 'ヒントをください',
    hintLevel: 1
  })
});
const educationResponse = await guard.fetch(educationRequest, educationEnv, {});
assert(educationResponse.status === 200, `integrated Education route returned ${educationResponse.status}`);
const educationBody = await educationResponse.json();
assert(educationBody.status === 'hint', 'integrated Education route must return an Education hint');
assert(aiCalls === 1, 'Education practice route must call the configured AI exactly once');

const testRequest = new Request('https://example.test/v1/education/assist', {
  method: 'POST',
  headers: { 'content-type': 'application/json' },
  body: JSON.stringify({
    stage: 'test',
    lessonId: 'mouse-1',
    lessonTitle: 'マウスの基本',
    objective: '確認テスト',
    message: '答えを教えて',
    hintLevel: 1
  })
});
const testResponse = await guard.fetch(testRequest, educationEnv, {});
const testBody = await testResponse.json();
assert(testBody.status === 'blocked', 'Education test route must remain non-generative');
assert(aiCalls === 1, 'Education test route must not call AI');
console.log('production HelpSys Education route self-test passed.');
