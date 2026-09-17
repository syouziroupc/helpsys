import { buildWindowsTaskContext, guardDecisionForTask } from './windows-knowledge.js';
import { validateQualityDecision } from './quality-guide.js';

const DEFAULT_GEMINI_MODEL = 'gemini-3.8-flash';
const MAX_UI_ELEMENTS = 420;
const MAX_HISTORY = 12;
const MAX_IMAGE_CHARS = 6_500_000;

const decisionSchema = {
  type: 'object',
  properties: {
    status: { type: 'string', enum: ['target', 'clarify', 'done', 'not_found'] },
    targetId: { type: 'string' },
    action: { type: 'string', enum: ['left_click', 'double_click', 'type_text', 'press_key', 'none'] },
    instruction: { type: 'string' },
    question: { type: 'string' },
    key: { type: 'string' },
    confidence: { type: 'number', minimum: 0, maximum: 1 }
  },
  required: ['status', 'targetId', 'action', 'instruction', 'question', 'key', 'confidence'],
  additionalProperties: false
};

const qualitySchema = {
  type: 'object',
  properties: {
    status: { type: 'string', enum: ['target', 'clarify', 'done', 'not_found'] },
    targetId: { type: 'string' },
    action: { type: 'string', enum: ['left_click', 'double_click', 'type_text', 'press_key', 'none'] },
    instruction: { type: 'string' },
    question: { type: 'string' },
    key: { type: 'string' },
    confidence: { type: 'number', minimum: 0, maximum: 1 },
    x: { type: 'number', minimum: 0, maximum: 1000 },
    y: { type: 'number', minimum: 0, maximum: 1000 },
    width: { type: 'number', minimum: 0, maximum: 1000 },
    height: { type: 'number', minimum: 0, maximum: 1000 },
    screenConfirmed: { type: 'boolean' },
    visualEvidence: { type: 'string' },
    observedDomain: { type: 'string' },
    sponsored: { type: 'boolean' }
  },
  required: [
    'status', 'targetId', 'action', 'instruction', 'question', 'key', 'confidence',
    'x', 'y', 'width', 'height', 'screenConfirmed', 'visualEvidence', 'observedDomain', 'sponsored'
  ],
  additionalProperties: false
};

const structuredPrompt = `You are the primary Windows UI planner for HelpSys. The human performs every action.
Return one immediate next step as JSON matching the supplied schema.

Evidence rules:
- uiElements are current Windows accessibility objects. A current enabled/interactable element is direct evidence that the control exists even when a screenshot is not available.
- systemContext identifies the foreground app/window and browser domain. completedSteps are history only and never override current state.
- windowsKnowledge is a route reference. Do NOT reject a real current target merely because it differs from a canonical route.
- For launching an app, any grounded reversible bridge step is valid: choose a visible result, close an irrelevant non-security dialog, focus the relevant app, or use another current control that advances the goal.
- not_found is exceptional. Use it only when no current actionable step can be grounded. Do not use not_found for technical uncertainty that can be resolved from a real UI element.
- clarify is only for a genuine user decision such as account/identity, saved-data overwrite/delete, payment/purchase, permissions/defaults, or two materially different choices. Never use clarify to ask the user to diagnose HelpSys.
- Never invent target IDs, labels, state, domains, or completed actions.
- Never request passwords, PINs, OTPs, recovery keys, CVVs, private keys, or other secrets.
- Never bypass browser security/privacy/certificate/phishing warnings. For known sites, do not select ads or lookalike domains.
- targetId must be an existing current uiElements id for mouse/type actions. press_key may use an empty targetId.
- type_text is allowed only for a focused keyboard-focusable non-password field.
- Use short concrete Japanese instructions for a complete PC beginner.`;

