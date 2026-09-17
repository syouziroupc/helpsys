import base, { guardSecretClarification, sanitizeScreenBody } from './reliability-v4-guard.js';
import { buildWindowsTaskContext, guardDecisionForTask, guardVisionDecisionForTask } from './windows-knowledge.js';

const SCREEN_ROUTES = new Set(['/v1/guide', '/v1/quality-guide', '/v1/vision-guide']);
const DEFAULT_GEMINI_MODEL = 'gemini-3.8-flash';
const GEMINI_TIMEOUT_MS = 4800;
const MAX_ELEMENTS = 420;
const MAX_HISTORY = 12;

const decisionSchema = {
  type: 'object',
  properties: {
    status: { type: 'string', enum: ['target', 'clarify', 'done', 'not_found'] },
    targetId: nullableStringSchema(),
    action: { type: 'string', enum: ['left_click', 'double_click', 'type_text', 'press_key', 'none'] },
    instruction: { type: 'string' },
    question: nullableStringSchema(),
    key: nullableStringSchema(),
    confidence: { type: 'number', minimum: 0, maximum: 1 }
  },
  required: ['status', 'targetId', 'action', 'instruction', 'question', 'key', 'confidence'],
  additionalProperties: false
};

const visionSchema = {
  type: 'object',
  properties: {
    status: { type: 'string', enum: ['target', 'clarify', 'done', 'not_found'] },
    label: nullableStringSchema(),
    instruction: { type: 'string' },
    question: nullableStringSchema(),
    x: { type: 'number', minimum: 0, maximum: 1000 },
    y: { type: 'number', minimum: 0, maximum: 1000 },
    width: { type: 'number', minimum: 0, maximum: 1000 },
    height: { type: 'number', minimum: 0, maximum: 1000 },
    confidence: { type: 'number', minimum: 0, maximum: 1 },
    observedDomain: nullableStringSchema(),
    sponsored: { type: 'boolean' }
  },
  required: ['status', 'label', 'instruction', 'question', 'x', 'y', 'width', 'height', 'confidence', 'observedDomain', 'sponsored'],
  additionalProperties: false
};

const qualitySchema = {
  type: 'object',
  properties: {
    ...decisionSchema.properties,
    x: { type: 'number', minimum: 0, maximum: 1000 },
    y: { type: 'number', minimum: 0, maximum: 1000 },
    width: { type: 'number', minimum: 0, maximum: 1000 },
    height: { type: 'number', minimum: 0, maximum: 1000 },
    screenConfirmed: { type: 'boolean' },
    visualEvidence: { type: 'string' },
    observedDomain: nullableStringSchema(),
    sponsored: { type: 'boolean' }
  },
  required: [
    ...decisionSchema.required,
    'x', 'y', 'width', 'height', 'screenConfirmed', 'visualEvidence', 'observedDomain', 'sponsored'
  ],
  additionalProperties: false
};

const systemPrompt = `You are the FINAL current-screen judge for HelpSys, a Windows guidance application.
The user, not the model, performs every operation. Return exactly ONE immediate next operation.

Your job is to resolve the CURRENT state from the evidence, not to police an imagined route.
- Windows UI Automation elements are strong evidence that a real current control exists and is actionable.
- A screenshot is strong evidence for visible layout, custom-rendered controls, warnings and visual-only targets.
- Foreground process/window and browser domain identify the current work surface.
- History is only context. Never let an old expected route override current evidence.
- Windows knowledge is a route reference for ordinary tasks. It is a hard constraint only for actual safety warnings, identity/account choices, secrets/authentication, destructive/financial decisions, and known-site identity.
- DO NOT return not_found merely because the current screen differs from a canonical path.
- DO NOT require two evidence sources when one source is intrinsically sufficient. A current enabled interactable UIA control can by itself ground a structured target.
- If a real current target plausibly advances the goal, select it even when it is a different valid route than expected.
- Use not_found only when no grounded next operation exists in the supplied current evidence.
- Never invent a control, target id, coordinate, app state, domain or completed action.
- Never ask the user to tell HelpSys a password, PIN, OTP, recovery key, API key, private key, CVV or other secret.
- Never guide through or around a browser certificate, phishing, malware or privacy warning.
- For a known website, do not choose sponsored results or a lookalike/non-official domain.
- If a choice changes account/identity, saved data, overwrite/delete, payment, permissions or defaults, clarify rather than choosing for the user.
- Keep Japanese instructions short and physical: where to point, press, or which key to press.

For structured targets, targetId must be an id from the current uiElements. For a visual-only quality target, use targetId="vision-target" with a tight normalized 0..1000 rectangle.`;

