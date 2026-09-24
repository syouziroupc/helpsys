const VISION_MODEL = '@cf/zai-org/glm-5.3-flash';
const REASONING_MODEL = '@cf/zai-org/glm-5.3';
const GEMINI_MODEL = 'gemini-3.8-flash';
const VERSION = 'outlaw-2026.09.24-r3.6';
const MAX_BODY_BYTES = 50_000_000;
const MAX_UI_ELEMENTS = 4000;
const MAX_HISTORY = 64;
const MAX_COMPLETION_TOKENS = 6000;

const tool = {
  name: 'return_outlaw_guidance',
  description: 'Return exactly one current-screen HelpSys guidance step using all available evidence.',
  parameters: {
    type: 'object',
    properties: {
      status: { type: 'string', enum: ['target', 'clarify', 'done', 'not_found'] },
      targetId: { type: ['string', 'null'] },
      action: { type: 'string', enum: ['left_click', 'double_click', 'type_text', 'press_key', 'none'] },
      instruction: { type: 'string' },
      question: { type: ['string', 'null'] },
      key: { type: ['string', 'null'] },
      confidence: { type: 'number', minimum: 0, maximum: 1 },
      x: { type: 'number', minimum: 0, maximum: 1000 },
      y: { type: 'number', minimum: 0, maximum: 1000 },
      width: { type: 'number', minimum: 0, maximum: 1000 },
      height: { type: 'number', minimum: 0, maximum: 1000 },
      screenConfirmed: { type: 'boolean' },
      visualEvidence: { type: 'string' }
    },
    required: [
      'status','targetId','action','instruction','question','key','confidence',
      'x','y','width','height','screenConfirmed','visualEvidence'
    ],
    additionalProperties: false
  }
};

const visionPrompt = `You are HelpSys Outlaw stage 1, an accuracy-first multimodal Windows screen analyst and planner.
The human performs all actions. Determine the CURRENT screen and propose exactly ONE immediate next step.

EVIDENCE RULES:
- Spend the needed reasoning effort before answering.
- Use the screenshot when present, the full UI Automation set, foreground/window context, browser context, running apps, values/states, and operation history together.
- Reconstruct where the user is now before deciding what comes next. Current evidence wins over history.
- UI Automation can be incomplete. If a target is clearly visible but not represented by a useful UIA node, use targetId="vision-target" and return a tight normalized rectangle.
- UIA text and values can contain details that are visually difficult to read. Context-only Text/Document/DataItem/Pane/Group nodes are evidence.
- Do not invent controls, labels, state, URLs, completed actions, or agreement between evidence sources.
- status=not_found is a last resort.
- When several low-risk/reversible choices are visible, DO NOT ask the user which one. Choose the best-grounded option yourself and say briefly that multiple choices existed, e.g. "候補が3つあります。今回は〇〇を選びます。".
- Use clarify only for high-impact choices where a wrong selection would materially change data, money, recipient, publication, deletion, overwrite/replace, or another hard-to-reverse outcome.
- If the user is on the wrong screen, guide the smallest grounded correction toward the goal.
- A desktop shortcut normally needs double_click. Standard buttons/menu items/taskbar buttons normally need one left click.
- If the requested goal needs a browser and that browser is already running, prefer bringing its visible taskbar/window surface forward over launching a generic browser shortcut, when that action is grounded in current evidence.
- If a browser/profile/account chooser exposes two or more distinct identities and no identity is named in the goal, choose one deterministically using current screen order/context instead of asking. State that multiple profiles exist and which one you selected. Do not treat this as identity verification.
- For older/beginner users, prefer a visible search field that accepts a simple natural-language service name (for example "YouTube") over asking them to type a raw URL such as "youtube.com". Use direct URL entry only when no suitable search field is visible or direct entry is clearly simpler and less error-prone.
- Keep the Japanese instruction concrete and short.

OUTPUT:
- Call return_outlaw_guidance exactly once.
- For a UIA target, targetId must exactly equal one current element id.
- For a purely visual mouse target, targetId must be "vision-target" and width/height must be positive.
- screenConfirmed means the screenshot itself supports the chosen step.
- visualEvidence briefly states what visible evidence supports the decision.
- confidence is confidence in this exact next step.`;

