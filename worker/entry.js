import core from './index.js';

const PROFILE_SCREEN = /(どなたが使用|プロファイル.*選|プロフィール.*選|アカウント.*選|ゲストモード)/i;
const IDENTITY_SENSITIVE_GOAL = /(メール|gmail|outlook|drive|ドライブ|カレンダー|ログイン|サインイン|アカウント|購入|注文|支払|決済|保存済み|履歴|ブックマーク)/i;

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

    const override = beginnerBranchOverride(body);
    if (!override) return response;

    const headers = new Headers(response.headers);
    headers.set('content-type', 'application/json; charset=utf-8');
    return new Response(JSON.stringify(override), { status: 200, headers });
  }
};

function beginnerBranchOverride(body) {
  const goal = String(body?.request || '').trim();
  const elements = Array.isArray(body?.elements) ? body.elements : [];
  const history = Array.isArray(body?.history) ? body.history : [];
  const foreground = String(body?.systemContext?.ForegroundProcess ?? body?.systemContext?.foregroundProcess ?? '').toLowerCase();
  const relevant = elements.filter(x => !foreground || String(x?.processName || '').toLowerCase() === foreground);
  const visibleText = relevant.map(x => String(x?.name || '')).join(' ');
  if (!PROFILE_SCREEN.test(visibleText)) return null;

  // If the user has already answered a choice question, respect the answer instead of
  // asking the same question forever. Only select a visible, interactable exact/near match.
  const latestAnswer = [...history].reverse().find(x => String(x?.action || '').toLowerCase() === 'clarification_answer');
  if (latestAnswer) {
    const answer = String(latestAnswer?.target || latestAnswer?.Target || '').trim();
    if (answer) {
      const matching = relevant
        .filter(x => x?.interactable !== false && x?.enabled !== false)
        .find(x => namesMatch(String(x?.name || ''), answer));
      if (matching) {
        return {
          status: 'target', targetId: String(matching.id), action: 'left_click',
          instruction: `「${safeLabel(matching.name)}」と書かれたところで、マウスの左ボタンを1回押してください。`,
          question: null, key: null, confidence: 0.99
        };
      }
    }
  }

  // Identity-sensitive work must not silently choose another person's browser profile.
  if (IDENTITY_SENSITIVE_GOAL.test(goal)) return null;

  const guest = relevant.find(x => x?.interactable !== false && x?.enabled !== false && /ゲストモード|guest mode|ゲスト/i.test(String(x?.name || '')));
  if (guest) {
    return {
      status: 'target', targetId: String(guest.id), action: 'left_click',
      instruction: '使う人を選ぶ画面です。どの名前を使うかわからない場合は、「ゲストモード」と書かれたところで、マウスの左ボタンを1回押してください。',
      question: null, key: null, confidence: 0.99
    };
  }

  const profiles = relevant.filter(x => x?.interactable !== false && x?.enabled !== false &&
    !/(追加|add|設定|メニュー|その他|閉じる)/i.test(String(x?.name || '')) && String(x?.name || '').trim().length > 0);
  if (profiles.length === 1) {
    return {
      status: 'target', targetId: String(profiles[0].id), action: 'left_click',
      instruction: `使う人として「${safeLabel(profiles[0].name)}」だけが表示されています。その名前のところで、マウスの左ボタンを1回押してください。`,
      question: null, key: null, confidence: 0.96
    };
  }

  return null;
}

function namesMatch(name, answer) {
  const a = normalize(name);
  const b = normalize(answer);
  if (!a || !b) return false;
  return a === b || a.includes(b) || b.includes(a);
}

function normalize(value) {
  return String(value || '').toLowerCase().replace(/[\s　「」『』"']/g, '');
}

function safeLabel(value) {
  const text = String(value || '').trim().replace(/[\r\n]+/g, ' ');
  return text.length > 50 ? text.slice(0, 50) : text;
}
