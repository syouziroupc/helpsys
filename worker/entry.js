import core from './index.js';

const PROFILE_SCREEN = /(どなたが使用|プロファイル.*選|プロフィール.*選|アカウント.*選|ゲストモード)/i;
const IDENTITY_SENSITIVE_GOAL = /(メール|gmail|outlook|drive|ドライブ|カレンダー|ログイン|サインイン|アカウント|購入|注文|支払|決済|保存済み|履歴|ブックマーク)/i;
const BROWSER_GOAL = /(youtube|ユーチューブ|ウェブ|web|サイト|インターネット|楽天|yahoo|amazon|アマゾン|google\s*マップ|グーグルマップ)/i;
const BROWSER_LABEL = /(google\s*chrome|chrome|クローム|microsoft\s*edge|edge|エッジ|firefox|ファイアフォックス)/i;
const START_PROCESS = /(searchhost|startmenuexperiencehost)/i;
const SHELL_PROCESS = /^(explorer|searchhost|startmenuexperiencehost)$/i;

const APP_LABELS = [
  { goal: /(excel|エクセル)/i, label: /(microsoft\s*excel|excel|エクセル)/i },
  { goal: /(word|ワード)/i, label: /(microsoft\s*word|word|ワード)/i },
  { goal: /(powerpoint|パワーポイント|パワポ)/i, label: /(microsoft\s*powerpoint|powerpoint|パワーポイント)/i },
  { goal: /(chrome|クローム|グーグルクローム)/i, label: /(google\s*chrome|chrome|クローム)/i },
  { goal: /(edge|エッジ)/i, label: /(microsoft\s*edge|edge|エッジ)/i },
  { goal: /(メモ帳|notepad)/i, label: /(メモ帳|notepad)/i },
  { goal: /(電卓|calculator)/i, label: /(電卓|calculator)/i },
  { goal: /(設定)/i, label: /^(設定|settings)$/i }
];

export default {
  async fetch(request, env, ctx) {
    let bodyPromise = null;
    try {
      const url = new URL(request.url);
      if (request.method === 'POST' && url.pathname === '/v1/guide') bodyPromise = request.clone().json();
    } catch { }

    const response = await core.fetch(request, env, ctx);
    if (!bodyPromise || response.status !== 200) return response;

    let body;
    try { body = await bodyPromise; }
    catch { return response; }

    const branchOverride = beginnerBranchOverride(body);
    if (branchOverride) return replaceJson(response, branchOverride);

    let coreDecision = null;
    try { coreDecision = await response.clone().json(); }
    catch { }

    const visibleOverride = visibleFirstOverride(body, coreDecision);
    if (visibleOverride) return replaceJson(response, visibleOverride);
    return response;
  }
};

