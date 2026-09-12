import base from './deliberation-guard.js';
import education from './education.js';
import quality from './quality-guide.js';
import transcribe from './transcribe.js';

const START_PROCESS = /(searchhost|startmenuexperiencehost)/i;
const SCREEN_ROUTES = new Set(['/v1/quality-guide', '/v1/guide', '/v1/vision-guide']);
const EMAIL = /(?<![\w.+-])[\w.+-]+@[\w.-]+\.[A-Za-z]{2,}(?![\w.-])/g;
const JP_PHONE = /(?<!\d)(?:(?:0[5789]0[- ]?\d{4}[- ]?\d{4})|(?:0\d{1,4}[- ]\d{1,4}[- ]\d{3,4})|(?:\+81[- ]?[1-9]\d{0,4}[- ]?\d{1,4}[- ]?\d{3,4}))(?!\d)/g;
const JP_POSTAL = /(?<!\d)〒?\s*\d{3}-\d{4}(?!\d)/g;
const LABELED_SECRET = /\b(password|passwd|passcode|otp|totp|2fa|mfa|api[ _-]?key|client[ _-]?secret|access[ _-]?token|refresh[ _-]?token|session[ _-]?token|backup[ _-]?code|recovery[ _-]?code)\b\s*[:=]\s*([^\s,;]{3,})/gi;
const BEARER = /\bbearer\s+[A-Za-z0-9._~+/=-]{8,}/gi;
const JWT = /\beyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\b/g;
const API_KEY = /\b(?:sk-[A-Za-z0-9_-]{16,}|gh[pousr]_[A-Za-z0-9]{20,}|AIza[A-Za-z0-9_-]{20,}|AKIA[0-9A-Z]{16})\b/gi;
const PRIVATE_KEY = /-----BEGIN [A-Z0-9 ]*PRIVATE KEY-----/gi;
const CARD = /(?<!\d)(?:\d[ -]?){13,19}(?!\d)/g;
const URL_PATTERN = /https?:\/\/[^\s<>"']+/gi;

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
    catch { return base.fetch(request, privacyHardenedEnv(env), ctx); }

    if (url.pathname === '/v1/transcribe') {
      return transcribe.fetch(request, env, ctx);
    }

    const privateEnv = privacyHardenedEnv(env);

    let screenBody = null;
    let screenRequest = request;
    if (request.method === 'POST' && SCREEN_ROUTES.has(url.pathname)) {
      try {
        const raw = await request.clone().json();
        screenBody = sanitizeScreenBody(raw);
        screenRequest = rebuildJsonRequest(request, screenBody);
      }
      catch {
        screenBody = null;
        screenRequest = request;
      }
    }

    if (url.pathname === '/v1/quality-guide') {
      return quality.fetch(screenRequest, privateEnv, ctx);
    }

    if (url.pathname === '/v1/education/assist') {
      return education.fetch(request, privateEnv, ctx);
    }

    const response = await base.fetch(screenRequest, privateEnv, ctx);
    if (!screenBody || response.status !== 200) return response;

    let decision;
    try {
      decision = await response.clone().json();
    } catch {
      return response;
    }

    const secretOverride = guardSecretClarification(decision);
    if (secretOverride) return replaceJson(response, secretOverride);

    const override = isStructuredGuide(screenRequest) ? preventBackgroundDone(screenBody, decision) : null;
    return override ? replaceJson(response, override) : response;
  }
};

export function sanitizeScreenBody(raw) {
  if (!raw || typeof raw !== 'object' || Array.isArray(raw)) return raw;
  const body = { ...raw };

  sanitizeAliasedText(body, 'request', 'Request', 1200);
  sanitizeAliasedText(body, 'routeIssue', 'RouteIssue', 300);

  if (Array.isArray(raw.elements)) {
    body.elements = raw.elements.map(element => {
      if (!element || typeof element !== 'object' || Array.isArray(element)) return element;
      const { value, Value, ...safe } = element;
      const password = safe.password === true || safe.Password === true;
      const hadValue = typeof value === 'string' ? value.length > 0 : typeof Value === 'string' ? Value.length > 0 : false;
      if (safe.inputPresent === undefined && safe.InputPresent === undefined)
        safe.inputPresent = !password && hadValue;
      sanitizeAliasedText(safe, 'name', 'Name', 220);
      sanitizeAliasedText(safe, 'automationId', 'AutomationId', 160);
      sanitizeAliasedText(safe, 'className', 'ClassName', 160);
      return safe;
    });
  }

  const context = raw.systemContext ?? raw.SystemContext;
  if (context && typeof context === 'object' && !Array.isArray(context)) {
    const contextCopy = { ...context };
    sanitizeAliasedText(contextCopy, 'foregroundTitle', 'ForegroundTitle', 320);
    const browserKey = contextCopy.browser !== undefined ? 'browser' : contextCopy.Browser !== undefined ? 'Browser' : null;
    if (browserKey) {
      const browser = contextCopy[browserKey];
      if (browser && typeof browser === 'object' && !Array.isArray(browser)) {
        const { url, Url, ...safeBrowser } = browser;
        sanitizeAliasedText(safeBrowser, 'windowTitle', 'WindowTitle', 320);
        sanitizeAliasedText(safeBrowser, 'domain', 'Domain', 220);
        contextCopy[browserKey] = safeBrowser;
      }
    }
    if (raw.systemContext !== undefined) body.systemContext = contextCopy;
    else body.SystemContext = contextCopy;
  }

  const evidence = raw.evidence ?? raw.Evidence;
  if (evidence && typeof evidence === 'object' && !Array.isArray(evidence)) {
    const { browserUrl, BrowserUrl, ...safeEvidence } = evidence;
    sanitizeAliasedText(safeEvidence, 'foregroundTitle', 'ForegroundTitle', 320);
    sanitizeAliasedText(safeEvidence, 'browserDomain', 'BrowserDomain', 220);
    sanitizeStringArray(safeEvidence, 'focusedElements', 'FocusedElements', 220);
    sanitizeStringArray(safeEvidence, 'recentTargets', 'RecentTargets', 220);
    if (raw.evidence !== undefined) body.evidence = safeEvidence;
    else body.Evidence = safeEvidence;
  }

  if (Array.isArray(raw.history)) {
    body.history = raw.history.map(item => {
      if (!item || typeof item !== 'object' || Array.isArray(item)) return item;
      const safe = { ...item };
      sanitizeAliasedText(safe, 'targetName', 'TargetName', 220);
      sanitizeAliasedText(safe, 'instruction', 'Instruction', 360);
      return safe;
    });
  }

  return body;
}

