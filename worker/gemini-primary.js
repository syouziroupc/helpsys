import base, { sanitizeScreenBody } from './reliability-v4-guard.js';
import geminiGuide from './gemini-guide.js';
import geminiVision from './gemini-vision.js';

const GEMINI_ROUTES = new Set(['/v1/guide', '/v1/quality-guide', '/v1/vision-guide']);

export default {
  async fetch(request, env, ctx) {
    let url;
    try { url = new URL(request.url); }
    catch { return base.fetch(request, env, ctx); }

    if (request.method === 'GET' && url.pathname === '/health') {
      return json({
        ok: true,
        service: 'helpsys',
        planner: env.HELPSYS_GEMINI_MODEL || 'gemini-3.8-flash',
        plannerProvider: 'gemini',
        geminiConfigured: configured(env)
      });
    }

    if (request.method !== 'POST' || !GEMINI_ROUTES.has(url.pathname))
      return base.fetch(request, env, ctx);

    if (!configured(env))
      return json({ error: 'gemini_unconfigured' }, 503);

    let body;
    try { body = sanitizeScreenBody(await request.clone().json()); }
    catch { return json({ error: 'invalid_json' }, 400); }

    const safeRequest = rebuildJsonRequest(request, body);
    if (url.pathname === '/v1/vision-guide')
      return geminiVision.fetch(safeRequest, env, ctx);

    // Guidance model failures stay failures. Never route a Gemini failure or low-confidence result
    // into the old GLM planner, because that silently reintroduces the weaker behavior we removed.
    return geminiGuide.fetch(safeRequest, env, ctx);
  }
};

function configured(env) {
  return typeof env?.GEMINI_API_KEY === 'string' && env.GEMINI_API_KEY.trim().length > 0;
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

function json(value, status = 200) {
  return new Response(JSON.stringify(value), {
    status,
    headers: { 'content-type': 'application/json; charset=utf-8' }
  });
}