function visibleFirstOverride(body, decision) {
  const goal = String(body?.request || '').trim();
  const elements = usableElements(body?.elements);
  const foreground = getForeground(body);
  if (!goal || !decision || typeof decision !== 'object') return null;

  const startOpen = isStartSurfaceOpen(elements, foreground);

  // Website tasks: if a browser or browser control is already visibly usable, stay on the
  // screen. Keyboard shortcuts and Start are fallbacks, not the default route.
  if (BROWSER_GOAL.test(goal)) {
    if (isBrowserProcess(foreground)) {
      const pageSearch = findWebSearchField(elements, foreground);
      const proposedTarget = elements.find(x => String(x.id) === String(decision.targetId || ''));
      const proposedUsesAddress = proposedTarget && elementRole(proposedTarget) === 'browser_address';
      if (pageSearch && ((decision.action === 'press_key' && /^(ctrl\+l)$/i.test(String(decision.key || ''))) || proposedUsesAddress)) {
        return target(pageSearch, pageSearch.focused ? 'type_text' : 'left_click',
          pageSearch.focused
            ? '画面の中の検索欄に検索したい名前を入力し、最後に「Enter」と書かれたキーを1回押してください。'
            : 'ページの中にある検索欄で、マウスの左ボタンを1回押してください。',
          0.995, pageSearch.focused ? 'Enter' : null);
      }

      if (decision.action === 'press_key' && /^(ctrl\+l)$/i.test(String(decision.key || ''))) {
        const address = findBrowserAddressField(elements, foreground);
        if (address) return target(address, 'left_click', '画面上部の、検索や文字を入力できる欄で、マウスの左ボタンを1回押してください。', 0.99);
      }

      if (decision.action === 'press_key' && /^(ctrl\+t)$/i.test(String(decision.key || ''))) {
        const newTab = findNewTabButton(elements, foreground);
        if (newTab) return target(newTab, 'left_click', '今のページはそのまま残します。画面上部の「新しいタブ」を開く場所で、マウスの左ボタンを1回押してください。', 0.99);
      }
    }

    // Direct browser launch always beats opening Start. This restores the useful behavior
    // of the first prototype: point at Chrome/Edge when it is actually on the current screen.
    if (decision.action === 'press_key' && /windows/i.test(String(decision.key || ''))) {
      const browserTarget = findVisibleBrowserLauncher(elements, foreground, startOpen);
      if (browserTarget) return target(browserTarget, launcherAction(browserTarget), browserInstruction(browserTarget), 0.99);

      const search = findWindowsSearchField(elements);
      if (search) {
        if (search.focused) return target(search, 'type_text', 'キーボードで「Microsoft Edge」と入力し、最後に「Enter」と書かれたキーを1回押してください。', 0.99, 'Enter');
        return target(search, 'left_click', '開いている検索画面の、文字を入力できる欄で、マウスの左ボタンを1回押してください。', 0.99);
      }

      // Pressing Windows while Start is already open usually closes it. Never do that.
      if (startOpen) return notFound('スタート画面はすでに開いています。今見えている項目から次を探します。');

      const start = findStartButton(elements, foreground);
      if (start) return target(start, 'left_click', 'インターネットを見るアプリが今の画面に見当たらないため、画面下のWindowsマークを1回押して、アプリを探す画面を開いてください。', 0.97);
    }
  }

  // App-launch tasks receive the same visible-first rule. Do not open Start when the app
  // itself is already visible on the desktop, taskbar or open Start surface.
  if (decision.action === 'press_key' && /windows/i.test(String(decision.key || ''))) {
    const appRule = APP_LABELS.find(rule => rule.goal.test(goal));
    if (appRule) {
      const appTarget = findVisibleAppLauncher(elements, foreground, startOpen, appRule.label);
      if (appTarget) return target(appTarget, launcherAction(appTarget), `青い枠の「${safeLabel(appTarget.name)}」で、マウスの左ボタンを${launcherAction(appTarget) === 'double_click' ? '間をあけずに2回' : '1回'}押してください。`, 0.99);

      const search = findWindowsSearchField(elements);
      if (search) {
        if (search.focused) return target(search, 'type_text', `キーボードで「${goalAppSearchText(goal)}」と入力し、最後に「Enter」と書かれたキーを1回押してください。`, 0.99, 'Enter');
        return target(search, 'left_click', '開いている検索画面の、文字を入力できる欄で、マウスの左ボタンを1回押してください。', 0.99);
      }
      if (startOpen) return notFound('スタート画面はすでに開いています。Windowsキーはもう押さず、今の画面から目的のアプリを探します。');

      const start = findStartButton(elements, foreground);
      if (start) return target(start, 'left_click', '目的のアプリが今の画面に見当たらないため、画面下のWindowsマークを1回押して、アプリを探す画面を開いてください。', 0.97);
    }
  }

  return null;
}

