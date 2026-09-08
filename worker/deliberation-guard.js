import base from './start-state-guard.js';

const DEFAULT_MODEL = '@cf/google/gemma-4-26b-a4b-it';
const MAX_REVIEW_ELEMENTS = 240;

const reviewTool = {
  name: 'return_guidance_review',
  description: 'Approve or reject the proposed single HelpSys instruction after careful internal reasoning.',
  parameters: {
    type: 'object',
    properties: {
      approved: { type: 'boolean' },
      confidence: { type: 'number', minimum: 0, maximum: 1 },
      reason: { type: 'string' }
    },
    required: ['approved', 'confidence', 'reason'],
    additionalProperties: false
  }
};

const reviewPrompt = `You are the final deliberation gate for HelpSys, a Windows guidance application for complete beginners.
A faster planner has proposed exactly one immediate instruction. Think carefully before approving it.
You MUST call return_guidance_review exactly once and output no prose outside the tool call.

Approve only when all of these are true:
- the proposed action is directly supported by the current foreground context and visible UI evidence;
- targetId, when present, is a current enabled/interactable element and the proposed action fits that control;
- the proposal is consistent with the user's goal and completed successful steps;
- it does not repeat a completed step or abruptly switch to an unrelated strategy;
- it does not rely on a transient typing suggestion, animation, incidental focus movement, or a background window;
- it is safe to say aloud now without immediately needing to retract it.

Reject if evidence is ambiguous, stale, contradictory, or if another screen transition appears to be in progress.
Do not invent or propose an alternative step. Do not reveal your reasoning. The reason field must be a short machine-oriented label, not chain-of-thought.`;

export default {
  async fetch(request, env, ctx) {
    let bodyPromise = null;
    let isStructuredGuide = false;

    try {
      const url = new URL(request.url);
      isStructuredGuide = request.method === 'POST' && url.pathname === '/v1/guide';
      if (isStructuredGuide) bodyPromise = request.clone().json();
    } catch { }

    const response = await base.fetch(request, env, ctx);
    if (!isStructuredGuide || !bodyPromise || response.status !== 200 || !env?.AI) return response;

    let body;
    let proposed;
    try {
      body = await bodyPromise;
      proposed = await response.clone().json();
    } catch {
      return response;
    }

    if (!proposed || String(proposed.status || '').toLowerCase() !== 'target') return response;

    const elements = Array.isArray(body?.elements)
      ? body.elements.slice(0, MAX_REVIEW_ELEMENTS).map(compactElement).filter(Boolean)
      : [];

    const targetId = String(proposed.targetId || '');
    if (targetId && !elements.some(x => x.id === targetId && x.interactable && x.enabled)) {
      return replaceJson(response, rejectedDecision());
    }

    const payload = JSON.stringify({
      goal: String(body?.request || '').slice(0, 1600),
      completedSteps: Array.isArray(body?.history) ? body.history.slice(-12) : [],
      systemContext: body?.systemContext ?? null,
      proposed,
      uiElements: elements
    });

    try {
      const result = await env.AI.run(env.HELPSYS_MODEL || DEFAULT_MODEL, {
        messages: [
          { role: 'system', content: reviewPrompt },
          { role: 'user', content: payload }
        ],
        temperature: 0,
        max_completion_tokens: 650,
        tools: [reviewTool],
        tool_choice: 'required',
        parallel_tool_calls: false,
        chat_template_kwargs: { enable_thinking: true }
      });

      const review = extractToolArguments(result, 'return_guidance_review');
      if (!review) return response;

      const approved = review.approved === true && Number(review.confidence) >= 0.74;
      return approved ? response : replaceJson(response, rejectedDecision());
    } catch (error) {
      console.error('guidance deliberation failed', error);
      // Keep the already validated base decision if the optional review service itself fails.
      return response;
    }
  }
};

function compactElement(value) {
  if (!value || typeof value !== 'object') return null;
  const id = text(value.id, 48);
  if (!id) return null;
  return {
    id,
    name: text(value.name, 160),
    automationId: text(value.automationId, 100),
    className: text(value.className, 100),
    controlType: text(value.controlType, 70),
    processName: text(value.processName, 70),
    interactable: value.interactable !== false,
    enabled: value.enabled !== false,
    keyboardFocusable: value.keyboardFocusable === true,
    focused: value.focused === true,
    password: value.password === true,
    x: finite(value.x),
    y: finite(value.y),
    width: finite(value.width),
    height: finite(value.height)
  };
}

function rejectedDecision() {
  return {
    status: 'not_found',
    targetId: null,
    action: 'none',
    instruction: '候補を再確認した結果、今はまだ安全に案内を確定できません。',
    question: null,
    key: null,
    confidence: 0
  };
}

function extractToolArguments(result, toolName) {
  const directCalls = Array.isArray(result?.tool_calls) ? result.tool_calls : [];
  const messageCalls = Array.isArray(result?.choices?.[0]?.message?.tool_calls)
    ? result.choices[0].message.tool_calls
    : [];

  for (const call of [...directCalls, ...messageCalls]) {
    const name = call?.name ?? call?.function?.name;
    if (name !== toolName) continue;
    const raw = call?.arguments ?? call?.function?.arguments;
    if (raw && typeof raw === 'object') return raw;
    if (typeof raw === 'string') {
      try { return JSON.parse(raw); }
      catch { return null; }
    }
  }

  const rawText = result?.response ?? result?.choices?.[0]?.message?.content;
  if (typeof rawText !== 'string') return null;
  try { return JSON.parse(stripCodeFence(rawText)); }
  catch { return null; }
}

function stripCodeFence(value) {
  const trimmed = value.trim();
  if (!trimmed.startsWith('```')) return trimmed;
  return trimmed.replace(/^```(?:json)?\s*/i, '').replace(/\s*```$/, '');
}

function replaceJson(response, value) {
  const headers = new Headers(response.headers);
  headers.set('content-type', 'application/json; charset=utf-8');
  return new Response(JSON.stringify(value), { status: 200, headers });
}

function text(value, limit) {
  if (value === null || value === undefined) return '';
  const out = String(value).trim().replace(/[\r\n\t]+/g, ' ');
  return out.length <= limit ? out : out.slice(0, limit);
}

function finite(value) {
  const n = Number(value);
  return Number.isFinite(n) ? n : 0;
}
