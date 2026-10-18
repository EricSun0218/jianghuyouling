import models from '../shared/models.json' with { type: 'json' };
import { exactKeys, jsonResponse, readJson, RequestError, validateEvent, validateRange } from '../shared/protocol.mjs';

const modelIds = new Set(models.map(x => x.id));

export function createHandler({ env = process.env, fetchImpl = fetch, now = () => new Date() } = {}) {
  return async function handle(request) {
    try {
      const url = new URL(request.url);
      const path = url.pathname;
      const method = request.method;
      if (!['/api/config', '/api/telemetry', '/api/login', '/api/stats'].includes(path))
        throw new RequestError(404, 'not_found');
      const expectedMethod = ['/api/config', '/api/stats'].includes(path) ? 'GET' : 'POST';
      if (method !== expectedMethod) return new Response(null, { status: 405, headers: { Allow: expectedMethod } });
      const base = env.SUPABASE_URL, key = env.SUPABASE_PUBLISHABLE_KEY;
      if (!base || !key || !/^https:\/\/[a-z0-9]+\.supabase\.co$/.test(base))
        throw new RequestError(503, 'service_unavailable');
      if (path === '/api/config') return jsonResponse({ supabaseUrl: base, publishableKey: key });
      let route, body, authorization;
      if (path === '/api/telemetry') {
        route = 'ingest';
        body = validateEvent(await readJson(request), modelIds, now());
      } else if (path === '/api/login') {
        // Login is a same-origin browser action. Credentials never enter logs or URLs.
        if (!env.SITE_ORIGIN || request.headers.get('origin') !== env.SITE_ORIGIN)
          throw new RequestError(403, 'forbidden');
        route = 'login';
        const data = await readJson(request, 512);
        if (!exactKeys(data, ['password']) || typeof data.password !== 'string' || !data.password.length || data.password.length > 128)
          throw new RequestError(400, 'invalid_password');
        body = { password: data.password };
      } else {
        route = 'stats';
        authorization = request.headers.get('authorization');
        if (!authorization?.startsWith('Bearer ') || authorization.length > 8192)
          throw new RequestError(401, 'unauthorized');
        body = validateRange(url.searchParams.get('start'), url.searchParams.get('end'), now());
      }
      const response = await fetchImpl(`${base}/functions/v1/telemetry-gateway/${route}`, {
        method: 'POST', headers: { 'Content-Type': 'application/json', apikey: key, ...(authorization ? { Authorization: authorization } : {}) },
        body: JSON.stringify(body), signal: AbortSignal.timeout(12000), redirect: 'error'
      });
      // Never forward upstream diagnostics, headers, or HTML to the client.
      let result;
      try { result = await response.json(); } catch { throw new RequestError(503, 'service_unavailable'); }
      if (!response.ok) {
        const allowed = new Set(['invalid_event', 'event_expired', 'rate_limited', 'unauthorized', 'forbidden', 'invalid_range', 'invalid_credentials']);
        throw new RequestError([400, 401, 403, 409, 410, 429].includes(response.status) ? response.status : 503,
          allowed.has(result.error) ? result.error : 'service_unavailable');
      }
      return jsonResponse(result);
    } catch (error) {
      if (error instanceof RequestError) return jsonResponse({ error: error.code }, error.status);
      console.error('telemetry_gateway_unavailable');
      return jsonResponse({ error: 'service_unavailable' }, 503);
    }
  };
}
