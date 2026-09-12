import entry from './entry.js';

const BROWSER_GOAL = /(youtube|ユーチューブ|ウェブ|web|サイト|インターネット|楽天|yahoo|amazon|アマゾン|google\s*マップ|グーグルマップ)/i;
const BROWSER_LABEL = /(google\s*chrome|chrome|クローム|microsoft\s*edge|edge|エッジ|firefox|ファイアフォックス)/i;
const BROWSER_PROCESS = /^(chrome|msedge|firefox|brave|opera|vivaldi)$/i;
const START_PROCESS = /(searchhost|startmenuexperiencehost)/i;

const APPS = [
  { goal: /(excel|エクセル)/i, label: /(microsoft\s*excel|excel|エクセル)/i, search: 'Excel' },
  { goal: /(word|ワード)/i, label: /(microsoft\s*word|word|ワード)/i, search: 'Word' },
  { goal: /(powerpoint|パワーポイント|パワポ)/i, label: /(microsoft\s*powerpoint|powerpoint|パワーポイント)/i, search: 'PowerPoint' },
  { goal: /(chrome|クローム|グーグルクローム)/i, label: /(google\s*chrome|chrome|クローム)/i, search: 'Google Chrome' },
  { goal: /(edge|エッジ)/i, label: /(microsoft\s*edge|edge|エッジ)/i, search: 'Microsoft Edge' },
  { goal: /(メモ帳|notepad)/i, label: /(メモ帳|notepad)/i, search: 'メモ帳' },
  { goal: /(電卓|calculator)/i, label: /(電卓|calculator)/i, search: '電卓' },
  { goal: /設定/i, label: /^(設定|settings)$/i, search: '設定' }
];

export default {
  async fetch(request, env, ctx) {
    let bodyPromise = null;
    try {
      const url = new URL(request.url);
      if (request.method === 'POST' && url.pathname === '/v1/guide') bodyPromise = request.clone().json();
    } catch { }

    const response = await entry.fetch(request, env, ctx);
    if (!bodyPromise || response.status !== 200) return response;

    let body;
    let decision;
    try {
      body = await bodyPromise;
      decision = await response.clone().json();
    } catch {
      return response;
    }

    const override = enforceVisibleFirst(body, decision);
    return override ? replaceJson(response, override) : response;
  }
};

function enforceVisibleFirst(body, decision) {
  const goal = String(body?.request || '').trim();
  const elements = usable(body?.elements);
  const foreground = String(body?.systemContext?.ForegroundProcess ?? body?.systemContext?.foregroundProcess ?? '').toLowerCase();
  const startOpen = isStartOpen(elements, foreground);
  if (!goal || !decision) return null;

  if (BROWSER_GOAL.test(goal)) {
    if (!BROWSER_PROCESS.test(foreground)) {
      const direct = bestLauncher(elements.filter(x => BROWSER_LABEL.test(String(x.name || '')) && reachableLauncher(x, foreground, startOpen)));
      if (direct && String(decision.targetId || '') !== String(direct.id)) {
        return target(direct, launcherAction(direct), launcherInstruction(direct), 0.995);
      }
    } else {
      const pageSearch = findWebSearch(elements, foreground);
      const proposedTarget = elements.find(x => String(x.id) === String(decision.targetId || ''));
      const proposedUsesAddress = proposedTarget && elementRole(proposedTarget) === 'browser_address';
      if (pageSearch && ((decision.action === 'press_key' && /^ctrl\+l$/i.test(String(decision.key || ''))) || proposedUsesAddress)) {
        return target(pageSearch, pageSearch.focused ? 'type_text' : 'left_click',
          pageSearch.focused
            ? '画面の中の検索欄に検索したい名前を入力し、最後に「Enter」と書かれたキーを1回押してください。'
            : 'ページの中にある検索欄で、マウスの左ボタンを1回押してください。',
          0.995, pageSearch.focused ? 'Enter' : null);
      }

      if (decision.action === 'press_key' && /^ctrl\+l$/i.test(String(decision.key || ''))) {
        const address = findAddressField(elements, foreground);
        if (address) return target(address, 'left_click', '画面上部の、検索や文字を入力できる欄で、マウスの左ボタンを1回押してください。', 0.99);
      }
      if (decision.action === 'press_key' && /^ctrl\+t$/i.test(String(decision.key || ''))) {
        const newTab = findNewTab(elements, foreground);
        if (newTab) return target(newTab, 'left_click', '今のページはそのまま残します。画面上部の「新しいタブ」を開く場所で、マウスの左ボタンを1回押してください。', 0.995);
      }
    }

    if (decision.action === 'press_key' && /windows/i.test(String(decision.key || ''))) {
      const search = findWindowsSearch(elements);
      if (search) return searchDecision(search, 'Microsoft Edge');
      if (startOpen) return notFound('スタート画面はすでに開いています。Windowsキーはもう押さず、今見えている画面から次を探します。');
      const start = findStart(elements);
      if (start) return target(start, 'left_click', 'インターネットを見るアプリが今の画面に見当たらないため、画面下のWindowsマークを1回押して、アプリを探す画面を開いてください。', 0.97);
    }
  }

  const app = APPS.find(x => x.goal.test(goal));
  if (app && !BROWSER_GOAL.test(goal)) {
    const direct = bestLauncher(elements.filter(x => app.label.test(String(x.name || '')) && reachableLauncher(x, foreground, startOpen)));
    if (direct && String(decision.targetId || '') !== String(direct.id)) {
      return target(direct, launcherAction(direct), appInstruction(direct), 0.995);
    }
    if (decision.action === 'press_key' && /windows/i.test(String(decision.key || ''))) {
      const search = findWindowsSearch(elements);
      if (search) return searchDecision(search, app.search);
      if (startOpen) return notFound('スタート画面はすでに開いています。Windowsキーはもう押さず、今の画面から目的のアプリを探します。');
      const start = findStart(elements);
      if (start) return target(start, 'left_click', '目的のアプリが今の画面に見当たらないため、画面下のWindowsマークを1回押して、アプリを探す画面を開いてください。', 0.97);
    }
  }

  return null;
}

