import base from './start-state-guard.js';

const MAX_REVIEW_ELEMENTS = 420;
const BROWSER_PROCESS = /^(chrome|msedge|firefox|brave|opera|vivaldi)$/i;
const ADDRESS_HINT = /(アドレス|address|location|omnibox|url\s*bar|urlbar|web\s*address)/i;

export default {
  async fetch(request, env, ctx) {
    let bodyPromise = null;
    let isStructuredGuide = false;

    try {
      const url = new URL(request.url);
      isStructuredGuide = request.method === 'POST' && url.pathname === '/v1/guide';
      if (isStructuredGuide) bodyPromise = request.clone().json();
    } catch { }

    // Do the careful reasoning in the one planner inference rather than adding a second
    // Workers AI round trip. This avoids latency-induced false communication failures.
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

    // Older visible-first layers can mistake a webpage's own search Edit for the browser's
    // address field. If their instruction explicitly describes the top/address field but the
    // accessibility metadata has no address-bar identity, recover the canonical Ctrl+L route.
    // This is safer and cheaper than returning not_found and forcing an unnecessary screenshot.
    const proposedInstruction = String(proposed.instruction || '');
    if (target && BROWSER_PROCESS.test(target.processName) && target.controlType.toLowerCase() === 'edit' &&
        (action === 'left_click' || action === 'type_text') &&
        /(画面上部|アドレス|ホームページのアドレス)/.test(proposedInstruction) &&
        !looksLikeBrowserAddressField(target)) {
      return replaceJson(response, browserAddressShortcut());
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
    name: text(value.name, 180),
    automationId: text(value.automationId, 120),
    className: text(value.className, 120),
    controlType: text(value.controlType, 70),
    processName: text(value.processName, 70),
    interactable: value.interactable !== false,
    enabled: value.enabled !== false,
    keyboardFocusable: value.keyboardFocusable === true,
    focused: value.focused === true,
    password: value.password === true
  };
}

function looksLikeBrowserAddressField(target) {
  return ADDRESS_HINT.test(`${target.name} ${target.automationId} ${target.className}`);
}

function browserAddressShortcut() {
  return {
    status: 'target',
    targetId: null,
    action: 'press_key',
    instruction: 'キーボードの「Ctrl」と書かれたキーを押したまま、「L」と書かれたキーを1回押してください。',
    question: null,
    key: 'Ctrl+L',
    confidence: 0.99
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
