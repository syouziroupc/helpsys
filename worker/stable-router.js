import planner from './stable-gemini.js';
import education from './education-gemini.js';
import outlaw from './outlaw-guide.js';

export default {
  async fetch(request, env, ctx) {
    const url = new URL(request.url);

    if (url.pathname === '/v1/plan' || url.pathname === '/health')
      return planner.fetch(request, env, ctx);

    if (url.pathname === '/v1/education/assist' || url.pathname === '/health/education')
      return education.fetch(request, env, ctx);

    if (url.pathname === '/v1/outlaw-plan')
      return outlaw.fetch(request, env, ctx);

    if (['/v1/guide', '/v1/quality-guide', '/v1/vision-guide'].includes(url.pathname)) {
      return new Response(JSON.stringify({
        error: 'upgrade_required',
        message: 'HelpSys Stable 3.0 へ更新してください。'
      }), {
        status: 426,
        headers: { 'content-type': 'application/json; charset=utf-8', 'cache-control': 'no-store' }
      });
    }

    return new Response(JSON.stringify({ error: 'not_found' }), {
      status: 404,
      headers: { 'content-type': 'application/json; charset=utf-8', 'cache-control': 'no-store' }
    });
  }
};