const reviewPrompt = `You are HelpSys Outlaw stage 2, the final high-reasoning reviewer.
You do NOT receive raw screenshot pixels. You receive:
1) the full current Windows/UI Automation/system evidence,
2) the user's goal and history,
3) a preliminary decision produced by a vision-capable model that did see the screenshot.

Your job is to issue the most accurate ONE-step final guidance, correcting the preliminary decision when structured/current-state evidence shows a better answer.

RULES:
- Treat preliminaryVisionDecision as visual evidence, not as authority.
- Reconstruct the current state from all evidence before choosing the next operation.
- Prefer a real current UIA targetId when it cleanly identifies the same actionable target.
- You may keep targetId="vision-target" only if the preliminary decision identified a visual-only target. Do not invent a new visual target or new coordinates.
- If you keep vision-target, preserve the preliminary target's geometry conceptually; the server will enforce its coordinates.
- Do not invent controls, labels, states, URLs, or completed actions.
- Do not ask the user to describe the screen because recognition is difficult.
- For low-risk/reversible visible alternatives, choose one yourself and mention that alternatives existed. clarify is reserved for high-impact choices with materially different outcomes. A generic request to save does NOT authorize overwrite/replace of an existing file; clarify unless overwrite/replace was explicitly requested.
- not_found is a last resort when neither the structured evidence nor the preliminary visual evidence grounds a next step.
- Current evidence beats stale history or an imagined canonical route.
- Keep the Japanese instruction concrete and short.
- Call return_outlaw_guidance exactly once.`;