function beginnerBranchOverride(body) {
  const goal = String(body?.request || '').trim();
  const elements = Array.isArray(body?.elements) ? body.elements : [];
  const history = Array.isArray(body?.history) ? body.history : [];
  const foreground = getForeground(body);
  const relevant = elements.filter(x => !foreground || String(x?.processName || '').toLowerCase() === foreground);
  const visibleText = relevant.map(x => String(x?.name || '')).join(' ');
  if (!PROFILE_SCREEN.test(visibleText)) return null;

  const latestAnswer = [...history].reverse().find(x => String(x?.action ?? x?.Action ?? '').toLowerCase() === 'clarification_answer');
  if (latestAnswer) {
    const answer = String(
      latestAnswer?.targetName ?? latestAnswer?.TargetName ??
      latestAnswer?.target ?? latestAnswer?.Target ?? ''
    ).trim();
    if (answer) {
      const matching = resolveAnsweredTarget(relevant, answer);
      if (matching) {
        return target(matching, 'left_click', `「${safeLabel(matching.name)}」と書かれたところで、マウスの左ボタンを1回押してください。`, 0.99);
      }
    }
  }

  if (IDENTITY_SENSITIVE_GOAL.test(goal)) return null;

  const guest = relevant.find(x => x?.interactable !== false && x?.enabled !== false && /ゲストモード|guest mode|ゲスト/i.test(String(x?.name || '')));
  if (guest) {
    return target(guest, 'left_click', 'ご自分の名前が画面にあれば、その名前のところでマウスの左ボタンを1回押してください。どれを使うかわからない場合は、青い枠の「ゲストモード」を1回押してください。', 0.99);
  }

  const profiles = relevant.filter(x => x?.interactable !== false && x?.enabled !== false &&
    !/(追加|add|設定|メニュー|その他|閉じる)/i.test(String(x?.name || '')) && String(x?.name || '').trim().length > 0);
  if (profiles.length === 1) {
    return target(profiles[0], 'left_click', `使う人として「${safeLabel(profiles[0].name)}」だけが表示されています。その名前のところで、マウスの左ボタンを1回押してください。`, 0.96);
  }

  return null;
}

function usableElements(raw) {
  return (Array.isArray(raw) ? raw : []).filter(x => x && x.interactable !== false && x.enabled !== false && String(x.id || '').trim());
}

function getForeground(body) {
  return String(body?.systemContext?.ForegroundProcess ?? body?.systemContext?.foregroundProcess ?? '').toLowerCase();
}

function isBrowserProcess(process) {
  return /^(chrome|msedge|firefox|brave|opera|vivaldi)$/i.test(String(process || ''));
}

function isStartSurfaceOpen(elements, foreground) {
  if (START_PROCESS.test(foreground)) return true;
  return elements.some(x => START_PROCESS.test(String(x.processName || '')) &&
    (x.focused === true || /検索|search|すべて|pinned|ピン留め|おすすめ/i.test(String(x.name || ''))));
}

function findVisibleBrowserLauncher(elements, foreground, startOpen) {
  return bestLauncher(elements.filter(x => BROWSER_LABEL.test(String(x.name || '')) && launcherReachable(x, foreground, startOpen)));
}

function findVisibleAppLauncher(elements, foreground, startOpen, labelRegex) {
  return bestLauncher(elements.filter(x => labelRegex.test(String(x.name || '')) && launcherReachable(x, foreground, startOpen)));
}

function launcherReachable(element, foreground, startOpen) {
  const process = String(element.processName || '').toLowerCase();
  const type = String(element.controlType || '');
  if (START_PROCESS.test(process)) return startOpen;
  if (process === 'explorer') {
    if (/^(button|menuitem)$/i.test(type)) return true;
    return !foreground || foreground === 'explorer';
  }
  return false;
}

function bestLauncher(candidates) {
  if (!candidates.length) return null;
  return [...candidates].sort((a, b) => launcherScore(b) - launcherScore(a))[0];
}

function launcherScore(x) {
  let score = 0;
  const process = String(x.processName || '');
  const type = String(x.controlType || '');
  if (START_PROCESS.test(process)) score += 80;
  if (/^button$/i.test(type)) score += 60;
  if (/^listitem$/i.test(type)) score += 35;
  if (/google\s*chrome|chrome|クローム/i.test(String(x.name || ''))) score += 8;
  return score;
}

function launcherAction(element) {
  return String(element.controlType || '').toLowerCase() === 'listitem' && String(element.processName || '').toLowerCase() === 'explorer'
    ? 'double_click'
    : 'left_click';
}

function browserInstruction(element) {
  const label = safeLabel(element.name) || 'インターネットを見るアプリ';
  return launcherAction(element) === 'double_click'
    ? `青い枠の「${label}」で、マウスの左ボタンを間をあけずに2回押してください。`
    : `青い枠の「${label}」で、マウスの左ボタンを1回押してください。`;
}

function elementRole(element) {
  const match = String(element?.automationId || '').match(/(?:^|\|)role:([a-z_]+)/i);
  return match ? match[1].toLowerCase() : '';
}

