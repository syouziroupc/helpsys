const VISION_MODEL = '@cf/zai-org/glm-5.3-flash';
const REASONING_MODEL = '@cf/zai-org/glm-5.3';
const GEMINI_MODEL = 'gemini-3.8-flash';
const VERSION = 'outlaw-2026.09.24-r3.7';
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
      coordinateSpace: { type: 'string', enum: ['image_px', 'none'] },
      coordinateImageWidth: { type: 'number', minimum: 0, maximum: 16384 },
      coordinateImageHeight: { type: 'number', minimum: 0, maximum: 16384 },
      x: { type: 'number', minimum: 0, maximum: 16384 },
      y: { type: 'number', minimum: 0, maximum: 16384 },
      width: { type: 'number', minimum: 0, maximum: 16384 },
      height: { type: 'number', minimum: 0, maximum: 16384 },
      screenConfirmed: { type: 'boolean' },
      visualEvidence: { type: 'string' }
    },
    required: [
      'status','targetId','action','instruction','question','key','confidence',
      'coordinateSpace','coordinateImageWidth','coordinateImageHeight',
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
- UI Automation can be incomplete. If a target is clearly visible but not represented by a useful UIA node, use targetId="vision-target" and return a tight rectangle in screenshot IMAGE PIXELS. Never use a 0-1000 normalized coordinate system.
- UIA text and values can contain details that are visually difficult to read. Context-only Text/Document/DataItem/Pane/Group nodes are evidence.
- Do not invent controls, labels, state, URLs, completed actions, or agreement between evidence sources.
- status=not_found is a last resort.
- When several low-risk/reversible choices are visible, DO NOT ask the user which one. Choose the best-grounded option yourself and say briefly that multiple choices existed, e.g. "候補が3つあります。今回は〇〇を選びます。".
- Use clarify only for high-impact choices where a wrong selection would materially change data, money, recipient, publication, deletion, overwrite/replace, or another hard-to-reverse outcome.
- If the user is on the wrong screen, guide the smallest grounded correction toward the goal.
- A desktop shortcut normally needs double_click. Standard buttons/menu items/taskbar buttons normally need one left click.
- If the requested goal needs a browser and that browser is already running, prefer bringing its visible taskbar/window surface forward over launching a generic browser shortcut, when that action is grounded in current evidence.
- If a browser/profile/account chooser exposes two or more distinct identities and no identity is named in the goal, choose one deterministically using current screen order/context instead of asking. State that multiple profiles exist and which one you selected. Do not treat this as identity verification.
- For older/beginner users, prefer a large visible page/search-engine search field that accepts a simple natural-language service name (for example "YouTube") over the browser omnibox when both are visible. Prefer a simple service/query word over a raw URL such as "youtube.com". Use direct URL entry only when no suitable natural-language search field is visible or direct entry is clearly simpler and less error-prone.
- Keep the Japanese instruction concrete and short.

OUTPUT:
- Call return_outlaw_guidance exactly once.
- For a UIA target, targetId must exactly equal one current element id.
- For a purely visual mouse target, targetId must be "vision-target", coordinateSpace must be "image_px", coordinateImageWidth/coordinateImageHeight must exactly equal capture.imageWidth/capture.imageHeight, and x/y/width/height must be screenshot pixel coordinates with origin at the screenshot's top-left.
- For UIA targets, key-only actions, clarify/done/not_found, set coordinateSpace="none", coordinateImageWidth=0, coordinateImageHeight=0 and x=y=width=height=0.
- screenConfirmed means the screenshot itself supports the chosen step.
- visualEvidence briefly states what visible evidence supports the decision.
- confidence is confidence in this exact next step.`;

const reviewPrompt = `You are HelpSys Outlaw stage 2, an independent multimodal grounding reviewer.
You DO receive the same raw screenshot pixels as stage 1. You also receive:
1) the full current Windows/UI Automation/system evidence,
2) the user's goal and history,
3) preliminaryVisionDecision from stage 1.

Your job is to independently verify the CURRENT screen and issue exactly ONE next step. Do not copy stage-1 geometry merely because it exists.