export default {
  async fetch(request, env) {
    const url = new URL(request.url);

    if (request.method === 'GET' && url.pathname === '/health/outlaw') {
      return json({
        ok: true,
        service: 'helpsys-outlaw',
        version: VERSION,
        visionModel: VISION_MODEL,
        reasoningModel: REASONING_MODEL,
        geminiModel: GEMINI_MODEL,
        aiConfigured: Boolean(env?.AI),
        geminiConfigured: Boolean(env?.GEMINI_API_KEY),
        providers: ['auto','gemini','glm'],
        maxUiElements: MAX_UI_ELEMENTS,
        maxHistory: MAX_HISTORY,
        reasoningEffort: 'high',
        maxCompletionTokens: MAX_COMPLETION_TOKENS,
        dualStageVisionReview: true,
        fastStructuredReturn: true
      });
    }

    if (request.method !== 'POST' || url.pathname !== '/v1/outlaw-plan')
      return json({ error: 'not_found' }, 404);
    if (!env?.AI) return json({ error: 'workers_ai_unconfigured' }, 503);

    const declared = Number(request.headers.get('content-length') || 0);
    if (declared > MAX_BODY_BYTES) return json({ error: 'payload_too_large' }, 413);

    let body;
    try { body = await request.json(); }
    catch { return json({ error: 'invalid_json' }, 400); }

    const goal = text(body?.request, 4000);
    const image = typeof body?.image === 'string' ? body.image : '';
    const aiProvider = normalizeAiProvider(body?.aiProvider);
    if (!goal || (image && (!/^data:image\/(?:png|jpeg);base64,/i.test(image) || image.length > MAX_BODY_BYTES)))
      return json({ error: 'invalid_request' }, 400);

    const elements = Array.isArray(body?.elements)
      ? body.elements.slice(0, MAX_UI_ELEMENTS).map(compactElement).filter(Boolean)
      : [];
    const history = Array.isArray(body?.history) ? body.history.slice(-MAX_HISTORY) : [];
    const payload = {
      goal,
      systemContext: body?.systemContext ?? null,
      capture: body?.capture ?? null,
      evidence: body?.evidence ?? null,
      screenshotPresent: Boolean(image),
      completedSteps: history,
      uiElements: elements
    };

    const identityChoice = detectIdentityChoice(elements, payload.systemContext, goal);
    if (identityChoice) return json(identityChoice);

    try {
      if (aiProvider !== 'glm') {
        if (!env?.GEMINI_API_KEY) {
          if (aiProvider === 'gemini') return json({ error: 'gemini_not_configured' }, 503);
        } else {
          try {
            const geminiRaw = await runGeminiGuidance(env, visionPrompt, payload, image || null);
            const geminiChecked = validate(geminiRaw, elements);
            if (!geminiChecked.ok) return json({ error: geminiChecked.error }, 502);
            return json(guardUserChoice(geminiChecked.value, goal, elements));
          } catch (geminiError) {
            console.error(aiProvider === 'auto' ? 'outlaw_gemini_fallback' : 'outlaw_gemini_failed', geminiError);
            if (aiProvider === 'gemini') return json({ error: 'gemini_inference_failed' }, 502);
          }
        }
      }

      if (!image) {
        const structuredRaw = await runGuidance(env, REASONING_MODEL, visionPrompt, payload, null);
        const structuredChecked = validate(structuredRaw, elements);
        if (!structuredChecked.ok) return json({ error: structuredChecked.error }, 502);
        return json(guardUserChoice(structuredChecked.value, goal, elements));
      }

      const visionRaw = await runGuidance(env, VISION_MODEL, visionPrompt, payload, image);
      const visionChecked = validate(visionRaw, elements);
      if (!visionChecked.ok) return json({ error: visionChecked.error }, 502);
      const preliminary = visionChecked.value;

      if (canReturnFastStructured(preliminary, elements, payload.systemContext)) {
        return json(guardUserChoice(preliminary, goal, elements));
      }

      try {
        const reviewPayload = {
          ...payload,
          screenshotPresent: true,
          preliminaryVisionDecision: preliminary
        };
        const reviewRaw = await runGuidance(env, REASONING_MODEL, reviewPrompt, reviewPayload, null);
        const reviewChecked = validate(reviewRaw, elements);
        if (!reviewChecked.ok) {
          console.warn('outlaw_reasoning_review_invalid', reviewChecked.error);
          return json(guardUserChoice(preliminary, goal, elements));
        }

        const finalDecision = enforceVisionGeometry(reviewChecked.value, preliminary);
        if (!finalDecision) {
          console.warn('outlaw_reasoning_review_visual_mismatch');
          return json(guardUserChoice(preliminary, goal, elements));
        }
        return json(guardUserChoice(finalDecision, goal, elements));
      } catch (reviewError) {
        console.error('outlaw_reasoning_review_failed', reviewError);
        return json(guardUserChoice(preliminary, goal, elements));
      }
    } catch (error) {
      console.error('outlaw_inference_failed', error);
      return json({ error: 'outlaw_inference_failed' }, 502);
    }
  }
};

