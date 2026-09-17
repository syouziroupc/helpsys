const MODEL = 'gemini-3.8-flash';
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

const systemPrompt = `You are the only planning model for HelpSys Stable, a Windows guidance application for complete beginners.
The human performs every action. HelpSys never clicks, types, submits, installs, purchases, deletes, or changes settings itself.
Return exactly one immediate next step in the required JSON schema.

CURRENT-STATE RULES:
- screenshot is the current visible foreground window.
- controls are current Windows UI Automation elements. Their x/y/width/height are normalized 0..1000 relative to the screenshot/window.
- browserDomain, when present, is locally extracted from the browser address field with path/query discarded.
- Choose a real current action that advances the user's stated goal. Do not require a canonical or imagined route.
- Prefer an existing enabled control targetId when one is suitable. If no reliable UIA control exists but the screenshot clearly shows the target, use targetId=null and return a tight visual rectangle.
- A desktop Explorer ListItem used to launch an app/shortcut normally requires double_click.
- press_key may use targetId=null when the current screen clearly supports that keyboard action.
- type_text is only guidance: the user types. Never include or infer the text of passwords, PINs, OTPs, private keys, API keys, payment-card authentication data, or other secrets.

DO NOT BECOME PASSIVE:
- Do not return a refusal merely because the current screen differs from a standard route.
- Do not ask the user to diagnose HelpSys, describe the screen, or name a visible button just because recognition is difficult.
- Use clarify only when the USER must make a genuine choice: account/identity, overwrite/delete, purchase/payment, permission/default selection, or another materially different outcome.
- Otherwise choose the best grounded next operation from current evidence.

SAFETY BOUNDARY:
- Never tell the user to bypass browser certificate/phishing/malware/privacy warnings.
- Never instruct the user to reveal secrets to HelpSys.
- For known websites, do not select sponsored results or lookalike domains. Use browserDomain when available.
- Never claim an action already happened unless it is visible now.
- done is allowed only when the user's requested goal is visibly achieved now.

OUTPUT QUALITY:
- One action only.
- Short concrete Japanese for a PC beginner.
- confidence is confidence in this exact next action on the current screen.
- If targetId is present, it must exactly match one current control id.
- If targetId is null for a mouse action, rectangle width and height must both be > 0.`;

export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    if (request.method === 'GET' && url.pathname === '/health') {
      return json({ ok: true, service: 'helpsys-stable', model: MODEL, geminiConfigured: hasKey(env) });
    }
    if (request.method !== 'POST' || url.pathname !== '/v1/plan') return json({ error: 'not_found' }, 404);
    if (!hasKey(env)) return json({ error: 'gemini_unconfigured' }, 503);

    const declaredLength = Number(request.headers.get('content-length') || 0);
    if (declaredLength > MAX_BODY_BYTES) return json({ error: 'payload_too_large' }, 413);

    let body;
    try { body = await request.json(); }
    catch { return json({ error: 'invalid_json' }, 400); }

    const goal = text(body?.goal, 1000);
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
      windowTitle: text(body?.windowTitle, 260),
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
      maxOutputTokens: 1000,
      responseMimeType: 'application/json',
      responseJsonSchema: responseSchema,
      thinkingConfig: { thinkingLevel: 'medium' }
    }
  };

  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), 22_000);
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
  if (!statuses.has(status) || !actions.has(action)) return { ok: false, error: 'invalid_plan_shape' };

  const targetId = nullableText(value?.targetId, 80);
  const instruction = text(value?.instruction, 420);
  const question = nullableText(value?.question, 320);
  const key = nullableText(value?.key, 80);
  const confidence = clamp01(value?.confidence);
  const x = clamp1000(value?.x);
  const y = clamp1000(value?.y);
  const width = clamp1000(value?.width);
  const height = clamp1000(value?.height);

  if (!instruction && status !== 'clarify') return { ok: false, error: 'empty_instruction' };
  if (status === 'clarify' && !question) return { ok: false, error: 'empty_question' };
  if (status === 'target') {
    if (targetId && !controls.some(c => c.id === targetId)) return { ok: false, error: 'unknown_target' };
    if (['left_click', 'double_click', 'type_text'].includes(action) && !targetId && (width <= 0 || height <= 0))
      return { ok: false, error: 'missing_target_geometry' };
    if (action === 'press_key' && !key) return { ok: false, error: 'missing_key' };
  }

  return {
    ok: true,
    value: { status, action, instruction, question, targetId, key, confidence, x, y, width, height }
  };
}

function compactControl(value) {
  if (!value || typeof value !== 'object') return null;
  const id = text(value.id, 80);
  if (!id) return null;
  return {
    id,
    name: text(value.name, 180),
    controlType: text(value.controlType, 80),
    enabled: value.enabled !== false,
    focused: value.focused === true,
    keyboardFocusable: value.keyboardFocusable === true,
    x: clamp1000(value.x),
    y: clamp1000(value.y),
    width: clamp1000(value.width),
    height: clamp1000(value.height)
  };
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

function hasKey(env) { return typeof env?.GEMINI_API_KEY === 'string' && env.GEMINI_API_KEY.trim().length > 0; }
function text(value, max) { return typeof value === 'string' ? value.trim().slice(0, max) : ''; }
function nullableText(value, max) { const v = text(value, max); return v || null; }
function clamp01(value) { const n = Number(value); return Number.isFinite(n) ? Math.max(0, Math.min(1, n)) : 0; }
function clamp1000(value) { const n = Number(value); return Number.isFinite(n) ? Math.max(0, Math.min(1000, n)) : 0; }
function json(value, status = 200) { return new Response(JSON.stringify(value), { status, headers: { 'content-type': 'application/json; charset=utf-8', 'cache-control': 'no-store' } }); }