const qualityPrompt = `You are the primary multimodal Windows UI planner for HelpSys. The human performs every action.
Return one immediate next step as JSON matching the supplied schema.

Resolve the CURRENT state from independent evidence instead of comparing it to an imagined route:
- screenshot establishes what is visually present and layout/coordinates.
- uiElements establish actual Windows control identity, state, focus and exact bounds.
- systemContext establishes foreground ownership and browser domain.
- completedSteps explain how the user arrived here but are not a required route.
- windowsKnowledge/canonicalConstraint are route references, not vetoes, except explicit security/domain/user-choice constraints.

Decision policy:
- Prefer a real current UI element id whenever an enabled/interactable element advances the goal. If UIA uniquely identifies it but the screenshot is visually ambiguous, target that id with screenConfirmed=false and high confidence; the desktop revalidates the control before showing guidance.
- For app-launch tasks, a valid current bridge action is allowed even when it differs from the standard route. Do not return not_found just because the canonical next control is absent.
- not_found is exceptional: use it only when neither UIA nor screenshot/system context can ground any next action.
- clarify only for a genuine user decision (account/identity, overwrite/delete, purchase/payment, permission/default choice, or materially different alternatives). Never ask the user to compensate for model uncertainty.
- screenConfirmed=true only when the screenshot itself supports the claim. A UIA-only target may use screenConfirmed=false.
- targetId="vision-target" only when a clearly visible control has no reliable UIA match; then return a tight 0..1000 rectangle.
- done only when the goal itself is currently achieved and the screenshot visibly confirms it.
- Never invent controls, target ids, labels, state, domains, URLs, completed actions, or coordinates.
- Never request secrets. Never bypass browser security/privacy/certificate/phishing warnings. For known sites, reject ads/sponsored results and lookalike domains.
- Use short concrete Japanese instructions for a complete PC beginner.`;

export default {
  async fetch(request, env) {
    let url;
    try { url = new URL(request.url); }
    catch { return json({ error: 'bad_url' }, 400); }

    if (request.method !== 'POST' || !['/v1/guide', '/v1/quality-guide'].includes(url.pathname))
      return json({ error: 'not_found' }, 404);
    if (!authorized(request, env)) return json({ error: 'unauthorized' }, 401);
    if (!geminiConfigured(env)) return json({ error: 'gemini_unconfigured' }, 503);

    let body;
    try { body = await request.json(); }
    catch { return json({ error: 'invalid_json' }, 400); }

    const goal = text(body?.request ?? body?.Request, 1600);
    if (!goal) return json({ error: 'invalid_request' }, 400);

    const elements = (Array.isArray(body?.elements) ? body.elements : Array.isArray(body?.Elements) ? body.Elements : [])
      .slice(0, MAX_UI_ELEMENTS).map(compactElement).filter(Boolean);
    const history = (Array.isArray(body?.history) ? body.history : Array.isArray(body?.History) ? body.History : [])
      .slice(-MAX_HISTORY).map(compactHistory).filter(Boolean);
    const systemContext = compactSystemContext(body?.systemContext ?? body?.SystemContext);
    const task = buildWindowsTaskContext(goal, elements, history, systemContext);

    if (task?.kind === 'choice' && task?.deterministic)
      return json(normalizeDeterministic(task.deterministic));

    const commonPayload = {
      goal,
      completedSteps: history,
      systemContext,
      windowsKnowledge: task?.knowledge || '',
      canonicalConstraint: compactCanonical(task),
      uiElements: elements
    };

    if (url.pathname === '/v1/guide') {
      const raw = await invokeGeminiJson(env, structuredPrompt, commonPayload, decisionSchema, null, 'low');
      if (!raw.ok) return json({ error: raw.error }, 502);
      return json(validateStructuredDecision(raw.value, elements, task));
    }

    const image = text(body?.image ?? body?.Image, MAX_IMAGE_CHARS + 1);
    const parsedImage = parseDataImage(image);
    if (!parsedImage || image.length > MAX_IMAGE_CHARS) return json({ error: 'invalid_image' }, 400);

    const recoveryMode = (body?.recoveryMode ?? body?.RecoveryMode) === true;
    const routeIssue = text(body?.routeIssue ?? body?.RouteIssue, 240);
    const evidence = compactEvidence(body?.evidence ?? body?.Evidence, elements, history, systemContext);
    const payload = {
      ...commonPayload,
      recoveryMode,
      routeIssue: routeIssue || null,
      evidenceSummary: evidence,
      instruction: recoveryMode
        ? 'Infer the current state first. The goal is fixed but the route is flexible. Choose one grounded recovery action without requiring the screen to match the previous route.'
        : 'Infer the current state from the evidence and choose one grounded next action. A real actionable UI element is sufficient evidence even when the canonical path differs.'
    };

    const first = await invokeGeminiJson(env, qualityPrompt, payload, qualitySchema, parsedImage, 'medium');
    if (!first.ok) return json({ error: first.error }, 502);

    // Launch routes are deliberately flexible: a currently grounded bridge action must not be
    // discarded merely because it differs from the canonical route.
    const flexibleValidation = recoveryMode || task?.kind === 'launch-app';
    let validated = validateQualityDecision(first.value, elements, task, flexibleValidation);
    if (String(validated?.status || '').toLowerCase() !== 'not_found') return json(validated);

    // A semantic/model uncertainty is not fixed by taking the same screenshot four more times.
    // Reuse the SAME evidence once with deeper reasoning. This adds reasoning, not observation churn.
    const adjudicationPayload = {
      ...payload,
      adjudication: {
        previousDecision: validated,
        instruction: 'Reconcile the same evidence more deeply. Do not reject a real enabled/interactable current target merely for route mismatch. Return not_found only if no grounded action exists.'
      }
    };
    const second = await invokeGeminiJson(env, qualityPrompt, adjudicationPayload, qualitySchema, parsedImage, 'high');
    if (!second.ok) return json(validated);
    validated = validateQualityDecision(second.value, elements, task, flexibleValidation);
    return json(validated);
  }
};

