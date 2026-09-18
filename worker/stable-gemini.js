const MODEL = 'gemini-3.8-flash';
const VERSION = '3.0.1';
const MAX_BODY_BYTES = 7_000_000;
const MAX_CONTROLS = 240;

const responseSchema = {
  type: 'object',
  properties: {
    status: { type: 'string', enum: ['target', 'clarify', 'done'] },
    action: { type: 'string', enum: ['left_click', 'double_click', 'type_text', 'press_key', 'none'] },
    instruction: { type: 'string' },
    question: { type: ['string', 'null'] },
    targetId: { type: ['string', 'null'] },
    key: { type: ['string', 'null'] },
    confidence: { type: 'number', minimum: 0, maximum: 1 },
    x: { type: 'number', minimum: 0, maximum: 1000 },
    y: { type: 'number', minimum: 0, maximum: 1000 },
    width: { type: 'number', minimum: 0, maximum: 1000 },
    height: { type: 'number', minimum: 0, maximum: 1000 }
  },
  required: ['status', 'action', 'instruction', 'question', 'targetId', 'key', 'confidence', 'x', 'y', 'width', 'height'],
  additionalProperties: false
};

const systemPrompt = `You are the only planning model for HelpSys Stable 3.0, a Windows guidance application for complete beginners.
The human performs every action. HelpSys never clicks, types, submits, installs, purchases, deletes, or changes settings itself.
Return exactly one immediate next step in the required JSON schema.

CURRENT STATE:
- The screenshot is the current visible foreground window.
- controls are current Windows UI Automation elements with stable fields for this observation only.
- browserDomain, when present, is locally extracted from the address field with path/query removed.
- Choose one real current action that advances the user's stated goal. Do not require a canonical or imagined route.
- Prefer an existing enabled control targetId when one is suitable.
- If no reliable UIA control exists but the screenshot clearly shows the target, targetId may be null and you must return a tight visual rectangle.
- A desktop Explorer ListItem used to launch an app or shortcut normally requires double_click.
- press_key may use targetId=null only when the current screen clearly supports that key.
- type_text is guidance only; the human types.

DO NOT BECOME PASSIVE:
- Do not refuse merely because the screen differs from a standard route.
- Do not ask the user to diagnose HelpSys, describe the screen, or name a visible button because recognition is difficult.
- clarify is only for a genuine USER decision: account/identity, overwrite/delete, purchase/payment, permission/default selection, or materially different outcomes.
- Otherwise choose the best grounded next operation from current evidence.

SAFETY:
- Never ask for passwords, PINs, OTPs, verification codes, recovery keys, private keys, API keys, CVV/CVC, card authentication data, or other secrets.
- Never tell the user to bypass browser certificate, phishing, malware, SmartScreen, or privacy warnings.
- For known websites, do not select sponsored results or lookalike domains. Use browserDomain when available.
- Never claim an action already happened unless current evidence supports it.
- done is allowed only when the requested goal is visibly achieved now.
- Final destructive, purchase/payment, permission, identity/account, overwrite, or default-selection choices should be clarify unless the user's current instruction already unambiguously specifies that exact choice.

OUTPUT QUALITY:
- One action only.
- Short concrete Japanese suitable for a PC beginner.
- confidence is confidence in this exact next action on the current screen.
- If targetId is present, it must exactly match one current control id.
- If targetId is null for a mouse action, rectangle width and height must both be greater than zero.`;

export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    if (request.method === 'GET' && url.pathname === '/health') {
      return json({
        ok: true,
        service: 'helpsys',
        version: VERSION,
        planner: MODEL,
        plannerProvider: 'gemini',
        geminiConfigured: hasKey(env)
      });
    }
    if (request.method !== 'POST' || url.pathname !== '/v1/plan')
      return json({ error: 'not_found' }, 404);
    if (!hasKey(env)) return json({ error: 'gemini_unconfigured' }, 503);

    const declaredLength = Number(request.headers.get('content-length') || 0);
    if (declaredLength > MAX_BODY_BYTES) return json({ error: 'payload_too_large' }, 413);

    let body;
    try {
      const raw = await request.arrayBuffer();
      if (raw.byteLength > MAX_BODY_BYTES) return json({ error: 'payload_too_large' }, 413);
      body = JSON.parse(new TextDecoder().decode(raw));
    }
    catch { return json({ error: 'invalid_json' }, 400); }

    const goal = sanitizeOutboundText(text(body?.goal, 1000));
    const image = typeof body?.image === 'string' ? body.image : '';
    if (!goal) return json({ error: 'invalid_goal' }, 400);
    const parsedImage = parseDataImage(image);
    if (!parsedImage || image.length > MAX_BODY_BYTES) return json({ error: 'invalid_image' }, 400);

    const controls = Array.isArray(body?.controls)
      ? body.controls.slice(0, MAX_CONTROLS).map(compactControl).filter(Boolean)
      : [];

    const context = {
      goal,
      processName: text(body?.processName, 80),
      windowTitle: sanitizeOutboundText(text(body?.windowTitle, 260)),
      browserDomain: nullableText(body?.browserDomain, 220),
      controls
    };

    const result = await callGemini(env.GEMINI_API_KEY, context, parsedImage);
    if (!result.ok) return json({ error: result.error }, result.status);

    const validated = validatePlan(result.value, controls);
    if (!validated.ok) return json({ error: validated.error }, 502);
    return json(validated.value);
  }
};