export default {
  async fetch(request, env, ctx) {
    let url;
    try { url = new URL(request.url); }
    catch { return base.fetch(request, env, ctx); }

    if (url.pathname === '/health' && request.method === 'GET') {
      const response = await base.fetch(request, env, ctx);
      if (response.status !== 200) return response;
      try {
        const value = await response.clone().json();
        return json({
          ...value,
          screenJudge: geminiConfigured(env) ? selectGeminiModel(env.HELPSYS_GEMINI_MODEL) : 'glm-fallback',
          geminiConfigured: geminiConfigured(env)
        }, 200, response.headers);
      } catch {
        return response;
      }
    }

    if (request.method !== 'POST' || !SCREEN_ROUTES.has(url.pathname) || !geminiConfigured(env))
      return base.fetch(request, env, ctx);

    if (!authorized(request, env)) return base.fetch(request, env, ctx);

    let raw;
    try { raw = await request.clone().json(); }
    catch { return base.fetch(request, env, ctx); }

    const body = sanitizeScreenBody(raw);
    const goal = text(body?.request ?? body?.Request, 1600);
    if (!goal) return base.fetch(request, env, ctx);

    const elements = compactElements(body?.elements ?? body?.Elements);
    const history = compactHistory(body?.history ?? body?.History);
    const systemContext = compactSystemContext(body?.systemContext ?? body?.SystemContext);
    const task = buildWindowsTaskContext(goal, elements, history, systemContext);

    try {
      const rawDecision = await runGeminiJudge(url.pathname, body, goal, elements, history, systemContext, task, env);
      const decision = validateGeminiDecision(url.pathname, rawDecision, body, elements, task);
      return json(decision);
    } catch {
      // Gemini transport/provider failure only: preserve the established GLM path as availability fallback.
      // A valid Gemini not_found is NOT replaced by GLM; Gemini remains authoritative when it answered.
      return base.fetch(request, env, ctx);
    }
  }
};

async function runGeminiJudge(route, body, goal, elements, history, systemContext, task, env) {
  const image = typeof body?.image === 'string' ? body.image : typeof body?.Image === 'string' ? body.Image : '';
  const schema = route === '/v1/quality-guide' ? qualitySchema : route === '/v1/vision-guide' ? visionSchema : decisionSchema;

  const payload = {
    route,
    goal,
    recoveryMode: body?.recoveryMode === true || body?.RecoveryMode === true,
    routeIssue: text(body?.routeIssue ?? body?.RouteIssue, 240) || null,
    completedSteps: history,
    systemContext,
    windowsKnowledge: text(task?.knowledge, 5000),
    taskKind: text(task?.kind, 80) || 'general',
    uiElements: elements,
    evidenceSummary: compactEvidence(body?.evidence ?? body?.Evidence),
    instruction: 'Judge the current state directly. Prefer a grounded actionable next step over not_found. Ordinary route deviation is not a safety failure.'
  };

  const parts = [{ text: JSON.stringify(payload) }];
  const imagePart = parseImageDataUri(image);
  if (imagePart) parts.push({ inlineData: imagePart });

  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), GEMINI_TIMEOUT_MS);
  try {
    const model = selectGeminiModel(env.HELPSYS_GEMINI_MODEL);
    const response = await fetch(`https://generativelanguage.googleapis.com/v1beta/models/${encodeURIComponent(model)}:generateContent`, {
      method: 'POST',
      headers: {
        'content-type': 'application/json',
        'x-goog-api-key': String(env.GEMINI_API_KEY),
        'x-goog-api-client': 'syouziroupc-helpsys/1.0'
      },
      body: JSON.stringify({
        systemInstruction: { parts: [{ text: systemPrompt }] },
        contents: [{ role: 'user', parts }],
        generationConfig: {
          thinkingConfig: { thinkingLevel: 'low' },
          maxOutputTokens: 900,
          responseMimeType: 'application/json',
          responseJsonSchema: schema
        }
      }),
      signal: controller.signal
    });

    if (!response.ok) throw new Error('gemini_provider_error');
    const data = await response.json();
    const content = data?.candidates?.[0]?.content?.parts;
    const output = Array.isArray(content)
      ? content.map(part => typeof part?.text === 'string' ? part.text : '').join('').trim()
      : '';
    if (!output) throw new Error('gemini_empty_output');
    return JSON.parse(stripCodeFence(output));
  } finally {
    clearTimeout(timer);
  }
}