function usable(raw) {
  return (Array.isArray(raw) ? raw : []).filter(x => x && x.interactable !== false && x.enabled !== false && String(x.id || '').trim());
}

function isStartOpen(elements, foreground) {
  if (START_PROCESS.test(foreground)) return true;
  return elements.some(x => START_PROCESS.test(String(x.processName || '')) &&
    (x.focused === true || /(検索|search|ピン留め|pinned|おすすめ|すべて)/i.test(String(x.name || ''))));
}

function reachableLauncher(x, foreground, startOpen) {
  const process = String(x.processName || '').toLowerCase();
  const type = String(x.controlType || '');
  if (START_PROCESS.test(process)) return startOpen;
  if (process !== 'explorer') return false;
  if (/^(button|menuitem)$/i.test(type)) return true;
  return /^(listitem)$/i.test(type) && (!foreground || foreground === 'explorer');
}

function bestLauncher(items) {
  if (!items.length) return null;
  return [...items].sort((a, b) => scoreLauncher(b) - scoreLauncher(a))[0];
}

function scoreLauncher(x) {
  let score = 0;
  if (START_PROCESS.test(String(x.processName || ''))) score += 100;
  if (/^button$/i.test(String(x.controlType || ''))) score += 70;
  if (/^listitem$/i.test(String(x.controlType || ''))) score += 45;
  if (/google\s*chrome|chrome|クローム/i.test(String(x.name || ''))) score += 10;
  return score;
}

function launcherAction(x) {
  return String(x.processName || '').toLowerCase() === 'explorer' && String(x.controlType || '').toLowerCase() === 'listitem'
    ? 'double_click' : 'left_click';
}

function launcherInstruction(x) {
  const name = clean(x.name) || 'インターネットを見るアプリ';
  return launcherAction(x) === 'double_click'
    ? `青い枠の「${name}」で、マウスの左ボタンを間をあけずに2回押してください。`
    : `青い枠の「${name}」で、マウスの左ボタンを1回押してください。`;
}

function appInstruction(x) {
  const name = clean(x.name) || '目的のアプリ';
  return launcherAction(x) === 'double_click'
    ? `青い枠の「${name}」で、マウスの左ボタンを間をあけずに2回押してください。`
    : `青い枠の「${name}」で、マウスの左ボタンを1回押してください。`;
}

function elementRole(element) {
  const match = String(element?.automationId || '').match(/(?:^|\|)role:([a-z_]+)/i);
  return match ? match[1].toLowerCase() : '';
}

function findWindowsSearch(elements) {
  return elements.find(x => String(x.controlType || '').toLowerCase() === 'edit' && elementRole(x) === 'windows_search') ||
    elements.find(x => String(x.controlType || '').toLowerCase() === 'edit' &&
      /(検索|search)/i.test(`${x.name || ''} ${x.automationId || ''}`) &&
      /searchhost|startmenuexperiencehost|explorer/i.test(String(x.processName || ''))) || null;
}

function findWebSearch(elements, foreground) {
  return elements.find(x => String(x.processName || '').toLowerCase() === foreground &&
    String(x.controlType || '').toLowerCase() === 'edit' && elementRole(x) === 'web_search') || null;
}

function findAddressField(elements, foreground) {
  return elements.find(x => String(x.processName || '').toLowerCase() === foreground &&
    String(x.controlType || '').toLowerCase() === 'edit' && elementRole(x) === 'browser_address') ||
    elements.find(x => String(x.processName || '').toLowerCase() === foreground &&
      String(x.controlType || '').toLowerCase() === 'edit' &&
      /(アドレス|address|location|omnibox|urlbar|url bar|web address)/i.test(`${x.name || ''} ${x.automationId || ''} ${x.className || ''}`)) || null;
}

function findNewTab(elements, foreground) {
  return elements.find(x => String(x.processName || '').toLowerCase() === foreground &&
    /^(button|tabitem)$/i.test(String(x.controlType || '')) &&
    /(新しいタブ|new tab|タブを追加|add tab)/i.test(String(x.name || ''))) || null;
}

function findStart(elements) {
  return elements.find(x => String(x.processName || '').toLowerCase() === 'explorer' &&
    /^(button|menuitem)$/i.test(String(x.controlType || '')) && /^(スタート|start)$/i.test(String(x.name || '').trim())) || null;
}

function searchDecision(field, text) {
  if (field.focused === true) {
    return target(field, 'type_text', `キーボードで「${text}」と入力し、最後に「Enter」と書かれたキーを1回押してください。`, 0.995, 'Enter');
  }
  return target(field, 'left_click', '開いている検索画面の、文字を入力できる欄で、マウスの左ボタンを1回押してください。', 0.995);
}

function target(x, action, instruction, confidence, key = null) {
  return { status: 'target', targetId: String(x.id), action, instruction, question: null, key, confidence };
}

function notFound(instruction) {
  return { status: 'not_found', targetId: null, action: 'none', instruction, question: null, key: null, confidence: 0.95 };
}

function replaceJson(response, value) {
  const headers = new Headers(response.headers);
  headers.set('content-type', 'application/json; charset=utf-8');
  return new Response(JSON.stringify(value), { status: 200, headers });
}

function clean(value) {
  const text = String(value || '').trim().replace(/[\r\n]+/g, ' ');
  return text.length > 55 ? text.slice(0, 55) : text;
}