import visibleFirst from './visible-first.js';

const START_PROCESS = /(searchhost|startmenuexperiencehost)/i;

export default {
  async fetch(request, env, ctx) {
    let bodyPromise = null;
    try {
      const url = new URL(request.url);
      if (request.method === 'POST' && url.pathname === '/v1/guide') bodyPromise = request.clone().json();
    } catch { }

    const response = await visibleFirst.fetch(request, env, ctx);
    if (!bodyPromise || response.status !== 200) return response;

    let body;
    let decision;
    try {
      body = await bodyPromise;
      decision = await response.clone().json();
    } catch {
      return response;
    }

    if (!(decision?.action === 'press_key' && /windows/i.test(String(decision?.key || '')))) return response;

    const foreground = String(body?.systemContext?.ForegroundProcess ?? body?.systemContext?.foregroundProcess ?? '');
    const elements = Array.isArray(body?.elements) ? body.elements : [];
    const startVisible = START_PROCESS.test(foreground) || elements.some(x =>
      START_PROCESS.test(String(x?.processName || '')) && x?.enabled !== false &&
      (x?.focused === true || /検索|search|ピン留め|pinned|おすすめ|すべて|スタート/i.test(String(x?.name || '')))
    );

    if (!startVisible) return response;

    const headers = new Headers(response.headers);
    headers.set('content-type', 'application/json; charset=utf-8');
    return new Response(JSON.stringify({
      status: 'not_found', targetId: null, action: 'none',
      instruction: 'スタート画面はすでに開いています。Windowsキーはもう押さず、今見えている画面から次を探します。',
      question: null, key: null, confidence: 0.99
    }), { status: 200, headers });
  }
};