async function callGemini(apiKey, context, image) {
  const endpoint = `https://generativelanguage.googleapis.com/v1beta/models/${MODEL}:generateContent`;
  const requestBody = {
    systemInstruction: { parts: [{ text: systemPrompt }] },
    contents: [{
      role: 'user',
      parts: [
        { text: JSON.stringify(context) },
        { inlineData: { mimeType: image.mimeType, data: image.data } }
      ]
    }],
    generationConfig: {
      temperature: 0.05,
      maxOutputTokens: 900,
      responseMimeType: 'application/json',
      responseJsonSchema: responseSchema,
      thinkingConfig: { thinkingLevel: 'medium' }
    }
  };

  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), 24_000);
  try {
    const response = await fetch(endpoint, {
      method: 'POST',
      headers: {
        'content-type': 'application/json; charset=utf-8',
        'x-goog-api-key': apiKey
      },
      body: JSON.stringify(requestBody),
      signal: controller.signal
    });
    if (!response.ok) {
      console.error(`stable_gemini_http_${response.status}`);
      return { ok: false, error: 'gemini_provider_error', status: 502 };
    }
    const payload = await response.json();
    const output = extractJson(payload);
    if (!output) return { ok: false, error: 'gemini_invalid_output', status: 502 };
    return { ok: true, value: output };
  } catch (error) {
    if (error?.name === 'AbortError') return { ok: false, error: 'gemini_timeout', status: 504 };
    console.error('stable_gemini_request_failed');
    return { ok: false, error: 'gemini_provider_error', status: 502 };
  } finally {
    clearTimeout(timeout);
  }
}

function validatePlan(value, controls) {
  const statuses = new Set(['target', 'clarify', 'done']);
  const actions = new Set(['left_click', 'double_click', 'type_text', 'press_key', 'none']);
  const status = String(value?.status || '');
  const action = String(value?.action || '');
  if (!statuses.has(status) || !actions.has(action))
    return { ok: false, error: 'invalid_plan_shape' };

  const targetId = nullableText(value?.targetId, 80);
  const instruction = text(value?.instruction, 420);
  const question = nullableText(value?.question, 320);
  const key = nullableText(value?.key, 80);
  const confidence = clamp01(value?.confidence);
  const x = clamp1000(value?.x);
  const y = clamp1000(value?.y);
  const width = clamp1000(value?.width);
  const height = clamp1000(value?.height);

  if (containsSecretRequest(instruction) || containsSecretRequest(question || ''))
    return { ok: false, error: 'secret_request_rejected' };
  if (containsWarningBypass(instruction) || containsWarningBypass(question || ''))
    return { ok: false, error: 'warning_bypass_rejected' };

  if (status === 'clarify') {
    if (!question) return { ok: false, error: 'empty_question' };
    return { ok: true, value: { status, action: 'none', instruction, question, targetId: null, key: null, confidence, x: 0, y: 0, width: 0, height: 0 } };
  }

  if (!instruction) return { ok: false, error: 'empty_instruction' };

  if (status === 'done') {
    if (confidence < 0.85) return { ok: false, error: 'done_confidence_too_low' };
    return { ok: true, value: { status, action: 'none', instruction, question: null, targetId: null, key: null, confidence, x: 0, y: 0, width: 0, height: 0 } };
  }

  let target = null;
  if (targetId) {
    target = controls.find(c => c.id === targetId) || null;
    if (!target || !target.enabled) return { ok: false, error: 'unknown_or_disabled_target' };
  }

  if (['left_click', 'double_click', 'type_text'].includes(action)) {
    if (!target && (width <= 0 || height <= 0)) return { ok: false, error: 'missing_target_geometry' };
    if (target && confidence < 0.65) return { ok: false, error: 'structured_confidence_too_low' };
    if (!target && confidence < 0.85) return { ok: false, error: 'visual_confidence_too_low' };
  }

  if (action === 'type_text' && (!target || !target.focused || !target.keyboardFocusable))
    return { ok: false, error: 'unsafe_text_target' };

  if (action === 'press_key' && (!key || confidence < 0.72))
    return { ok: false, error: 'unsafe_key_action' };

  if (action === 'none') return { ok: false, error: 'target_without_action' };

  return {
    ok: true,
    value: { status, action, instruction, question: null, targetId, key, confidence, x, y, width, height }
  };
}

