from pathlib import Path


def replace_once_if_needed(text: str, old: str, new: str, label: str) -> str:
    if new in text:
        return text
    if old not in text:
        raise SystemExit(f'{label} anchor not found')
    return text.replace(old, new, 1)


# 1/2: make the shared Windows choice detector history-aware.
path = Path('worker/windows-knowledge.js')
text = path.read_text(encoding='utf-8')
text = replace_once_if_needed(
    text,
    "  const branch = detectChoiceBranch(elements, systemContext);",
    "  const branch = detectChoiceBranch(elements, history, systemContext);",
    'choice branch call',
)

if 'function detectChoiceBranch(elements, history, systemContext) {' not in text:
    start = text.find('function detectChoiceBranch(elements, systemContext) {')
    end = text.find('\nfunction buildKnowledge(goal) {', start)
    if start < 0 or end < 0:
        raise SystemExit('detectChoiceBranch block anchor not found')

    replacement = r'''function detectChoiceBranch(elements, history, systemContext) {
  const foreground = String(systemContext?.foregroundProcess || '').toLowerCase();
  const relevant = elements.filter(x => !foreground || String(x.processName || '').toLowerCase() === foreground);
  const text = `${systemContext?.foregroundTitle || ''} ${relevant.map(x => x.name).join(' ')}`;
  const accountChoice = /(どなたが使用|プロファイル.*選|プロフィール.*選|アカウント(?:の)?選択|アカウント.*選|ユーザー.*選|別のアカウントを使用|ゲストモード|choose\s+an?\s+account|select\s+an?\s+account|who(?:'s|\s+is)\s+using\s+chrome|use\s+another\s+account|guest\s+mode)/i.test(text);

  if (accountChoice) {
    const answer = latestChoiceAnswer(history);
    if (answer && !looksRedactedChoiceAnswer(answer)) {
      const matches = findAnsweredChoiceTargets(relevant, answer);
      if (matches.length === 1) {
        const selected = matches[0];
        return task('choice', CORE_KNOWLEDGE, {
          status: 'target', targetId: selected.id, action: 'left_click',
          instruction: `青い枠の「${safeChoiceLabel(selected.name)}」で、マウスの左ボタンを1回押してください。`,
          question: null, key: null, confidence: 0.99
        }, false, new Set([selected.id]));
      }
    }

    return task('choice', CORE_KNOWLEDGE, {
      status: 'clarify', targetId: null, action: 'none', instruction: '',
      question: '使う人を選ぶ画面です。勝手に選ばないので、画面に出ている名前のうち、どの名前を使うか教えてください。', key: null, confidence: 0.99
    }, false, null);
  }

  if (/(上書き|置き換えますか|削除しますか|既定.*ブラウ|アクセスを許可|許可しますか|購入|支払い|注文を確定)/i.test(text)) {
    return task('choice', CORE_KNOWLEDGE, {
      status: 'clarify', targetId: null, action: 'none', instruction: '',
      question: 'この画面は選び方によって結果が変わります。HelpSysでは勝手に決めません。何をしたいか教えてください。', key: null, confidence: 0.99
    }, false, null);
  }
  return null;
}

function latestChoiceAnswer(history) {
  if (!Array.isArray(history)) return '';
  const item = [...history].reverse().find(entry =>
    String(entry?.action ?? entry?.Action ?? '').toLowerCase() === 'clarification_answer');
  return String(
    item?.targetName ?? item?.TargetName ??
    item?.target ?? item?.Target ?? ''
  ).trim();
}

function findAnsweredChoiceTargets(elements, answer) {
  const normalizedAnswer = normalizeChoiceText(answer);
  if (!normalizedAnswer) return [];

  const candidates = elements.filter(element =>
    element?.interactable !== false &&
    element?.enabled !== false &&
    String(element?.id || '').trim() &&
    String(element?.name || '').trim() &&
    !/(ゲストモード|guest\s+mode|別のアカウントを使用|use\s+another\s+account|アカウントを追加|add\s+account|その他|more|設定|settings|閉じる|close)/i.test(String(element?.name || '')));

  const exact = candidates.filter(element => normalizeChoiceText(element.name) === normalizedAnswer);
  if (exact.length === 1) return exact;
  if (exact.length > 1) return [];

  const partial = candidates.filter(element => {
    const name = normalizeChoiceText(element.name);
    return name && (name.includes(normalizedAnswer) || normalizedAnswer.includes(name));
  });
  return partial.length === 1 ? partial : [];
}

function normalizeChoiceText(value) {
  return String(value || '')
    .toLowerCase()
    .normalize('NFKC')
    .replace(/[\s　「」『』\"'()（）\[\]【】<>＜＞]/g, '');
}

function looksRedactedChoiceAnswer(value) {
  const text = String(value || '').trim();
  return !text || /^<(?:email|phone|postal-code|redacted[^>]*)>$/i.test(text);
}

function safeChoiceLabel(value) {
  const label = String(value || '').replace(/[\r\n\t]+/g, ' ').trim();
  if (!label) return '選んだ名前';
  if (/[\w.+-]+@[\w.-]+\.[A-Za-z]{2,}/.test(label)) return '選んだアカウント';
  return label.length <= 60 ? label : label.slice(0, 60);
}
'''
    text = text[:start] + replacement + text[end:]