async function runGeminiGuidance(env, system, payload, image) {
  const match = image ? /^data:image\/(png|jpeg);base64,(.+)$/i.exec(image) : null;
  if (image && !match) throw new Error('invalid_gemini_image');

  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), 9_000);
  try {
    const response = await fetch(
      `https://generativelanguage.googleapis.com/v1beta/models/${encodeURIComponent(GEMINI_MODEL)}:generateContent`,
      {
        method: 'POST',
        headers: {
          'content-type': 'application/json',
          'x-goog-api-key': env.GEMINI_API_KEY
        },
        body: JSON.stringify({
          systemInstruction: { parts: [{ text: system }] },
          contents: [{
            role: 'user',
            parts: match
              ? [
                  { text: JSON.stringify(payload) },
                  { inlineData: { mimeType: `image/${match[1].toLowerCase()}`, data: match[2] } }
                ]
              : [{ text: JSON.stringify(payload) }]
          }],
          generationConfig: {
            temperature: 0,
            maxOutputTokens: 1800,
            thinkingConfig: { thinkingLevel: 'medium' },
            responseMimeType: 'application/json',
            responseSchema: tool.parameters
          }
        }),
        signal: controller.signal
      }
    );
    if (!response.ok) throw new Error(`gemini_http_${response.status}`);
    const data = await response.json();
    const rawText = data?.candidates?.[0]?.content?.parts?.find(x => typeof x?.text === 'string')?.text;
    if (!rawText) throw new Error('gemini_empty_output');
    return JSON.parse(rawText);
  } finally {
    clearTimeout(timer);
  }
}

async function runGuidance(env, model, system, payload, image) {
  const fullReasoning = model === REASONING_MODEL;
  const request = {
    messages: [
      { role: 'system', content: system },
      { role: 'user', content: JSON.stringify(payload) }
    ],
    temperature: 0,
    reasoning_effort: fullReasoning ? 'high' : 'low',
    max_completion_tokens: fullReasoning ? MAX_COMPLETION_TOKENS : 1400,
    tools: [tool],
    tool_choice: 'required',
    parallel_tool_calls: false
  };
  if (image) request.image = image;
  const result = await env.AI.run(model, request);
  const raw = extractToolArguments(result, 'return_outlaw_guidance');
  if (!raw) throw new Error('invalid_model_output');
  return raw;
}


function detectIdentityChoice(elements, systemContext, goal) {
  const foregroundPid = Number(systemContext?.foregroundProcessId ?? systemContext?.ForegroundProcessId ?? 0);
  const choices = elements.filter(e =>
    e &&
    e.enabled !== false &&
    e.interactable !== false &&
    (!foregroundPid || !e.processId || e.processId === foregroundPid) &&
    (
      String(e.automationId || '').toLowerCase() === 'profilecardbutton' ||
      /(?:プロフィール|profile).*(?:開く|open)/i.test(String(e.name || ''))
    )
  ).sort((a,b) => (Number(a.y)-Number(b.y)) || (Number(a.x)-Number(b.x)));
  if (choices.length < 2) return null;

  const normalizedGoal = String(goal || '').toLowerCase();
  const named = choices.find(e => {
    const label = String(e.name || '').replace(/(?:のプロフィールを開く|プロフィール.*|profile.*)$/i, '').trim().toLowerCase();
    return label.length >= 2 && normalizedGoal.includes(label);
  });
  const selected = named || choices[0];
  return {
    status: 'target',
    targetId: selected.id,
    action: 'left_click',
    instruction: `候補が${choices.length}つあります。今回は「${selected.name || '先頭のプロフィール'}」を選びます。青い枠の項目を1回押してください。`,
    question: null,
    key: null,
    confidence: named ? 0.99 : 0.9,
    x: Number(selected.x)||0, y: Number(selected.y)||0,
    width: Number(selected.width)||0, height: Number(selected.height)||0,
    screenConfirmed: true,
    visualEvidence: `複数のプロフィール候補が表示されているため、${named ? '利用者の目的文に一致する候補' : '現在画面で最初の候補'}を選択します。`
  };
}

function canReturnFastStructured(decision, elements, systemContext) {
  if (!decision || decision.status !== 'target' || !decision.targetId || decision.targetId === 'vision-target')
    return false;
  if (Number(decision.confidence || 0) < 0.88) return false;

  const target = elements.find(e => e.id === decision.targetId);
  if (!target || target.enabled === false || target.interactable === false) return false;

  const foregroundPid = Number(systemContext?.foregroundProcessId ?? systemContext?.ForegroundProcessId ?? 0);
  if (foregroundPid > 0 && target.processId > 0 && target.processId !== foregroundPid) return false;

  if (decision.action === 'type_text' && !(target.focused === true && target.keyboardFocusable === true))
    return false;

  return true;
}