RULES:
- Re-read the screenshot yourself. preliminaryVisionDecision is a hypothesis, not authority.
- Prefer a real current UIA targetId when it cleanly identifies the visible actionable target.
- If a visual-only target is necessary, independently re-localize it from the screenshot and return your own rectangle.
- Visual rectangles use coordinateSpace="image_px" and exact screenshot pixels. coordinateImageWidth/coordinateImageHeight must equal capture.imageWidth/capture.imageHeight.
- If stage 1 points at a different object or substantially different place, correct it. The server will reject large geometric disagreement rather than silently choosing one.
- Do not invent controls, labels, states, URLs, or completed actions.
- Do not ask the user to describe the screen because recognition is difficult.
- For low-risk/reversible visible alternatives, choose one yourself and mention that alternatives existed. clarify is reserved for high-impact choices with materially different outcomes. A generic request to save does NOT authorize overwrite/replace of an existing file; clarify unless overwrite/replace was explicitly requested.
- not_found is a last resort when neither structured evidence nor the screenshot grounds a next step.
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
            const geminiChecked = validate(geminiRaw, elements, payload.capture);
            if (!geminiChecked.ok) return json({ error: geminiChecked.error }, 502);
            let geminiDecision = preferBeginnerSearchField(geminiChecked.value, elements, goal, payload.systemContext);
            if (image && geminiDecision.targetId === 'vision-target') {
              const geminiReviewPayload = {
                ...payload,
                screenshotPresent: true,
                preliminaryVisionDecision: geminiDecision
              };
              const geminiReviewRaw = await runGeminiGuidance(env, reviewPrompt, geminiReviewPayload, image);
              const geminiReviewChecked = validate(geminiReviewRaw, elements, payload.capture);
              if (!geminiReviewChecked.ok) return json({ error: geminiReviewChecked.error }, 502);
              const reconciled = reconcileVisionDecision(geminiReviewChecked.value, geminiDecision, payload.capture);
              if (!reconciled) return json(visualDisagreement(geminiDecision));
              geminiDecision = reconciled;
            }
            return json(guardUserChoice(geminiDecision, goal, elements));
          } catch (geminiError) {
            console.error(aiProvider === 'auto' ? 'outlaw_gemini_fallback' : 'outlaw_gemini_failed', geminiError);
            if (aiProvider === 'gemini') return json({ error: 'gemini_inference_failed' }, 502);
          }
        }
      }

      if (!image) {
        const structuredRaw = await runGuidance(env, REASONING_MODEL, visionPrompt, payload, null);
        const structuredChecked = validate(structuredRaw, elements, payload.capture);
        if (!structuredChecked.ok) return json({ error: structuredChecked.error }, 502);
        return json(guardUserChoice(structuredChecked.value, goal, elements));
      }

      const visionRaw = await runGuidance(env, VISION_MODEL, visionPrompt, payload, image);
      const visionChecked = validate(visionRaw, elements, payload.capture);
      if (!visionChecked.ok) return json({ error: visionChecked.error }, 502);
      let preliminary = visionChecked.value;
      preliminary = preferBeginnerSearchField(preliminary, elements, goal, payload.systemContext);

      if (canReturnFastStructured(preliminary, elements, payload.systemContext)) {
        return json(guardUserChoice(preliminary, goal, elements));
      }

      try {
        const reviewPayload = {
          ...payload,
          screenshotPresent: true,
          preliminaryVisionDecision: preliminary
        };
        const reviewRaw = await runGuidance(env, VISION_MODEL, reviewPrompt, reviewPayload, image);
        const reviewChecked = validate(reviewRaw, elements, payload.capture);
        if (!reviewChecked.ok) {
          console.warn('outlaw_reasoning_review_invalid', reviewChecked.error);
          return json(guardUserChoice(preliminary, goal, elements));
        }

        const finalDecision = reconcileVisionDecision(reviewChecked.value, preliminary, payload.capture);
        if (!finalDecision) {
          console.warn('outlaw_vision_review_geometry_disagreement');
          return json(visualDisagreement(preliminary));
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


function preferBeginnerSearchField(decision, elements, goal, systemContext) {
  if (!decision || decision.status !== 'target') return decision;
  if (!['type_text','left_click'].includes(String(decision.action || ''))) return decision;

  const current = elements.find(e => e.id === decision.targetId);
  const currentLooksOmnibox =
    current &&
    (
      /Omnibox/i.test(String(current.className || '')) ||
      /browser_address/i.test(String(current.automationId || ''))
    ) &&
    Number(current.y || 0) < 220;

  if (!currentLooksOmnibox) return decision;

  const foregroundPid = Number(systemContext?.foregroundProcessId ?? systemContext?.ForegroundProcessId ?? 0);
  const pageSearch = elements
    .filter(e =>
      e &&
      e.enabled !== false &&
      e.interactable !== false &&
      (!foregroundPid || !e.processId || e.processId === foregroundPid) &&
      ['edit','combobox'].includes(String(e.controlType || '').toLowerCase()) &&
      Number(e.y || 0) >= 220 &&
      Number(e.width || 0) >= 220 &&
      /(?:検索|search)/i.test(String(e.name || ''))
    )
    .sort((a,b) => (Number(b.width||0) * Number(b.height||0)) - (Number(a.width||0) * Number(a.height||0)))[0];

  if (!pageSearch) return decision;

  return {
    ...decision,
    targetId: pageSearch.id,
    action: 'left_click',
    instruction: `入力方法を簡単にするため、中央の「${pageSearch.name || '検索欄'}」を1回押してください。`,
    question: null,
    key: null,
    confidence: Math.max(Number(decision.confidence || 0), 0.96),
    x: Number(pageSearch.x)||0,
    y: Number(pageSearch.y)||0,
    width: Number(pageSearch.width)||0,
    height: Number(pageSearch.height)||0,
    coordinateSpace: 'none',
    coordinateImageWidth: 0,
    coordinateImageHeight: 0,
    screenConfirmed: true,
    visualEvidence: 'ブラウザのURL欄より、画面中央に大きな自然言語検索欄が見えているため、初心者向けにそちらを優先します。'
  };
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
    x: 0, y: 0,
    width: 0, height: 0,
    coordinateSpace: 'none',
    coordinateImageWidth: 0,
    coordinateImageHeight: 0,
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
      coordinateSpace: 'none',
      coordinateImageWidth: 0,
      coordinateImageHeight: 0,
      x: 0, y: 0, width: 0, height: 0,
      screenConfirmed: decision.screenConfirmed === true,
      visualEvidence: decision.visualEvidence || '置き換えと別の選択肢が同じ画面にあります。'
    };
  }

  return decision;
}