path.write_text(text, encoding='utf-8')


# Quality First must honor deterministic choice resolution before invoking the model.
quality_path = Path('worker/quality-guide.js')
quality = quality_path.read_text(encoding='utf-8')
old_task = """    const task = buildWindowsTaskContext(goal, elements, history, systemContext);\n    const canonical = compactCanonical(task, recoveryMode);\n"""
new_task = """    const task = buildWindowsTaskContext(goal, elements, history, systemContext);\n    if (task?.kind === 'choice' && task?.deterministic) {\n      return json(validateQualityDecision(\n        qualityRawFromTaskDecision(task.deterministic),\n        elements,\n        task,\n        recoveryMode));\n    }\n    const canonical = compactCanonical(task, recoveryMode);\n"""
quality = replace_once_if_needed(quality, old_task, new_task, 'quality deterministic choice short-circuit')

if 'function qualityRawFromTaskDecision(decision) {' not in quality:
    marker = '\nexport function validateQualityDecision(raw, elements, task, recoveryMode = false) {'
    if marker not in quality:
        raise SystemExit('quality validation marker not found')
    helper = r'''

function qualityRawFromTaskDecision(decision) {
  return {
    status: String(decision?.status || 'not_found'),
    targetId: decision?.targetId ?? null,
    action: String(decision?.action || 'none'),
    instruction: String(decision?.instruction || ''),
    question: decision?.question ?? null,
    key: decision?.key ?? null,
    confidence: Number.isFinite(Number(decision?.confidence)) ? Number(decision.confidence) : 0.99,
    x: 0, y: 0, width: 0, height: 0,
    screenConfirmed: false,
    visualEvidence: '',
    observedDomain: null,
    sponsored: false
  };
}
'''
    quality = quality.replace(marker, helper + marker, 1)

quality_path.write_text(quality, encoding='utf-8')