function guardUserChoice(decision, goal, elements) {
  if (!decision || decision.status !== 'target' || !decision.targetId) return decision;

  const chosen = elements.find(e => e.id === decision.targetId);
  if (!chosen) return decision;

  const chosenText = `${chosen.name || ''} ${chosen.automationId || ''}`.toLowerCase();
  const screenText = elements.map(e => String(e?.name || '')).join(' ').toLowerCase();
  const goalText = String(goal || '').toLowerCase();

  const replaceTarget =
    /(置き換|上書|replace|overwrite)/i.test(chosenText);
  const replacementChoiceScreen =
    /(置き換|上書|replace|overwrite)/i.test(screenText) &&
    /(スキップ|skip|キャンセル|cancel|両方|both|比較|compare)/i.test(screenText);
  const explicitlyRequestedReplace =
    /(置き換|上書|replace|overwrite)/i.test(goalText);

  if (replaceTarget && replacementChoiceScreen && !explicitlyRequestedReplace) {
    return {
      status: 'clarify',
      targetId: null,
      action: 'none',
      instruction: '既存のファイルを置き換えるかどうかの確認が必要です。',
      question: '同じ名前のファイルが既にあります。既存ファイルを上書きして置き換えますか、それとも残しますか？',
      key: null,
      confidence: 1,
      x: 0, y: 0, width: 0, height: 0,
      screenConfirmed: decision.screenConfirmed === true,
      visualEvidence: decision.visualEvidence || '置き換えと別の選択肢が同じ画面にあります。'
    };
  }

  return decision;
}

function enforceVisionGeometry(finalDecision, preliminary) {
  // Only the vision-capable first stage saw raw pixels. The text-only reviewer may
  // strengthen/correct reasoning, but it may never upgrade screenshot confirmation.
  const screenshotConfirmed =
    preliminary?.screenConfirmed === true && finalDecision?.screenConfirmed === true;

  if (finalDecision.status !== 'target' || finalDecision.targetId !== 'vision-target') {
    return {
      ...finalDecision,
      screenConfirmed: screenshotConfirmed,
      visualEvidence: preliminary?.visualEvidence || finalDecision.visualEvidence
    };
  }

  if (preliminary.status !== 'target' || preliminary.targetId !== 'vision-target')
    return null;

  return {
    ...finalDecision,
    x: preliminary.x,
    y: preliminary.y,
    width: preliminary.width,
    height: preliminary.height,
    screenConfirmed: screenshotConfirmed,
    visualEvidence: preliminary.visualEvidence || finalDecision.visualEvidence
  };
}

function validate(raw, elements) {
  const statuses = new Set(['target','clarify','done','not_found']);
  const actions = new Set(['left_click','double_click','type_text','press_key','none']);
  const status = statuses.has(String(raw?.status || '')) ? String(raw.status) : 'not_found';
  const action = actions.has(String(raw?.action || '')) ? String(raw.action) : 'none';
  let targetId = nullableText(raw?.targetId, 80);
  const instruction = text(raw?.instruction, 900);
  const question = nullableText(raw?.question, 600);
  const key = nullableText(raw?.key, 120);
  const confidence = clamp01(raw?.confidence);
  const x = clamp1000(raw?.x), y = clamp1000(raw?.y);
  const width = clamp1000(raw?.width), height = clamp1000(raw?.height);
  const screenConfirmed = raw?.screenConfirmed === true;
  const visualEvidence = text(raw?.visualEvidence, 900);

  if (status === 'clarify')
    return question
      ? { ok: true, value: { status, targetId: null, action: 'none', instruction, question, key: null, confidence, x:0,y:0,width:0,height:0,screenConfirmed,visualEvidence } }
      : { ok: false, error: 'empty_question' };

  if (status === 'done' || status === 'not_found')
    return { ok: true, value: { status, targetId: null, action:'none', instruction, question:null, key:null, confidence, x:0,y:0,width:0,height:0,screenConfirmed,visualEvidence } };

  if (action === 'none') return { ok:false, error:'target_without_action' };

  const visualMouse = ['left_click','double_click'].includes(action) && width > 0 && height > 0;
  if (!targetId && visualMouse) targetId = 'vision-target';

  if (targetId === 'vision-target') {
    if (!visualMouse) return { ok:false, error:'missing_visual_geometry' };
  } else if (targetId) {
    const target = elements.find(e => e.id === targetId);
    if (!target || target.enabled === false || target.interactable === false)
      return { ok:false, error:'unknown_or_disabled_target' };
  } else if (action !== 'press_key') {
    return { ok:false, error:'missing_target' };
  }

  if (action === 'press_key' && !key) return { ok:false, error:'missing_key' };

  return {
    ok:true,
    value:{
      status:'target', targetId, action, instruction, question:null, key,
      confidence, x,y,width,height,screenConfirmed,visualEvidence
    }
  };
}

