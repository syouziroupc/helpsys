import base from './deliberation-guard.js';
import education from './education.js';
import quality from './quality-guide.js';
import transcribe from './transcribe.js';

const START_PROCESS = /(searchhost|startmenuexperiencehost)/i;
const APP_RULES = [
  { goal: /(excel|エクセル)/i, processes: ['excel'], search: 'Excel' },
  { goal: /(word|ワード)/i, processes: ['winword'], search: 'Word' },
  { goal: /(powerpoint|パワーポイント|パワポ)/i, processes: ['powerpnt'], search: 'PowerPoint' },
  { goal: /(chrome|クローム|グーグルクローム)/i, processes: ['chrome'], search: 'Google Chrome' },
  { goal: /(edge|エッジ)/i, processes: ['msedge'], search: 'Microsoft Edge' },
  { goal: /(メモ帳|notepad)/i, processes: ['notepad'], search: 'メモ帳' },
  { goal: /(電卓|calculator)/i, processes: ['calculatorapp'], search: '電卓' }
];

export default {
  async fetch(request, env, ctx) {
    let url;
    try { url = new URL(request.url); }
    catch { return base.fetch(request, env, ctx); }

    if (url.pathname === '/v1/transcribe') {
      return transcribe.fetch(request, env, ctx);
    }

    // Quality-first normal HelpSys planning always receives the current screenshot and
    // UI structure together. It owns its own deterministic validation and secret guard.
    if (url.pathname === '/v1/quality-guide') {
      return quality.fetch(request, env, ctx);
    }

    // Keep Education routing available, but normal HelpSys development is prioritized.
    if (url.pathname === '/v1/education/assist') {
      return education.fetch(request, env, ctx);
    }

    let bodyPromise = null;
    try {
      if (request.method === 'POST' && (url.pathname === '/v1/guide' || url.pathname === '/v1/vision-guide'))
        bodyPromise = request.clone().json();
    } catch { }

    const response = await base.fetch(request, env, ctx);
    if (!bodyPromise || response.status !== 200) return response;

    let body;
    let decision;
    try {
      body = await bodyPromise;
      decision = await response.clone().json();
    } catch {
      return response;
    }

    const secretOverride = guardSecretClarification(decision);
    if (secretOverride) return replaceJson(response, secretOverride);

    const override = isStructuredGuide(request) ? preventBackgroundDone(body, decision) : null;
    return override ? replaceJson(response, override) : response;
  }
};

export function guardSecretClarification(decision) {
  if (!decision || String(decision.status || '').toLowerCase() !== 'clarify') return null;
  const question = String(decision.question || decision.instruction || '');
  const secret = /(password|passcode|パスワード|暗証|\bpin\b|otp|ワンタイム|認証コード|verification\s*code|recovery\s*key|リカバリ(?:ー)?キー|秘密鍵|secret\s*key|cvv|cvc|セキュリティコード)/i;
  if (!secret.test(question)) return null;
  return {
    status: 'not_found', targetId: null, action: 'none',
    instruction: 'パスワード、暗証番号、認証コードなどの秘密情報はHelpSysへ入力しないでください。秘密情報そのものを聞かずに続けられる画面から案内をやり直します。',
    question: null, key: null, confidence: 0
  };
}

function isStructuredGuide(request) {
  try { return new URL(request.url).pathname === '/v1/guide'; } catch { return false; }
}

function preventBackgroundDone(body, decision) {
  if (!decision || String(decision.status || '').toLowerCase() !== 'done') return null;
  if (!/すでに開いて|既に開いて/i.test(String(decision.instruction || ''))) return null;

  const goal = String(body?.request || '');
  const rule = APP_RULES.find(x => x.goal.test(goal));
  if (!rule) return null;

  const foreground = String(body?.systemContext?.foregroundProcess ?? body?.systemContext?.ForegroundProcess ?? '').toLowerCase();
  if (rule.processes.includes(foreground)) return null;

  const elements = usable(body?.elements);
  const search = elements.find(x => String(x.controlType || '').toLowerCase() === 'edit' &&
    /(検索|search)/i.test(`${x.name || ''} ${x.automationId || ''}`) &&
    /searchhost|startmenuexperiencehost|explorer/i.test(String(x.processName || '')));

  if (search?.focused === true) {
    return {
      status: 'target', targetId: String(search.id), action: 'type_text',
      instruction: `キーボードで「${rule.search}」と入力し、最後に「Enter」と書かれたキーを1回押してください。`,
      question: null, key: 'Enter', confidence: 0.99
    };
  }
  if (search) {
    return {
      status: 'target', targetId: String(search.id), action: 'left_click',
      instruction: '開いている検索画面の、文字を入力できる欄で、マウスの左ボタンを1回押してください。',
      question: null, key: null, confidence: 0.99
    };
  }

  const startOpen = START_PROCESS.test(foreground) || elements.some(x =>
    START_PROCESS.test(String(x.processName || '')) &&
    (x.focused === true || /(検索|search|ピン留め|pinned|おすすめ|すべて)/i.test(String(x.name || ''))));
  if (startOpen) {
    return {
      status: 'not_found', targetId: null, action: 'none',
      instruction: '目的のアプリは別の画面で開いていますが、今見えている画面にはまだ出ていません。現在の検索画面から安全に選べる場所を確認し直します。',
      question: null, key: null, confidence: 0.99
    };
  }

  return {
    status: 'target', targetId: null, action: 'press_key',
    instruction: '目的のアプリは別の画面で開いています。今操作できる画面へ出すため、キーボードの左下にある窓の形の「Windows」キーを1回押してください。',
    question: null, key: 'Windows', confidence: 0.99
  };
}

function usable(raw) {
  return (Array.isArray(raw) ? raw : []).filter(x => x && x.interactable !== false && x.enabled !== false && String(x.id || '').trim());
}

function replaceJson(response, value) {
  const headers = new Headers(response.headers);
  headers.set('content-type', 'application/json; charset=utf-8');
  return new Response(JSON.stringify(value), { status: 200, headers });
}
