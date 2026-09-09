import { validateQualityDecision } from '../worker/quality-guide.js';

function assert(condition, message) {
  if (!condition) throw new Error(message);
}

const capture = { x: -1920, y: 0, width: 1920, height: 1080 };
const targetBase = {
  id: 'target', name: '設定', automationId: 'Settings', className: 'Button',
  controlType: 'Button', processName: 'app', interactable: true, enabled: true,
  keyboardFocusable: true, focused: false, password: false,
  x: -300, y: 200, width: 120, height: 40
};
const rawTarget = {
  status: 'target', targetId: 'target', action: 'left_click',
  instruction: '青い枠の設定を1回押してください。', question: null, key: null, inputText: null,
  confidence: 0.98, x: 0, y: 0, width: 0, height: 0,
  screenConfirmed: true, visualEvidence: '設定ボタンが画面に見える', observedDomain: null, sponsored: false
};

let result = validateQualityDecision(rawTarget, [targetBase], null, false, capture);
assert(result.status === 'target', 'target whose center is inside the captured monitor must remain actionable');

const sliver = { ...targetBase, x: -10, width: 60 };
result = validateQualityDecision(rawTarget, [sliver], null, false, capture);
assert(result.status === 'not_found', 'a target with only a sliver inside the captured monitor must not be screen-confirmed');

const offscreenStructured = { ...targetBase, x: 40, width: 120 };
result = validateQualityDecision(
  { ...rawTarget, screenConfirmed: false, visualEvidence: '', confidence: 0.94 },
  [offscreenStructured], null, false, capture);
assert(result.status === 'target' && result.screenConfirmed === false,
  'high-confidence structured UIA guidance may survive outside the screenshot only when it does not claim visual confirmation');

const password = {
  ...targetBase, id: 'secret', automationId: 'PasswordBox', controlType: 'Edit',
  focused: true, password: true, x: -500
};
result = validateQualityDecision({
  ...rawTarget, targetId: 'secret', action: 'type_text', key: 'Enter', inputText: 'hunter2'
}, [password], null, false, capture);
assert(result.status === 'not_found', 'type_text into a password element must always be rejected');

const edit = {
  ...targetBase, id: 'edit', automationId: 'SearchBox', controlType: 'Edit',
  focused: true, password: false, x: -500
};
result = validateQualityDecision({
  ...rawTarget, targetId: 'edit', action: 'type_text', key: 'Enter', inputText: null
}, [edit], null, false, capture);
assert(result.status === 'not_found', 'type_text without exact inputText machine data must be rejected');

result = validateQualityDecision({
  ...rawTarget, inputText: 'unexpected text'
}, [targetBase], null, false, capture);
assert(result.status === 'not_found', 'non-type action carrying inputText must be rejected');

console.log('HelpSys quality validator boundary contract passed.');