function reconcileVisionDecision(finalDecision, preliminary, capture) {
  const screenshotConfirmed =
    preliminary?.screenConfirmed === true && finalDecision?.screenConfirmed === true;

  if (finalDecision.status !== 'target' || finalDecision.targetId !== 'vision-target') {
    return {
      ...finalDecision,
      screenConfirmed: finalDecision?.screenConfirmed === true,
      visualEvidence: finalDecision.visualEvidence || preliminary?.visualEvidence || ''
    };
  }

  if (preliminary?.status !== 'target' || preliminary?.targetId !== 'vision-target')
    return null;
  if (!sameImageContract(preliminary, capture) || !sameImageContract(finalDecision, capture))
    return null;
  if (!visionGeometryAgrees(preliminary, finalDecision))
    return null;

  return {
    ...finalDecision,
    screenConfirmed: screenshotConfirmed,
    visualEvidence: finalDecision.visualEvidence || preliminary.visualEvidence || ''
  };
}

function visualDisagreement(preliminary) {
  return {
    status: 'not_found',
    targetId: null,
    action: 'none',
    instruction: '画像上の操作位置を二重確認できなかったため、古い座標は使用しません。',
    question: null,
    key: null,
    confidence: 0,
    coordinateSpace: 'none',
    coordinateImageWidth: 0,
    coordinateImageHeight: 0,
    x: 0, y: 0, width: 0, height: 0,
    screenConfirmed: false,
    visualEvidence: preliminary?.visualEvidence || 'independent visual grounding disagreed'
  };
}

function sameImageContract(decision, capture) {
  const imageWidth = positiveInt(capture?.imageWidth ?? capture?.ImageWidth);
  const imageHeight = positiveInt(capture?.imageHeight ?? capture?.ImageHeight);
  return decision?.coordinateSpace === 'image_px' &&
    imageWidth > 0 && imageHeight > 0 &&
    positiveInt(decision.coordinateImageWidth) === imageWidth &&
    positiveInt(decision.coordinateImageHeight) === imageHeight;
}

function visionGeometryAgrees(a, b) {
  const ar = rectOf(a), br = rectOf(b);
  if (!ar || !br) return false;
  const ix1 = Math.max(ar.x, br.x), iy1 = Math.max(ar.y, br.y);
  const ix2 = Math.min(ar.x + ar.w, br.x + br.w), iy2 = Math.min(ar.y + ar.h, br.y + br.h);
  const inter = Math.max(0, ix2 - ix1) * Math.max(0, iy2 - iy1);
  const union = ar.w * ar.h + br.w * br.h - inter;
  const iou = union > 0 ? inter / union : 0;
  const acx = ar.x + ar.w / 2, acy = ar.y + ar.h / 2;
  const bcx = br.x + br.w / 2, bcy = br.y + br.h / 2;
  const centerDistance = Math.hypot(acx - bcx, acy - bcy);
  const scale = Math.max(24, Math.min(Math.max(ar.w, ar.h), Math.max(br.w, br.h)));
  const widthRatio = Math.max(ar.w, br.w) / Math.max(1, Math.min(ar.w, br.w));
  const heightRatio = Math.max(ar.h, br.h) / Math.max(1, Math.min(ar.h, br.h));
  return (iou >= 0.35 || centerDistance <= Math.max(36, scale * 0.45)) &&
    widthRatio <= 2.5 && heightRatio <= 2.5;
}