export function sanitizeOutboundText(value, max = 1200) {
  if (typeof value !== 'string') return value;
  let text = value.replace(/[\r\n]+/g, ' ').trim();
  text = text.replace(URL_PATTERN, raw => stripUrlSecrets(raw));
  text = text.replace(EMAIL, '<email>');
  text = text.replace(JP_PHONE, '<phone>');
  text = text.replace(JP_POSTAL, '<postal-code>');
  text = text.replace(LABELED_SECRET, '$1=<redacted-secret>');
  text = text.replace(BEARER, 'Bearer <redacted-secret>');
  text = text.replace(JWT, '<redacted-jwt>');
  text = text.replace(API_KEY, '<redacted-api-key>');
  text = text.replace(PRIVATE_KEY, '<redacted-private-key>');
  text = text.replace(CARD, raw => {
    const digits = raw.replace(/\D/g, '');
    return digits.length >= 13 && digits.length <= 19 && passesLuhn(digits) ? '<redacted-card>' : raw;
  });
  return text.length <= max ? text : text.slice(0, max);
}

function sanitizeAliasedText(object, camel, pascal, max) {
  if (typeof object?.[camel] === 'string') object[camel] = sanitizeOutboundText(object[camel], max);
  if (typeof object?.[pascal] === 'string') object[pascal] = sanitizeOutboundText(object[pascal], max);
}

function sanitizeStringArray(object, camel, pascal, max) {
  const key = Array.isArray(object?.[camel]) ? camel : Array.isArray(object?.[pascal]) ? pascal : null;
  if (!key) return;
  object[key] = object[key].map(value => typeof value === 'string' ? sanitizeOutboundText(value, max) : value);
}

function stripUrlSecrets(raw) {
  try {
    const url = new URL(raw);
    return url.origin;
  } catch {
    return '<url>';
  }
}

function passesLuhn(digits) {
  let sum = 0;
  let alternate = false;
  for (let i = digits.length - 1; i >= 0; i--) {
    let n = digits.charCodeAt(i) - 48;
    if (alternate) {
      n *= 2;
      if (n > 9) n -= 9;
    }
    sum += n;
    alternate = !alternate;
  }
  return sum % 10 === 0;
}

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

function privacyHardenedEnv(env) {
  if (!env?.AI || typeof env.AI.run !== 'function') return env;
  const ai = env.AI;
  const hardenedAi = new Proxy(ai, {
    get(target, property, receiver) {
      if (property !== 'run') return Reflect.get(target, property, receiver);
      return (model, options = {}) => target.run(model, { ...options, store: false });
    }
  });
  return new Proxy(env, {
    get(target, property, receiver) {
      if (property === 'AI') return hardenedAi;
      return Reflect.get(target, property, receiver);
    }
  });
}

function rebuildJsonRequest(request, body) {
  const headers = new Headers(request.headers);
  headers.set('content-type', 'application/json; charset=utf-8');
  headers.delete('content-length');
  return new Request(request.url, {
    method: request.method,
    headers,
    body: JSON.stringify(body),
    redirect: request.redirect
  });
}

function usable(raw) {
  return (Array.isArray(raw) ? raw : []).filter(x => x && x.interactable !== false && x.enabled !== false && String(x.id || '').trim());
}

function replaceJson(response, value) {
  const headers = new Headers(response.headers);
  headers.set('content-type', 'application/json; charset=utf-8');
  return new Response(JSON.stringify(value), { status: 200, headers });
}