function compactElement(value) {
  if (!value || typeof value !== 'object') return null;
  const id = text(value.id, 80);
  if (!id) return null;
  return {
    id,
    name: text(value.name, 900),
    automationId: text(value.automationId, 300),
    className: text(value.className, 300),
    controlType: text(value.controlType, 120),
    processName: text(value.processName, 120),
    interactable: value.interactable !== false,
    enabled: value.enabled !== false,
    keyboardFocusable: value.keyboardFocusable === true,
    focused: value.focused === true,
    password: value.password === true,
    processId: finite(value.processId),
    value: nullableText(value.value, 1400),
    toggleState: nullableText(value.toggleState, 120),
    selected: typeof value.selected === 'boolean' ? value.selected : null,
    expandCollapseState: nullableText(value.expandCollapseState, 120),
    x: finite(value.x), y: finite(value.y), width: finite(value.width), height: finite(value.height)
  };
}

function extractToolArguments(result, toolName) {
  const calls = [
    ...(Array.isArray(result?.tool_calls) ? result.tool_calls : []),
    ...(Array.isArray(result?.choices?.[0]?.message?.tool_calls) ? result.choices[0].message.tool_calls : [])
  ];
  for (const call of calls) {
    const name = call?.name ?? call?.function?.name;
    if (name !== toolName) continue;
    const args = call?.arguments ?? call?.function?.arguments;
    if (args && typeof args === 'object') return args;
    if (typeof args === 'string') { try { return JSON.parse(args); } catch {} }
  }
  const raw = result?.response ?? result?.choices?.[0]?.message?.content;
  if (typeof raw !== 'string') return null;
  try { return JSON.parse(raw.replace(/^\x60\x60\x60(?:json)?\s*/i,'').replace(/\s*\x60\x60\x60$/,'')); } catch { return null; }
}

export function normalizeAiProvider(value) {
  const provider = String(value || 'auto').trim().toLowerCase();
  return provider === 'gemini' || provider === 'glm' ? provider : 'auto';
}

function text(value,max){ return typeof value === 'string' ? value.trim().slice(0,max) : ''; }
function nullableText(value,max){
  const v=text(value,max);
  if(!v) return null;
  const lower=v.toLowerCase();
  return lower==='null' || lower==='none' || lower==='undefined' || lower==='(null)' ? null : v;
}
function finite(value){ const n=Number(value); return Number.isFinite(n) ? n : 0; }
function clamp01(value){ return Math.max(0,Math.min(1,finite(value))); }
function clamp1000(value){ return Math.max(0,Math.min(1000,finite(value))); }
function json(value,status=200){
  return new Response(JSON.stringify(value),{
    status,
    headers:{'content-type':'application/json; charset=utf-8','cache-control':'no-store'}
  });
}