function geminiConfigured(env) {
  return typeof env?.GEMINI_API_KEY === 'string' && env.GEMINI_API_KEY.trim().length > 0;
}

async function invokeGeminiJson(env, systemPrompt, payload, schema, image, thinkingLevel) {
  const model = text(env?.HELPSYS_GEMINI_MODEL, 80) || DEFAULT_GEMINI_MODEL;
  const endpoint = `https://generativelanguage.googleapis.com/v1beta/models/${encodeURIComponent(model)}:generateContent`;
  const parts = [{ text: JSON.stringify(payload) }];
  if (image) parts.push({ inlineData: { mimeType: image.mimeType, data: image.data } });

  const requestBody = {
    systemInstruction: { parts: [{ text: systemPrompt }] },
    contents: [{ role: 'user', parts }],
    generationConfig: {
      maxOutputTokens: 900,
      responseMimeType: 'application/json',
      responseSchema: schema,
      thinkingConfig: { thinkingLevel }
    }
  };

  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), thinkingLevel === 'high' ? 20_000 : 12_000);
  try {
    const response = await fetch(endpoint, {
      method: 'POST',
      headers: {
        'content-type': 'application/json; charset=utf-8',
        'x-goog-api-key': env.GEMINI_API_KEY
      },
      body: JSON.stringify(requestBody),
      signal: controller.signal
    });
    if (!response.ok) {
      console.error(`gemini_planner_http_${response.status}`);
      return { ok: false, error: 'gemini_provider_error' };
    }
    const result = await response.json();
    const value = extractGeminiJson(result);
    if (!value) return { ok: false, error: 'gemini_invalid_output' };
    return { ok: true, value };
  } catch (error) {
    console.error(error?.name === 'AbortError' ? 'gemini_planner_timeout' : 'gemini_planner_request_failed');
    return { ok: false, error: error?.name === 'AbortError' ? 'gemini_timeout' : 'gemini_provider_error' };
  } finally {
    clearTimeout(timeout);
  }
}

function extractGeminiJson(result) {
  const candidates = Array.isArray(result?.candidates) ? result.candidates : [];
  for (const candidate of candidates) {
    const parts = Array.isArray(candidate?.content?.parts) ? candidate.content.parts : [];
    const texts = parts
      .filter(part => part?.thought !== true && typeof part?.text === 'string')
      .map(part => part.text.trim())
      .filter(Boolean);
    for (let i = texts.length - 1; i >= 0; i--) {
      try { return JSON.parse(stripCodeFence(texts[i])); }
      catch { }
    }
  }
  return null;
}

