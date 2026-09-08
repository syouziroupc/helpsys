import worker from './index.js';

const requestBody = {
  request: 'YouTubeを見たい',
  history: [],
  elements: [
    {
      id: 'u1',
      name: 'Google Chrome',
      automationId: 'Chrome',
      className: 'Chrome_WidgetWin_1',
      controlType: 'Button',
      processName: 'explorer',
      enabled: true,
      keyboardFocusable: true,
      focused: false,
      password: false
    }
  ]
};

const expected = {
  status: 'target',
  targetId: 'u1',
  action: 'left_click',
  instruction: 'ここを左クリックしてください。',
  question: null,
  key: null,
  confidence: 0.93
};

await runCase('traditional root tool_calls', {
  tool_calls: [{ name: 'return_guidance', arguments: expected }]
});

await runCase('chat completions tool_calls', {
  choices: [{
    message: {
      tool_calls: [{
        type: 'function',
        function: {
          name: 'return_guidance',
          arguments: JSON.stringify(expected)
        }
      }]
    }
  }]
});

await runCase('defensive text JSON', {
  choices: [{ message: { content: JSON.stringify(expected) } }]
});

await runInvalidTargetCase();
console.log('HelpSys Worker self-test passed.');

async function runCase(name, modelResponse) {
  const response = await invoke(modelResponse);
  const json = await response.json();
  assert(response.status === 200, `${name}: expected HTTP 200, got ${response.status}`);
  assert(json.status === 'target', `${name}: expected target, got ${json.status}`);
  assert(json.targetId === 'u1', `${name}: expected u1, got ${json.targetId}`);
}

async function runInvalidTargetCase() {
  const response = await invoke({
    tool_calls: [{
      name: 'return_guidance',
      arguments: { ...expected, targetId: 'invented-control', confidence: 0.99 }
    }]
  });
  const json = await response.json();
  assert(json.status === 'not_found', 'invented target must be rejected');
  assert(json.targetId === null, 'invented target id must not escape validation');
}

function invoke(modelResponse) {
  return worker.fetch(
    new Request('https://unit.test/v1/guide', {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify(requestBody)
    }),
    {
      HELPSYS_MODEL: '@cf/google/gemma-4-26b-a4b-it',
      AI: { run: async () => modelResponse }
    }
  );
}

function assert(condition, message) {
  if (!condition) throw new Error(message);
}
