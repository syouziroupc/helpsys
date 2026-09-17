import { buildWindowsTaskContext, guardVisionDecisionForTask, visionHintForTask } from './windows-knowledge.js';

const DEFAULT_GEMINI_MODEL = 'gemini-3.8-flash';
const MAX_HISTORY = 12;
const MAX_IMAGE_CHARS = 6_500_000;

const visionSchema = {
  type: 'object',
  properties: {
    status: { type: 'string', enum: ['target', 'clarify', 'done', 'not_found'] },
    label: { type: 'string' },
    instruction: { type: 'string' },
    question: { type: 'string' },
    x: { type: 'number', minimum: 0, maximum: 1000 },
    y: { type: 'number', minimum: 0, maximum: 1000 },
    width: { type: 'number', minimum: 0, maximum: 1000 },
    height: { type: 'number', minimum: 0, maximum: 1000 },
    confidence: { type: 'number', minimum: 0, maximum: 1 },
    observedDomain: { type: 'string' },
    sponsored: { type: 'boolean' }
  },
  required: ['status', 'label', 'instruction', 'question', 'x', 'y', 'width', 'height', 'confidence', 'observedDomain', 'sponsored'],
  additionalProperties: false
};

const prompt = `You are the vision planner for HelpSys. The human performs every action.
Return exactly one immediate next Windows operation as JSON matching the supplied schema.

Use the screenshot as current visual evidence and systemContext as foreground/domain evidence.
The goal is fixed but an imagined route is not. Choose a visible reversible bridge action when it advances the goal; do not return not_found merely because the current screen differs from a standard path.
Use target only when the control is actually visible. Return a tight normalized 0..1000 rectangle.
not_found is exceptional and means no grounded visible action exists.
clarify is only for a genuine user decision such as identity/account, overwrite/delete, purchase/payment, permissions/defaults, or materially different choices. Never ask the user to diagnose model uncertainty.
Never invent controls, labels, state, coordinates, domains, or completed actions.
Never request secrets. Never bypass browser security/privacy/certificate/phishing warnings. For a known site, do not select ads/sponsored results or a lookalike domain.
Use short concrete Japanese instructions for a complete PC beginner.`;

export default {
  async fetch(request, env) {
    let url;
    try { url = new URL(request.url); }
    catch { return json({ error: 'bad_url' }, 400); }
    if (request.method !== 'POST' || url.pathname !== '/v1/vision-guide') return json({ error: 'not_found' }, 404);
    if (!authorized(request, env)) return json({ error: 'unauthorized' }, 401);
    if (!configured(env)) return json({ error: 'gemini_unconfigured' }, 503);

    let body;
    try { body = await request.json(); }
    catch { return json({ error: 'invalid_json' }, 400); }

    const goal = text(body?.request ?? body?.Request, 1600);
    const image = text(body?.image ?? body?.Image, MAX_IMAGE_CHARS + 1);
    const parsedImage = parseDataImage(image);
    if (!goal) return json({ error: 'invalid_request' }, 400);
    if (!parsedImage || image.length > MAX_IMAGE_CHARS) return json({ error: 'invalid_image' }, 400);

    const history = (Array.isArray(body?.history) ? body.history : Array.isArray(body?.History) ? body.History : [])
      .slice(-MAX_HISTORY).map(compactHistory).filter(Boolean);
    const systemContext = compactSystemContext(body?.systemContext ?? body?.SystemContext);
    const task = buildWindowsTaskContext(goal, [], history, systemContext);
    const payload = {
      goal,
      completedSteps: history,
      systemContext,
      windowsKnowledge: visionHintForTask(task),
      instruction: 'Infer the current visible state and choose one grounded visible action. Route mismatch alone is not a reason to stop.'
    };

    const first = await invoke(env, payload, parsedImage, 'medium');
    if (!first.ok) return json({ error: first.error }, 502);
    let value = validate(first.value, task);
    if (value.status !== 'not_found') return json(stripPrivate(value));

    // Same screenshot, deeper reasoning: no repeated capture loop.
    const second = await invoke(env, {
      ...payload,
      adjudication: {
        previousDecision: value,
        instruction: 'Re-evaluate the SAME screenshot carefully. Return not_found only when no visible grounded action exists.'
      }
    }, parsedImage, 'high');
    if (second.ok) value = validate(second.value, task);
    return json(stripPrivate(value));
  }
};

async function invoke(env, payload, image, thinkingLevel) {
  const model = text(env?.HELPSYS_GEMINI_MODEL, 80) || DEFAULT_GEMINI_MODEL;
  const endpoint = `https://generativelanguage.googleapis.com/v1beta/models/${encodeURIComponent(model)}:generateContent`;
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), thinkingLevel === 'high' ? 20_000 : 12_000);
  try {
    const response = await fetch(endpoint, {
      method: 'POST',
      headers: { 'content-type': 'application/json; charset=utf-8', 'x-goog-api-key': env.GEMINI_API_KEY },
      body: JSON.stringify({
        systemInstruction: { parts: [{ text: prompt }] },
        contents: [{ role: 'user', parts: [{ text: JSON.stringify(payload) }, { inlineData: { mimeType: image.mimeType, data: image.data } }] }],
        generationConfig: {
          maxOutputTokens: 700,
          responseMimeType: 'application/json',
          responseSchema: visionSchema,
          thinkingConfig: { thinkingLevel }
        }
      }),
      signal: controller.signal
    });
    if (!response.ok) {
      console.error(`gemini_vision_http_${response.status}`);
      return { ok: false, error: 'gemini_provider_error' };
    }
    const result = await response.json();
    const parsed = extractJson(result);
    return parsed ? { ok: true, value: parsed } : { ok: false, error: 'gemini_invalid_output' };
  } catch (error) {
    console.error(error?.name === 'AbortError' ? 'gemini_vision_timeout' : 'gemini_vision_request_failed');
    return { ok: false, error: error?.name === 'AbortError' ? 'gemini_timeout' : 'gemini_provider_error' };
  } finally {
    clearTimeout(timeout);
  }
}