function findWindowsSearchField(elements) {
  return elements.find(x => String(x.controlType || '').toLowerCase() === 'edit' && elementRole(x) === 'windows_search') ||
    elements.find(x => String(x.controlType || '').toLowerCase() === 'edit' &&
      /(検索|search)/i.test(`${x.name || ''} ${x.automationId || ''}`) &&
      /searchhost|startmenuexperiencehost|explorer/i.test(String(x.processName || ''))) || null;
}

function findWebSearchField(elements, foreground) {
  return elements.find(x => String(x.processName || '').toLowerCase() === foreground &&
    String(x.controlType || '').toLowerCase() === 'edit' && elementRole(x) === 'web_search') || null;
}

function findBrowserAddressField(elements, foreground) {
  return elements.find(x => String(x.processName || '').toLowerCase() === foreground &&
    String(x.controlType || '').toLowerCase() === 'edit' && elementRole(x) === 'browser_address') ||
    elements.find(x => String(x.processName || '').toLowerCase() === foreground &&
      String(x.controlType || '').toLowerCase() === 'edit' &&
      /(アドレス|address|location|omnibox|urlbar|url bar|web address)/i.test(`${x.name || ''} ${x.automationId || ''} ${x.className || ''}`)) || null;
}

function findNewTabButton(elements, foreground) {
  return elements.find(x => String(x.processName || '').toLowerCase() === foreground &&
    /^(button|tabitem)$/i.test(String(x.controlType || '')) &&
    /(新しいタブ|new tab|タブを追加|add tab)/i.test(String(x.name || ''))) || null;
}

function findStartButton(elements, foreground) {
  return elements.find(x => String(x.processName || '').toLowerCase() === 'explorer' &&
    /^(button|menuitem)$/i.test(String(x.controlType || '')) &&
    /^(スタート|start)$/i.test(String(x.name || '').trim())) || null;
}

function goalAppSearchText(goal) {
  const rule = APP_LABELS.find(x => x.goal.test(goal));
  if (!rule) return goal.slice(0, 36);
  if (/(excel|エクセル)/i.test(goal)) return 'Excel';
  if (/(word|ワード)/i.test(goal)) return 'Word';
  if (/(powerpoint|パワーポイント|パワポ)/i.test(goal)) return 'PowerPoint';
  if (/(chrome|クローム|グーグルクローム)/i.test(goal)) return 'Google Chrome';
  if (/(edge|エッジ)/i.test(goal)) return 'Microsoft Edge';
  if (/(メモ帳|notepad)/i.test(goal)) return 'メモ帳';
  if (/(電卓|calculator)/i.test(goal)) return '電卓';
  if (/設定/i.test(goal)) return '設定';
  return goal.slice(0, 36);
}

function target(element, action, instruction, confidence = 0.99, key = null) {
  return {
    status: 'target', targetId: String(element.id), action,
    instruction, question: null, key, confidence
  };
}

function notFound(instruction) {
  return { status: 'not_found', targetId: null, action: 'none', instruction, question: null, key: null, confidence: 0.95 };
}

function replaceJson(response, value) {
  const headers = new Headers(response.headers);
  headers.set('content-type', 'application/json; charset=utf-8');
  return new Response(JSON.stringify(value), { status: 200, headers });
}

function resolveAnsweredTarget(elements, answer) {
  const candidates = elements.filter(x => x?.interactable !== false && x?.enabled !== false && String(x?.name || '').trim());
  const normalizedAnswer = normalize(answer);
  if (!normalizedAnswer) return null;

  const exact = candidates.filter(x => normalize(x.name) === normalizedAnswer);
  if (exact.length === 1) return exact[0];
  if (exact.length > 1) return null;

  const fuzzy = candidates.filter(x => {
    const name = normalize(x.name);
    return name && (name.includes(normalizedAnswer) || normalizedAnswer.includes(name));
  });
  return fuzzy.length === 1 ? fuzzy[0] : null;
}

function normalize(value) {
  return String(value || '').toLowerCase().replace(/[\s　「」『』"']/g, '');
}

function safeLabel(value) {
  const text = String(value || '').trim().replace(/[\r\n]+/g, ' ');
  return text.length > 50 ? text.slice(0, 50) : text;
}