function rectOf(d) {
  const x=finite(d?.x), y=finite(d?.y), w=finite(d?.width), h=finite(d?.height);
  return x >= 0 && y >= 0 && w > 0 && h > 0 ? {x,y,w,h} : null;
}

function validate(raw, elements, capture) {
  const statuses = new Set(['target','clarify','done','not_found']);
  const actions = new Set(['left_click','double_click','type_text','press_key','none']);
  const status = statuses.has(String(raw?.status || '')) ? String(raw.status) : 'not_found';
  const action = actions.has(String(raw?.action || '')) ? String(raw.action) : 'none';
  let targetId = nullableText(raw?.targetId, 80);
  const instruction = text(raw?.instruction, 900);
  const question = nullableText(raw?.question, 600);
  const key = nullableText(raw?.key, 120);
  const confidence = clamp01(raw?.confidence);
  const x = finite(raw?.x), y = finite(raw?.y);
  const width = finite(raw?.width), height = finite(raw?.height);
  const screenConfirmed = raw?.screenConfirmed === true;
  const visualEvidence = text(raw?.visualEvidence, 900);
  const imageWidth = positiveInt(capture?.imageWidth ?? capture?.ImageWidth);
  const imageHeight = positiveInt(capture?.imageHeight ?? capture?.ImageHeight);
  const rawSpace = String(raw?.coordinateSpace || 'none').toLowerCase();
  const rawCoordinateWidth = positiveInt(raw?.coordinateImageWidth);
  const rawCoordinateHeight = positiveInt(raw?.coordinateImageHeight);

  const noGeometry = {
    coordinateSpace:'none',
    coordinateImageWidth:0,
    coordinateImageHeight:0,
    x:0,y:0,width:0,height:0
  };

  if (status === 'clarify')
    return question
      ? { ok: true, value: { status, targetId: null, action: 'none', instruction, question, key: null, confidence, ...noGeometry, screenConfirmed, visualEvidence } }
      : { ok: false, error: 'empty_question' };

  if (status === 'done' || status === 'not_found')
    return { ok: true, value: { status, targetId: null, action:'none', instruction, question:null, key:null, confidence, ...noGeometry, screenConfirmed, visualEvidence } };

  if (action === 'none') return { ok:false, error:'target_without_action' };

  const visualMouse = ['left_click','double_click'].includes(action) && width > 0 && height > 0;
  if (!targetId && visualMouse) targetId = 'vision-target';

  if (targetId === 'vision-target') {
    if (!visualMouse) return { ok:false, error:'missing_visual_geometry' };
    if (rawSpace !== 'image_px') return { ok:false, error:'visual_coordinate_space_must_be_image_px' };
    if (imageWidth <= 0 || imageHeight <= 0) return { ok:false, error:'missing_capture_dimensions' };
    if (rawCoordinateWidth !== imageWidth || rawCoordinateHeight !== imageHeight)
      return { ok:false, error:'visual_coordinate_image_size_mismatch' };
    if (x < 0 || y < 0 || x + width > imageWidth || y + height > imageHeight)
      return { ok:false, error:'visual_geometry_out_of_image_bounds' };
  } else if (targetId) {
    const target = elements.find(e => e.id === targetId);
    if (!target || target.enabled === false || target.interactable === false)
      return { ok:false, error:'unknown_or_disabled_target' };
  } else if (action !== 'press_key') {
    return { ok:false, error:'missing_target' };
  }

  if (action === 'press_key' && !key) return { ok:false, error:'missing_key' };

  const geometry = targetId === 'vision-target'
    ? {
        coordinateSpace:'image_px',
        coordinateImageWidth:imageWidth,
        coordinateImageHeight:imageHeight,
        x,y,width,height
      }
    : noGeometry;

  return {
    ok:true,
    value:{
      status:'target', targetId, action, instruction, question:null, key,
      confidence, ...geometry, screenConfirmed, visualEvidence
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
function positiveInt(value){ const n=Math.round(finite(value)); return n > 0 ? n : 0; }
function clamp01(value){ return Math.max(0,Math.min(1,finite(value))); }
function json(value,status=200){
  return new Response(JSON.stringify(value),{
    status,
    headers:{'content-type':'application/json; charset=utf-8','cache-control':'no-store'}
  });
}
