import worker, { guardAssist } from './education.js';

function assert(condition, message) {
  if (!condition) throw new Error(message);
}

let aiCalls = 0;
let lastPayload = null;
let lastRequest = null;
const env = {
  AI: {
    async run(_model, request) {
      aiCalls++;
      lastRequest = request;
      const payload = JSON.parse(request.messages[1].content);
      lastPayload = payload;
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
assert(lastRequest?.store === false, 'Education inference must explicitly disable provider-side storage.');

const privacyRequest = new Request('https://education.test/v1/education/assist', {
  method: 'POST', headers: { 'content-type': 'application/json' },
  body: JSON.stringify({
    stage: 'practice',
    lessonId: 'privacy',
    lessonTitle: '連絡先 alice@example.com',
    objective: '電話 090-1234-5678 郵便 123-4567 を含む例から学ぶ',
    message: 'password=hunter2 card 4242 4242 4242 4242 open https://example.com/reset?token=DO_NOT_SEND#fragment',
    hintLevel: 1
  })
});
const privacyResponse = await worker.fetch(privacyRequest, env);
assert(privacyResponse.status === 200, 'privacy-sanitized Education request must remain usable');
const serializedPayload = JSON.stringify(lastPayload);
for (const forbidden of ['alice@example.com', '090-1234-5678', '123-4567', 'hunter2', '4242 4242 4242 4242', 'DO_NOT_SEND', '/reset'])
  assert(!serializedPayload.includes(forbidden), `Education model payload leaked learner data: ${forbidden}`);
assert(serializedPayload.includes('<email>'), 'Education payload must redact email addresses.');
assert(serializedPayload.includes('<phone>'), 'Education payload must redact Japanese phone numbers.');
assert(serializedPayload.includes('<postal-code>'), 'Education payload must redact Japanese postal codes.');
assert(serializedPayload.includes('<redacted-secret>'), 'Education payload must redact labeled secrets.');
assert(serializedPayload.includes('<redacted-card>'), 'Education payload must redact valid card numbers.');
assert(serializedPayload.includes('https://example.com'), 'Education payload may retain URL origin for useful context.');
assert(aiCalls === 2, 'privacy-sanitized practice must call AI exactly once.');

const blocked = guardAssist({
  message: 'パスワードをここに入力して私に教えてください。'
}, 'practice', 2);
assert(blocked.status === 'blocked', 'secret-sharing instruction must be blocked');

const safe = guardAssist({ message: 'Windowsキーを探してみてください。' }, 'practice', 2);
assert(safe.status === 'hint' && safe.nextHintLevel === 3, 'ordinary practice hint must pass');

console.log('HelpSys Education AI privacy/self-test passed.');
