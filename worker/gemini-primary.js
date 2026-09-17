import base, { sanitizeScreenBody } from './reliability-v4-guard.js';
import geminiGuide from './gemini-guide.js';

const GEMINI_ROUTES = new Set(['/v1/guide', '/v1/quality-guide']);

export default {
  async fetch(request, env, ctx) {
    let url;
    try { url = new URL(request.url); }
    catch { return base.fetch(request, env, ctx); }

    if (request.method === 'GET' && url.pathname === '/health') {
      const response = await base.fetch(request, env, ctx);
      if (response.status !== 200) return response;
      try {
        const value = await response.clone().json();
        return json({
          ...value,
          primaryPlanner: configured(env) ? (env.HELPSYS_GEMINI_MODEL || 'gemini-3.8-flash') : 'cloudflare-glm-fallback',
          geminiConfigured: configured(env)
        });
      } catch {
        return response;
      }
    }

    if (request.method !== 'POST' || !GEMINI_ROUTES.has(url.pathname) || !configured(env))
      return base.fetch(request, env, ctx);

    let body;
    try { body = sanitizeScreenBody(await request.clone().json()); }
    catch { return base.fetch(request, env, ctx); }

    const safeRequest = rebuildJsonRequest(request, body);
    const geminiResponse = await geminiGuide.fetch(safeRequest.clone(), env, ctx);

    // Semantic uncertainty is a valid Gemini result and must not silently fall back to GLM.
    // GLM is retained only as a provider/transport fallback so one model's outage does not stop HelpSys.
    if (geminiResponse.status < 500) return geminiResponse;

    console.error('gemini_primary_unavailable_using_glm_fallback');
    return base.fetch(safeRequest, env, ctx);
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