function validateGeminiDecision(route, raw, body, elements, task) {
  if (route === '/v1/quality-guide') return validateQuality(raw, elements, task);
  if (route === '/v1/vision-guide') return validateVision(raw, task);
  return validateStructured(raw, elements, task);
}

function validateStructured(raw, elements, task) {
  const baseDecision = normalizeDecision(raw);
  const secretOverride = guardSecretClarification(baseDecision);
  if (secretOverride) return secretOverride;

  if (baseDecision.status === 'clarify')
    return baseDecision.question ? { ...baseDecision, targetId: null, action: 'none', key: null } : notFoundStructured('確認内容を特定できませんでした。');

  if (baseDecision.status === 'done') {
    if (baseDecision.confidence < 0.82) return notFoundStructured('完了状態の確度が足りませんでした。');
    return applyStructuredHardGuards(task, { ...baseDecision, targetId: null, action: 'none', key: null });
  }

  if (baseDecision.status !== 'target' || baseDecision.confidence < 0.74)
    return notFoundStructured(baseDecision.instruction || '現在の画面から次の操作を確定できませんでした。');

  if (baseDecision.action === 'press_key' && !baseDecision.targetId) {
    if (!baseDecision.key) return notFoundStructured('押すキーを特定できませんでした。');
    return applyStructuredHardGuards(task, baseDecision);
  }

  const target = elements.find(x => x.id === baseDecision.targetId);
  if (!target || !target.interactable || !target.enabled)
    return notFoundStructured('現在操作できる対象と一致しませんでした。');
  if (baseDecision.action === 'type_text' && (!target.focused || !target.keyboardFocusable || target.password))
    return notFoundStructured('現在入力できる欄として確認できませんでした。');

  return applyStructuredHardGuards(task, baseDecision);
}