function validate(raw, task) {
  const statuses = new Set(['target', 'clarify', 'done', 'not_found']);
  const status = statuses.has(String(raw?.status || '')) ? String(raw.status) : 'not_found';
  const value = {
    status,
    label: nullableText(raw?.label, 160),
    instruction: text(raw?.instruction, 420),
    question: nullableText(raw?.question, 320),
    x: clamp1000(raw?.x), y: clamp1000(raw?.y), width: clamp1000(raw?.width), height: clamp1000(raw?.height),
    confidence: clamp(raw?.confidence),
    observedDomain: nullableText(raw?.observedDomain, 220),
    sponsored: raw?.sponsored === true
  };

  if (status === 'clarify') return value.question ? value : notFound('確認内容を特定できませんでした。');
  if (status === 'done') return value.confidence >= 0.90 ? value : notFound('完了状態を十分に確認できませんでした。');
  if (status !== 'target' || value.confidence < 0.80) return notFound(value.instruction || '画面上の次の操作を特定できませんでした。');
  if (value.width < 4 || value.height < 4) return notFound('画面上の操作位置を十分に特定できませんでした。');

  const guarded = guardVisionDecisionForTask(task, value);
  return guarded?.status === 'target' ? { ...value, ...guarded } : notFound(guarded?.instruction || '現在の候補は安全条件と一致しませんでした。');
}

function stripPrivate(value) {
  return {
    status: value.status,
    label: value.label ?? null,
    instruction: value.instruction || '',
    question: value.question ?? null,
    x: value.x || 0,
    y: value.y || 0,
    width: value.width || 0,
    height: value.height || 0,
    confidence: value.confidence || 0
  };
}

function notFound(instruction) {
  return { status: 'not_found', label: null, instruction, question: null, x: 0, y: 0, width: 0, height: 0, confidence: 0, observedDomain: null, sponsored: false };
}

function extractJson(result) {
  const candidates = Array.isArray(result?.candidates) ? result.candidates : [];
  for (const candidate of candidates) {
    const parts = Array.isArray(candidate?.content?.parts) ? candidate.content.parts : [];
    for (const part of parts) {
      if (part?.thought === true || typeof part?.text !== 'string') continue;
      try { return JSON.parse(stripCodeFence(part.text)); } catch { }
    }
  }
  return null;
}

function compactHistory(value) {
  if (!value || typeof value !== 'object') return null;
  return { step: finite(value.step ?? value.Step), action: text(value.action ?? value.Action, 60), targetName: text(value.targetName ?? value.TargetName, 200), instruction: text(value.instruction ?? value.Instruction, 320) };
}

function compactSystemContext(value) {
  if (!value || typeof value !== 'object') return { foregroundProcess: '', foregroundTitle: '', foregroundProcessId: 0, taskbarVisible: false, runningApps: [], browser: null };
  const b = value.browser ?? value.Browser;
  const running = value.runningApps ?? value.RunningApps;
  return {
    foregroundProcess: text(value.foregroundProcess ?? value.ForegroundProcess, 80),
    foregroundTitle: text(value.foregroundTitle ?? value.ForegroundTitle, 300),
    foregroundProcessId: finite(value.foregroundProcessId ?? value.ForegroundProcessId),
    taskbarVisible: (value.taskbarVisible ?? value.TaskbarVisible) === true,
    runningApps: Array.isArray(running) ? running.slice(0, 40).map(x => text(x, 80)).filter(Boolean) : [],
    browser: b && typeof b === 'object' ? {
      processName: text(b.processName ?? b.ProcessName, 80),
      windowTitle: text(b.windowTitle ?? b.WindowTitle, 280),
      domain: nullableText(b.domain ?? b.Domain, 220),
      https: typeof (b.https ?? b.Https) === 'boolean' ? (b.https ?? b.Https) : null
    } : null
  };
}

function parseDataImage(value) {
  if (typeof value !== 'string') return null;
  const match = /^data:(image\/(?:png|jpeg));base64,(.+)$/is.exec(value);
  return match ? { mimeType: match[1].toLowerCase(), data: match[2] } : null;
}

function configured(env) { return typeof env?.GEMINI_API_KEY === 'string' && env.GEMINI_API_KEY.trim().length > 0; }
function authorized(request, env) { return !env.HELPSYS_API_KEY || (request.headers.get('x-helpsys-key') || '') === env.HELPSYS_API_KEY; }
function stripCodeFence(value) { const t = String(value || '').trim(); return t.startsWith('```') ? t.replace(/^```(?:json)?\s*/i, '').replace(/\s*```$/, '') : t; }
function clamp(value) { const n = Number(value); return Number.isFinite(n) ? Math.max(0, Math.min(1, n)) : 0; }
function clamp1000(value) { const n = Number(value); return Number.isFinite(n) ? Math.max(0, Math.min(1000, n)) : 0; }
function finite(value) { const n = Number(value); return Number.isFinite(n) ? n : 0; }
function text(value, max) { return typeof value === 'string' ? value.trim().slice(0, max) : ''; }
function nullableText(value, max) { const v = text(value, max); return v || null; }
function json(value, status = 200) { return new Response(JSON.stringify(value), { status, headers: { 'content-type': 'application/json; charset=utf-8' } }); }
