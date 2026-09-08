import base from './start-state-guard.js';

const MAX_REVIEW_ELEMENTS = 240;

export default {
  async fetch(request, env, ctx) {
    let bodyPromise = null;
    let isStructuredGuide = false;

    try {
      const url = new URL(request.url);
      isStructuredGuide = request.method === 'POST' && url.pathname === '/v1/guide';
      if (isStructuredGuide) bodyPromise = request.clone().json();
    } catch { }

    // The previous implementation called Workers AI twice: once to plan, then again to
    // review the same instruction. The Windows client has a finite request budget, so a
    // healthy network could still be reported as a communication failure when those two
    // inference latencies accumulated. Keep the careful reasoning, but do it in the one
    // planner inference instead of adding a second network/inference round.
    const response = await base.fetch(request, singlePassReasoningEnv(env, isStructuredGuide), ctx);
    if (!isStructuredGuide || !bodyPromise || response.status !== 200) return response;

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

    const action = String(proposed.action || '').toLowerCase();
    const targetId = String(proposed.targetId || '');
    const target = targetId ? elements.find(x => x.id === targetId) : null;

    // Final review is deliberately deterministic. It adds effectively no latency and
    // prevents stale/non-operable targets from reaching speech output.
    if (targetId && (!target || !target.interactable || !target.enabled)) {
      return replaceJson(response, rejectedDecision());
    }

    if (action === 'type_text' && (!target || !target.focused || !target.keyboardFocusable || target.password)) {
      return replaceJson(response, rejectedDecision());
    }

    if ((action === 'left_click' || action === 'double_click') && !target) {
      return replaceJson(response, rejectedDecision());
    }

    if (action === 'press_key' && targetId && !target) {
      return replaceJson(response, rejectedDecision());
    }

    return response;
  }
};

function singlePassReasoningEnv(env, enabled) {
  if (!enabled || !env?.AI || typeof env.AI.run !== 'function') return env;

  const ai = env.AI;
  const reasoningAI = {
    run(model, options = {}) {
      return ai.run(model, {
        ...options,
        chat_template_kwargs: {
          ...(options.chat_template_kwargs || {}),
          enable_thinking: true
        }
      });
    }
  };

  return new Proxy(env, {
    get(target, property, receiver) {
      if (property === 'AI') return reasoningAI;
      return Reflect.get(target, property, receiver);
    }
  });
}

function compactElement(value) {
  if (!value || typeof value !== 'object') return null;
  const id = text(value.id, 48);
  if (!id) return null;
  return {
    id,
    controlType: text(value.controlType, 70),
    processName: text(value.processName, 70),
    interactable: value.interactable !== false,
    enabled: value.enabled !== false,
    keyboardFocusable: value.keyboardFocusable === true,
    focused: value.focused === true,
    password: value.password === true
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