function validateQuality(raw, elements, task) {
  const decision = normalizeQuality(raw);
  const secretOverride = guardSecretClarification(decision);
  if (secretOverride) return qualityFromStructured(secretOverride);

  if (task?.kind === 'choice' && task?.deterministic?.status === 'clarify') {
    return {
      ...qualityNotFound(''),
      status: 'clarify',
      question: task.deterministic.question,
      instruction: '',
      confidence: Math.max(0.95, decision.confidence)
    };
  }

  if (decision.status === 'clarify') {
    if (!decision.question) return qualityNotFound('確認内容を特定できませんでした。');
    return { ...decision, targetId: null, action: 'none', key: null };
  }

  if (decision.status === 'done') {
    if (!decision.screenConfirmed || decision.confidence < 0.86 || decision.visualEvidence.length < 2)
      return qualityNotFound('現在の画面で完了状態を確認できませんでした。');
    return applyQualityHardGuards(task, { ...decision, targetId: null, action: 'none', key: null });
  }

  if (decision.status !== 'target' || decision.confidence < 0.76)
    return qualityNotFound(decision.instruction || '現在の画面から次の操作を確定できませんでした。');

  if (decision.action === 'press_key' && !decision.targetId) {
    if (!decision.key) return qualityNotFound('押すキーを特定できませんでした。');
    const canonicalKey = task?.deterministic?.status === 'target' && task?.deterministic?.action === 'press_key'
      ? normalizeKey(task.deterministic.key)
      : '';
    if (!decision.screenConfirmed && (!canonicalKey || canonicalKey !== normalizeKey(decision.key)))
      return qualityNotFound('現在状態から裏付けられるキー操作を特定できませんでした。');
    return applyQualityHardGuards(task, { ...decision, targetId: null });
  }

  if (decision.targetId === 'vision-target') {
    if (!decision.screenConfirmed || decision.confidence < 0.86 || decision.width < 4 || decision.height < 4)
      return qualityNotFound('画像上の操作位置を十分に確認できませんでした。');
    return applyQualityHardGuards(task, decision);
  }

  const target = elements.find(x => x.id === decision.targetId);
  if (!target || !target.interactable || !target.enabled)
    return qualityNotFound('現在操作できるUI対象と一致しませんでした。');
  if (decision.action === 'type_text' && (!target.focused || !target.keyboardFocusable || target.password))
    return qualityNotFound('現在入力できる欄として確認できませんでした。');

  // A real current UIA target is sufficient grounding even when screenshot text is ambiguous.
  // The Windows client revalidates this exact target again immediately before showing guidance.
  return applyQualityHardGuards(task, decision);
}

function validateVision(raw, task) {
  const status = validStatus(raw?.status);
  const decision = {
    status,
    label: nullableText(raw?.label, 180),
    instruction: text(raw?.instruction, 420),
    question: nullableText(raw?.question, 300),
    x: clamp1000(raw?.x),
    y: clamp1000(raw?.y),
    width: clamp1000(raw?.width),
    height: clamp1000(raw?.height),
    confidence: clamp(raw?.confidence),
    observedDomain: nullableText(raw?.observedDomain, 220),
    sponsored: raw?.sponsored === true
  };

  if (status === 'clarify') return decision.question ? decision : visionNotFound('確認内容を特定できませんでした。');
  if (status === 'done') return decision.confidence >= 0.86 ? decision : visionNotFound('完了状態の確度が足りませんでした。');
  if (status !== 'target' || decision.confidence < 0.84 || decision.width < 4 || decision.height < 4)
    return visionNotFound(decision.instruction || '画像上の次の操作位置を特定できませんでした。');

  const guarded = guardVisionDecisionForTask(task, decision);
  return guarded || decision;
}

function applyStructuredHardGuards(task, decision) {
  if (!task) return decision;
  if (task.kind === 'safety-block' || task.kind === 'choice' || task.kind === 'site') {
    const guarded = guardDecisionForTask(task, decision);
    return guarded || decision;
  }
  // launch-app and general tasks are deliberately NOT canonical-route locked.
  return decision;
}

function applyQualityHardGuards(task, decision) {
  if (!task) return decision;
  if (decision.targetId === 'vision-target' && task.kind === 'site') {
    const guarded = guardVisionDecisionForTask(task, {
      status: 'target', label: null, instruction: decision.instruction, question: null,
      x: decision.x, y: decision.y, width: decision.width, height: decision.height,
      confidence: decision.confidence, observedDomain: decision.observedDomain, sponsored: decision.sponsored
    });
    if (guarded?.status !== 'target') return qualityNotFound(guarded?.instruction || '公式サイトとして確認できない候補は案内しません。');
  }

  if (task.kind === 'safety-block' || task.kind === 'choice' || (task.kind === 'site' && decision.targetId !== 'vision-target')) {
    const guarded = guardDecisionForTask(task, {
      status: decision.status,
      targetId: decision.targetId,
      action: decision.action,
      instruction: decision.instruction,
      question: decision.question,
      key: decision.key,
      confidence: decision.confidence
    });
    if (guarded?.status !== decision.status && guarded?.status !== 'target')
      return qualityNotFound(guarded?.instruction || '現在の安全条件と一致しませんでした。');
    if (guarded?.status === 'target')
      return { ...decision, targetId: guarded.targetId, action: guarded.action, key: guarded.key, instruction: guarded.instruction || decision.instruction };
  }

  return decision;
}