# Regression coverage for JP/EN chooser and redacted identities.
test_path = Path('worker/quality-guide-selftest.mjs')
tests = test_path.read_text(encoding='utf-8')
old_ask = """async function ask(body) {\n  lastInvocation = null;\n  const request = new Request('https://example.test/v1/quality-guide', {\n    method: 'POST',\n    headers: { 'content-type': 'application/json' },\n    body: JSON.stringify({ image: 'data:image/png;base64,AAAA', history: [], ...body })\n  });\n  const response = await quality.fetch(request, env, {});\n  assert(response.status === 200, `unexpected quality response ${response.status}`);\n  const value = await response.json();\n  assert(lastInvocation?.args?.image?.startsWith('data:image/png;base64,'), 'quality planner must send the screenshot to the model');\n  assert(lastInvocation?.args?.store === false, 'quality planner must explicitly disable model-side storage when supported.');\n  return value;\n}\n"""
new_ask = """async function ask(body, expectModel = true) {\n  lastInvocation = null;\n  const request = new Request('https://example.test/v1/quality-guide', {\n    method: 'POST',\n    headers: { 'content-type': 'application/json' },\n    body: JSON.stringify({ image: 'data:image/png;base64,AAAA', history: [], ...body })\n  });\n  const response = await quality.fetch(request, env, {});\n  assert(response.status === 200, `unexpected quality response ${response.status}`);\n  const value = await response.json();\n  if (expectModel) {\n    assert(lastInvocation?.args?.image?.startsWith('data:image/png;base64,'), 'quality planner must send the screenshot to the model');\n    assert(lastInvocation?.args?.store === false, 'quality planner must explicitly disable model-side storage when supported.');\n  } else {\n    assert(lastInvocation === null, 'deterministic choice handling must not call the model');\n  }\n  return value;\n}\n"""
tests = replace_once_if_needed(tests, old_ask, new_ask, 'quality selftest ask')

sentinel = "answered account choice must resolve deterministically on the quality path"
if sentinel not in tests:
    marker = "\nconsole.log('HelpSys multisource evidence-fusion and route-recovery self-test passed.');"
    if marker not in tests:
        raise SystemExit('quality selftest final marker not found')
    added = r'''

value = await ask({
  request: 'Gmailを開いてメールを見たい',
  history: [{ step: 0, action: 'clarification_answer', targetName: '正二郎商事', instruction: 'どの名前を使うか教えてください。' }],
  systemContext: { foregroundProcess: 'chrome', foregroundProcessId: 91, foregroundTitle: 'アカウントの選択', runningApps: ['chrome'] },
  elements: [
    { id: 'choice-personal', name: '正二郎', controlType: 'Button', processName: 'chrome', interactable: true, enabled: true },
    { id: 'choice-business', name: '正二郎商事', controlType: 'Button', processName: 'chrome', interactable: true, enabled: true },
    { id: 'choice-other', name: '別のアカウントを使用', controlType: 'Button', processName: 'chrome', interactable: true, enabled: true }
  ]
}, false);
assert(value.status === 'target' && value.targetId === 'choice-business',
  'answered account choice must resolve deterministically on the quality path');

value = await ask({
  request: 'Gmailを開いてメールを見たい',
  systemContext: { foregroundProcess: 'chrome', foregroundProcessId: 92, foregroundTitle: 'Choose an account', runningApps: ['chrome'] },
  elements: [
    { id: 'choice-a', name: 'Personal', controlType: 'Button', processName: 'chrome', interactable: true, enabled: true },
    { id: 'choice-b', name: 'Business', controlType: 'Button', processName: 'chrome', interactable: true, enabled: true },
    { id: 'choice-other', name: 'Use another account', controlType: 'Button', processName: 'chrome', interactable: true, enabled: true }
  ]
}, false);
assert(value.status === 'clarify', 'English account chooser must be recognized as a user choice');

value = await ask({
  request: 'Gmailを開いてメールを見たい',
  history: [{ step: 0, action: 'clarification_answer', targetName: '<email>', instruction: 'どのアカウントを使いますか？' }],
  systemContext: { foregroundProcess: 'chrome', foregroundProcessId: 93, foregroundTitle: 'アカウントの選択', runningApps: ['chrome'] },
  elements: [
    { id: 'choice-a', name: '<email>', controlType: 'Button', processName: 'chrome', interactable: true, enabled: true },
    { id: 'choice-b', name: '<email>', controlType: 'Button', processName: 'chrome', interactable: true, enabled: true }
  ]
}, false);
assert(value.status === 'clarify', 'redacted account identity must never be guessed or auto-selected');
'''
    tests = tests.replace(marker, added + marker, 1)

test_path.write_text(tests, encoding='utf-8')