function compactControl(value) {
  if (!value || typeof value !== 'object') return null;
  const id = text(value.id, 80);
  if (!id) return null;
  return {
    id,
    name: sanitizeOutboundText(text(value.name, 180)),
    automationId: sanitizeOutboundText(text(value.automationId, 120)),
    className: sanitizeOutboundText(text(value.className, 120)),
    controlType: text(value.controlType, 80),
    enabled: value.enabled === true,
    focused: value.focused === true,
    keyboardFocusable: value.keyboardFocusable === true,
    x: clamp1000(value.x),
    y: clamp1000(value.y),
    width: clamp1000(value.width),
    height: clamp1000(value.height)
  };
}

const EMAIL = /(?<![\w.+-])[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}(?![\w.-])/gi;
const JP_PHONE = /(?<!\d)0\d{1,4}[-‐‑–—ー]?\d{1,4}[-‐‑–—ー]?\d{3,4}(?!\d)/g;
const JP_POSTAL = /〒?\s*\d{3}[-‐‑–—ー]?\d{4}/g;
const LABELED_SECRET = /(api[_ -]?key|token|secret|password|パスワード|秘密鍵|apiキー)\s*[:=]\s*\S{4,}/gi;
const BEARER = /bearer\s+[A-Za-z0-9._~+/=-]{12,}/gi;
const JWT = /eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}/g;
const CARD = /\b(?:\d[ -]?){13,19}\b/g;

function sanitizeOutboundText(value) {
  let result = String(value || '');
  result = result.replace(EMAIL, '<email>');
  result = result.replace(JP_PHONE, '<phone>');
  result = result.replace(JP_POSTAL, '<postal-code>');
  result = result.replace(BEARER, '<redacted-secret>');
  result = result.replace(JWT, '<redacted-secret>');
  result = result.replace(LABELED_SECRET, '<redacted-secret>');
  result = result.replace(CARD, '<redacted-card>');
  return result;
}

function containsSecretRequest(value) {
  return /(?:(password|passcode|パスワード|暗証|\bpin\b|otp|ワンタイム|認証コード|verification\s*code|recovery\s*key|リカバリ(?:ー)?キー|秘密鍵|private\s*key|api\s*key|apiキー|cvv|cvc|セキュリティコード).{0,36}(教え|送|貼|入力|記入|tell|send|paste|enter|type|provide)|(教え|送|貼|入力|記入|tell|send|paste|enter|type|provide).{0,36}(password|passcode|パスワード|暗証|\bpin\b|otp|ワンタイム|認証コード|verification\s*code|recovery\s*key|リカバリ(?:ー)?キー|秘密鍵|private\s*key|api\s*key|apiキー|cvv|cvc|セキュリティコード))/i.test(value || '');
}

function containsWarningBypass(value) {
  return /(proceed\s+anyway|continue\s+to\s+(?:the\s+)?site|ignore.{0,24}warning|bypass.{0,24}(warning|certificate|smartscreen)|advanced.{0,24}proceed|警告.{0,24}無視|無視して.{0,24}(続|進)|詳細設定.{0,24}(続行|アクセス|進)|安全ではありません.{0,24}(続|進)|危険.{0,24}続行|このサイトに進む)/i.test(value || '');
}

function parseDataImage(value) {
  const match = /^data:image\/(png|jpeg);base64,([A-Za-z0-9+/=]+)$/i.exec(value || '');
  if (!match) return null;
  return { mimeType: `image/${match[1].toLowerCase()}`, data: match[2] };
}

function extractJson(payload) {
  const candidates = Array.isArray(payload?.candidates) ? payload.candidates : [];
  for (const candidate of candidates) {
    const parts = Array.isArray(candidate?.content?.parts) ? candidate.content.parts : [];
    for (const part of parts) {
      if (part?.thought === true || typeof part?.text !== 'string') continue;
      try { return JSON.parse(part.text.trim()); }
      catch { }
    }
  }
  return null;
}

function hasKey(env) {
  return typeof env?.GEMINI_API_KEY === 'string' && env.GEMINI_API_KEY.trim().length > 0;
}
function text(value, max) { return typeof value === 'string' ? value.trim().slice(0, max) : ''; }
function nullableText(value, max) { const v = text(value, max); return v || null; }
function clamp01(value) { const n = Number(value); return Number.isFinite(n) ? Math.max(0, Math.min(1, n)) : 0; }
function clamp1000(value) { const n = Number(value); return Number.isFinite(n) ? Math.max(0, Math.min(1000, n)) : 0; }
function json(value, status = 200) {
  return new Response(JSON.stringify(value), {
    status,
    headers: { 'content-type': 'application/json; charset=utf-8', 'cache-control': 'no-store' }
  });
}