function normalizeDecision(raw) {
  return {
    status: validStatus(raw?.status),
    targetId: nullableText(raw?.targetId, 80),
    action: validAction(raw?.action),
    instruction: text(raw?.instruction, 420),
    question: nullableText(raw?.question, 300),
    key: nullableText(raw?.key, 80),
    confidence: clamp(raw?.confidence)
  };
}

function normalizeQuality(raw) {
  return {
    ...normalizeDecision(raw),
    x: clamp1000(raw?.x), y: clamp1000(raw?.y), width: clamp1000(raw?.width), height: clamp1000(raw?.height),
    screenConfirmed: raw?.screenConfirmed === true,
    visualEvidence: text(raw?.visualEvidence, 300),
    observedDomain: nullableText(raw?.observedDomain, 220),
    sponsored: raw?.sponsored === true
  };
}

function compactElements(raw) {
  return (Array.isArray(raw) ? raw : []).slice(0, MAX_ELEMENTS).map(value => {
    if (!value || typeof value !== 'object') return null;
    const id = text(value.id ?? value.Id, 60);
    if (!id) return null;
    return {
      id,
      name: text(value.name ?? value.Name, 360),
      automationId: text(value.automationId ?? value.AutomationId, 160),
      className: text(value.className ?? value.ClassName, 160),
      controlType: text(value.controlType ?? value.ControlType, 90),
      processName: text(value.processName ?? value.ProcessName, 90),
      interactable: (value.interactable ?? value.Interactable) !== false,
      enabled: (value.enabled ?? value.Enabled) !== false,
      keyboardFocusable: (value.keyboardFocusable ?? value.KeyboardFocusable) === true,
      focused: (value.focused ?? value.Focused) === true,
      password: (value.password ?? value.Password) === true,
      inputPresent: (value.inputPresent ?? value.InputPresent) === true,
      toggleState: nullableText(value.toggleState ?? value.ToggleState, 60),
      selected: typeof (value.selected ?? value.Selected) === 'boolean' ? (value.selected ?? value.Selected) : null,
      expandCollapseState: nullableText(value.expandCollapseState ?? value.ExpandCollapseState, 60),
      x: finite(value.x ?? value.X), y: finite(value.y ?? value.Y),
      width: finite(value.width ?? value.Width), height: finite(value.height ?? value.Height)
    };
  }).filter(Boolean);
}

function compactHistory(raw) {
  return (Array.isArray(raw) ? raw : []).slice(-MAX_HISTORY).map(value => ({
    step: finite(value?.step ?? value?.Step),
    action: text(value?.action ?? value?.Action, 60),
    targetName: text(value?.targetName ?? value?.TargetName, 180),
    instruction: text(value?.instruction ?? value?.Instruction, 320)
  }));
}

function compactSystemContext(value) {
  const source = value && typeof value === 'object' ? value : {};
  const b = source.browser ?? source.Browser;
  const browser = b && typeof b === 'object' ? {
    processName: text(b.processName ?? b.ProcessName, 80),
    windowTitle: text(b.windowTitle ?? b.WindowTitle, 260),
    domain: nullableText(b.domain ?? b.Domain, 220),
    https: typeof (b.https ?? b.Https) === 'boolean' ? (b.https ?? b.Https) : null,
    addressFieldFocused: (b.addressFieldFocused ?? b.AddressFieldFocused) === true
  } : null;
  const running = source.runningApps ?? source.RunningApps;
  return {
    foregroundProcess: text(source.foregroundProcess ?? source.ForegroundProcess, 90),
    foregroundTitle: text(source.foregroundTitle ?? source.ForegroundTitle, 300),
    foregroundProcessId: finite(source.foregroundProcessId ?? source.ForegroundProcessId),
    taskbarVisible: (source.taskbarVisible ?? source.TaskbarVisible) === true,
    runningApps: Array.isArray(running) ? running.slice(0, 40).map(x => text(x, 90)).filter(Boolean) : [],
    browser
  };
}