function validateStructuredDecision(raw, elements, task) {
  const statuses = new Set(['target', 'clarify', 'done', 'not_found']);
  const actions = new Set(['left_click', 'double_click', 'type_text', 'press_key', 'none']);
  const status = statuses.has(String(raw?.status || '')) ? String(raw.status) : 'not_found';
  const action = actions.has(String(raw?.action || '')) ? String(raw.action) : 'none';
  const base = {
    status,
    targetId: nullableText(raw?.targetId, 80),
    action,
    instruction: text(raw?.instruction, 420),
    question: nullableText(raw?.question, 320),
    key: nullableText(raw?.key, 80),
    confidence: clamp(raw?.confidence)
  };

  const secretOverride = guardSecretClarification(base);
  if (secretOverride) return secretOverride;
  if (status === 'clarify') return base.question ? { ...base, targetId: null, action: 'none', key: null } : notFoundStructured('確認内容を特定できませんでした。');
  if (status === 'done') return { ...base, targetId: null, action: 'none', key: null, question: null };
  if (status !== 'target' || base.confidence < 0.70) return notFoundStructured(base.instruction || '現在の情報から次の操作を特定できませんでした。');

  if (action === 'press_key' && !base.targetId) {
    if (!base.key) return notFoundStructured('押すキーを特定できませんでした。');
    return base;
  }

  if (!base.targetId) return notFoundStructured('操作対象を特定できませんでした。');
  const target = elements.find(x => x.id === base.targetId);
  if (!target || !target.interactable || !target.enabled) return notFoundStructured('現在操作できる対象と一致しませんでした。');
  if (action === 'type_text' && (!target.focused || !target.keyboardFocusable || target.password))
    return notFoundStructured('現在選ばれている安全な入力欄ではありません。');

  let physical = base;
  if (/listitem/i.test(target.controlType || '') && /explorer/i.test(target.processName || '') && ['launch-app', 'site'].includes(task?.kind)) {
    const label = text(target.name, 80) || '青い枠の項目';
    physical = {
      ...physical,
      action: 'double_click',
      instruction: `青い枠の「${label}」で、マウスの左ボタンを間をあけずに2回押してください。`
    };
  }

  // Preserve only actual hard constraints here. Launch-app canonical routes are intentionally not hard constraints.
  if (task?.kind === 'site' || task?.kind === 'choice' || task?.kind === 'safety-block') {
    const guarded = guardDecisionForTask(task, physical);
    if (guarded?.status !== 'target') return notFoundStructured(guarded?.instruction || '現在の対象は安全条件と一致しませんでした。');
    physical = { ...physical, targetId: guarded.targetId, action: guarded.action, key: guarded.key };
  }

  return physical;
}

function normalizeDeterministic(decision) {
  return {
    status: String(decision?.status || 'not_found'),
    targetId: nullableText(decision?.targetId, 80),
    action: String(decision?.action || 'none'),
    instruction: text(decision?.instruction, 420),
    question: nullableText(decision?.question, 320),
    key: nullableText(decision?.key, 80),
    confidence: Number.isFinite(Number(decision?.confidence)) ? Number(decision.confidence) : 0.99
  };
}

function compactCanonical(task) {
  if (!task) return null;
  return {
    kind: task.kind || 'general',
    role: ['site', 'choice', 'safety-block'].includes(task.kind) ? 'hard_safety_constraint' : 'route_reference',
    deterministic: task.deterministic ? {
      status: task.deterministic.status,
      targetId: task.deterministic.targetId,
      action: task.deterministic.action,
      instruction: task.deterministic.instruction,
      question: task.deterministic.question,
      key: task.deterministic.key
    } : null,
    allowedTargetIds: task.allowedTargetIds instanceof Set ? [...task.allowedTargetIds] : null,
    officialDomains: Array.isArray(task?.site?.domains) ? task.site.domains : null
  };
}

function compactElement(value) {
  if (!value || typeof value !== 'object') return null;
  const id = text(value.id ?? value.Id, 48);
  if (!id) return null;
  const password = (value.password ?? value.Password) === true;
  return {
    id,
    name: text(value.name ?? value.Name, 360),
    automationId: text(value.automationId ?? value.AutomationId, 140),
    className: text(value.className ?? value.ClassName, 140),
    controlType: text(value.controlType ?? value.ControlType, 80),
    processName: text(value.processName ?? value.ProcessName, 80),
    processId: finite(value.processId ?? value.ProcessId),
    interactable: (value.interactable ?? value.Interactable) !== false,
    enabled: (value.enabled ?? value.Enabled) !== false,
    keyboardFocusable: (value.keyboardFocusable ?? value.KeyboardFocusable) === true,
    focused: (value.focused ?? value.Focused) === true,
    password,
    inputPresent: password ? false : (value.inputPresent ?? value.InputPresent) === true,
    toggleState: nullableText(value.toggleState ?? value.ToggleState, 60),
    selected: typeof (value.selected ?? value.Selected) === 'boolean' ? (value.selected ?? value.Selected) : null,
    expandCollapseState: nullableText(value.expandCollapseState ?? value.ExpandCollapseState, 60),
    x: finite(value.x ?? value.X), y: finite(value.y ?? value.Y),
    width: finite(value.width ?? value.Width), height: finite(value.height ?? value.Height)
  };
}

function compactHistory(value) {
  if (!value || typeof value !== 'object') return null;
  return {
    step: finite(value.step ?? value.Step),
    action: text(value.action ?? value.Action, 60),
    targetName: text(value.targetName ?? value.TargetName, 200),
    instruction: text(value.instruction ?? value.Instruction, 320)
  };
}

