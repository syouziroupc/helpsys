import guard from './reliability-v4-guard.js';

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