function compactEvidence(value) {
  if (!value || typeof value !== 'object') return null;
  const copy = { ...value };
  delete copy.browserUrl;
  delete copy.BrowserUrl;
  return copy;
}

function parseImageDataUri(value) {
  if (typeof value !== 'string') return null;
  const match = /^data:(image\/(?:png|jpeg|jpg|webp));base64,([A-Za-z0-9+/=]+)$/i.exec(value);
  if (!match) return null;
  return { mimeType: match[1].toLowerCase() === 'image/jpg' ? 'image/jpeg' : match[1].toLowerCase(), data: match[2] };
}

function selectGeminiModel(value) {
  return value === DEFAULT_GEMINI_MODEL ? value : DEFAULT_GEMINI_MODEL;
}

function geminiConfigured(env) {
  return typeof env?.GEMINI_API_KEY === 'string' && env.GEMINI_API_KEY.trim().length >= 16;
}

function authorized(request, env) {
  if (!env.HELPSYS_API_KEY) return true;
  return (request.headers.get('x-helpsys-key') || '') === env.HELPSYS_API_KEY;
}

function nullableStringSchema() {
  return { anyOf: [{ type: 'string' }, { type: 'null' }] };
}

function validStatus(value) {
  const status = String(value || 'not_found');
  return ['target', 'clarify', 'done', 'not_found'].includes(status) ? status : 'not_found';
}

function validAction(value) {
  const action = String(value || 'none');
  return ['left_click', 'double_click', 'type_text', 'press_key', 'none'].includes(action) ? action : 'none';
}

function qualityFromStructured(value) {
  return {
    ...qualityNotFound(value?.instruction || ''),
    status: value?.status || 'not_found',
    targetId: value?.targetId ?? null,
    action: value?.action || 'none',
    question: value?.question ?? null,
    key: value?.key ?? null,
    confidence: clamp(value?.confidence)
  };
}

function qualityNotFound(instruction) {
  return {
    status: 'not_found', targetId: null, action: 'none', instruction: instruction || '現在の画面から次の操作を確定できませんでした。',
    question: null, key: null, confidence: 0,
    x: 0, y: 0, width: 0, height: 0,
    screenConfirmed: false, visualEvidence: '', observedDomain: null, sponsored: false
  };
}

function notFoundStructured(instruction) {
  return { status: 'not_found', targetId: null, action: 'none', instruction: instruction || '現在の画面から次の操作を確定できませんでした。', question: null, key: null, confidence: 0 };
}

function visionNotFound(instruction) {
  return { status: 'not_found', label: null, instruction: instruction || '画像上の次の操作位置を特定できませんでした。', question: null, x: 0, y: 0, width: 0, height: 0, confidence: 0, observedDomain: null, sponsored: false };
}

function json(value, status = 200, sourceHeaders = null) {
  const headers = new Headers(sourceHeaders || undefined);
  headers.set('content-type', 'application/json; charset=utf-8');
  headers.set('cache-control', 'no-store');
  return new Response(JSON.stringify(value), { status, headers });
}

function stripCodeFence(value) {
  const trimmed = String(value || '').trim();
  if (!trimmed.startsWith('```')) return trimmed;
  return trimmed.replace(/^```(?:json)?\s*/i, '').replace(/\s*```$/, '');
}

function normalizeKey(value) {
  return String(value || '').replace(/\s+/g, '').toLowerCase();
}

function nullableText(value, max) {
  const result = text(value, max);
  return result || null;
}

function text(value, max = 500) {
  if (typeof value !== 'string') return '';
  const normalized = value.replace(/[\r\n]+/g, ' ').trim();
  return normalized.length <= max ? normalized : normalized.slice(0, max);
}

function finite(value) {
  const number = Number(value);
  return Number.isFinite(number) ? number : 0;
}

function clamp(value) {
  return Math.max(0, Math.min(1, finite(value)));
}

function clamp1000(value) {
  return Math.max(0, Math.min(1000, finite(value)));
}