function compactSystemContext(value) {
  if (!value || typeof value !== 'object') return {
    foregroundProcess: '', foregroundTitle: '', foregroundProcessId: 0,
    foregroundWindowHandle: 0, taskbarVisible: false, runningApps: [], browser: null
  };
  const b = value.browser ?? value.Browser;
  const running = value.runningApps ?? value.RunningApps;
  return {
    foregroundProcess: text(value.foregroundProcess ?? value.ForegroundProcess, 80),
    foregroundTitle: text(value.foregroundTitle ?? value.ForegroundTitle, 300),
    foregroundProcessId: finite(value.foregroundProcessId ?? value.ForegroundProcessId),
    foregroundWindowHandle: finite(value.foregroundWindowHandle ?? value.ForegroundWindowHandle),
    taskbarVisible: (value.taskbarVisible ?? value.TaskbarVisible) === true,
    runningApps: Array.isArray(running) ? running.slice(0, 40).map(x => text(x, 80)).filter(Boolean) : [],
    browser: b && typeof b === 'object' ? {
      processName: text(b.processName ?? b.ProcessName, 80),
      windowTitle: text(b.windowTitle ?? b.WindowTitle, 280),
      domain: nullableText(b.domain ?? b.Domain, 220),
      https: typeof (b.https ?? b.Https) === 'boolean' ? (b.https ?? b.Https) : null,
      addressFieldFocused: (b.addressFieldFocused ?? b.AddressFieldFocused) === true
    } : null
  };
}

function compactEvidence(value, elements, history, systemContext) {
  const sourceValues = value?.evidenceSources ?? value?.EvidenceSources;
  return {
    sources: Array.isArray(sourceValues) ? sourceValues.slice(0, 8).map(x => text(x, 60)).filter(Boolean) : [],
    screenshotAvailable: (value?.screenshotAvailable ?? value?.ScreenshotAvailable) !== false,
    uiElementCount: finite(value?.uiElementCount ?? value?.UiElementCount ?? elements.length),
    interactableCount: finite(value?.interactableCount ?? value?.InteractableCount ?? elements.filter(x => x.interactable && x.enabled).length),
    focusedCount: finite(value?.focusedCount ?? value?.FocusedCount ?? elements.filter(x => x.focused).length),
    foregroundProcess: text(value?.foregroundProcess ?? value?.ForegroundProcess ?? systemContext.foregroundProcess, 80),
    foregroundTitle: text(value?.foregroundTitle ?? value?.ForegroundTitle ?? systemContext.foregroundTitle, 300),
    browserDomain: nullableText(value?.browserDomain ?? value?.BrowserDomain ?? systemContext.browser?.domain, 220),
    historyCount: finite(value?.historyCount ?? value?.HistoryCount ?? history.length)
  };
}

function parseDataImage(value) {
  if (typeof value !== 'string') return null;
  const match = /^data:(image\/(?:png|jpeg));base64,(.+)$/is.exec(value);
  if (!match) return null;
  return { mimeType: match[1].toLowerCase(), data: match[2] };
}

function guardSecretClarification(decision) {
  if (decision.status !== 'clarify') return null;
  const q = String(decision.question || decision.instruction || '');
  if (!/(password|passcode|パスワード|暗証|\bpin\b|otp|ワンタイム|認証コード|verification\s*code|recovery\s*key|リカバリ(?:ー)?キー|秘密鍵|secret\s*key|cvv|cvc|セキュリティコード)/i.test(q)) return null;
  return notFoundStructured('秘密情報そのものはHelpSysへ入力せず、実際のアプリ画面へ直接入力してください。');
}

function notFoundStructured(instruction) {
  return { status: 'not_found', targetId: null, action: 'none', instruction, question: null, key: null, confidence: 0 };
}

function authorized(request, env) {
  if (!env.HELPSYS_API_KEY) return true;
  return (request.headers.get('x-helpsys-key') || '') === env.HELPSYS_API_KEY;
}

function stripCodeFence(value) {
  const trimmed = String(value || '').trim();
  if (!trimmed.startsWith('```')) return trimmed;
  return trimmed.replace(/^```(?:json)?\s*/i, '').replace(/\s*```$/, '');
}

function clamp(value) { const n = Number(value); return Number.isFinite(n) ? Math.max(0, Math.min(1, n)) : 0; }
function finite(value) { const n = Number(value); return Number.isFinite(n) ? n : 0; }
function text(value, max) { return typeof value === 'string' ? value.trim().slice(0, max) : ''; }
function nullableText(value, max) { const v = text(value, max); return v || null; }
function json(value, status = 200) { return new Response(JSON.stringify(value), { status, headers: { 'content-type': 'application/json; charset=utf-8' } }); }
