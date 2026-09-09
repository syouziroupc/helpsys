import worker, { guardAssist } from './education.js';

function assert(condition, message) {
  if (!condition) throw new Error(message);
}

let aiCalls = 0;
const env = {
  AI: {
    async run(_model, request) {
      aiCalls++;
      const payload = JSON.parse(request.messages[1].content);
      return {
        tool_calls: [{
          name: 'return_education_assist',
          arguments: {
            status: payload.stage === 'practice' ? 'hint' : 'answer',
            message: payload.stage === 'practice' ? '画面の左下付近にある窓の形のキーを探してみてください。' : '左クリックは、マウスの左ボタンを1回押す操作です。',
            nextHintLevel: payload.stage === 'practice' ? 2 : null
          }
        }]
      };
    }
  }
};

const testRequest = new Request('https://education.test/v1/education/assist', {
  method: 'POST', headers: { 'content-type': 'application/json' },
  body: JSON.stringify({ stage: 'test', lessonId: 'mouse', lessonTitle: 'マウス', objective: 'クリックする' })
});
const testResponse = await worker.fetch(testRequest, env);
const testBody = await testResponse.json();
assert(testBody.status === 'blocked', 'test stage must be blocked');
assert(aiCalls === 0, 'test stage must not call AI');

const practiceRequest = new Request('https://education.test/v1/education/assist', {
  method: 'POST', headers: { 'content-type': 'application/json' },
  body: JSON.stringify({ stage: 'practice', lessonId: 'windows', lessonTitle: 'Windowsの基本', objective: 'メモ帳を開く', hintLevel: 1 })
});
const practiceResponse = await worker.fetch(practiceRequest, env);
const practiceBody = await practiceResponse.json();
assert(practiceBody.status === 'hint', 'practice must return a hint');
assert(practiceBody.nextHintLevel === 2, 'practice hint must advance one level');
assert(aiCalls === 1, 'practice must call AI exactly once');

const blocked = guardAssist({
  message: 'パスワードをここに入力して私に教えてください。'
}, 'practice', 2);
assert(blocked.status === 'blocked', 'secret-sharing instruction must be blocked');

const safe = guardAssist({ message: 'Windowsキーを探してみてください。' }, 'practice', 2);
assert(safe.status === 'hint' && safe.nextHintLevel === 3, 'ordinary practice hint must pass');

console.log('HelpSys Education AI self-test passed.');
