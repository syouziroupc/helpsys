import assert from 'node:assert/strict';
import guard from './deliberation-guard.js';

let calls = 0;
let capturedOptions = null;

const env = {
  HELPSYS_MODEL: '@cf/google/gemma-4-26b-a4b-it',
  AI: {
    async run(_model, options) {
      calls++;
      capturedOptions = options;
      return {
        tool_calls: [{
          name: 'return_guidance',
          arguments: {
            status: 'target',
            targetId: 'e1',
            action: 'left_click',
            instruction: '青い枠の「続ける」を、マウスの左ボタンで1回押してください。',
            question: null,
            key: null,
            confidence: 0.93
          }
        }]
      };
    }
  }
};

const request = new Request('https://helpsys.test/v1/guide', {
  method: 'POST',
  headers: { 'content-type': 'application/json' },
  body: JSON.stringify({
    request: '画面に表示されている続けるボタンを押したい',
    history: [],
    systemContext: {
      foregroundProcess: 'sampleapp',
      foregroundTitle: 'Sample',
      taskbarVisible: true,
      runningApps: ['sampleapp'],
      browser: null
    },
    elements: [{
      id: 'e1',
      name: '続ける',
      automationId: 'continue',
      className: 'Button',
      controlType: 'Button',
      processName: 'sampleapp',
      interactable: true,
      enabled: true,
      keyboardFocusable: true,
      focused: false,
      password: false,
      x: 100,
      y: 100,
      width: 120,
      height: 40
    }]
  })
});

const response = await guard.fetch(request, env, {});
assert.equal(response.status, 200);
const decision = await response.json();
assert.equal(calls, 1, 'structured guidance must invoke Workers AI exactly once');
assert.equal(capturedOptions?.chat_template_kwargs?.enable_thinking, true, 'the single planner call must use reasoning');
assert.equal(decision.status, 'target');
assert.equal(decision.targetId, 'e1');

console.log('deliberation single-pass selftest: ok');
