import { createClient } from 'npm:@supabase/supabase-js@2.118.0';
import models from '../../../shared/models.json' with { type: 'json' };
import { exactKeys, jsonResponse, readJson, RequestError, validateEvent, validateRange } from '../../../shared/protocol.mjs';

const modelIds = new Set(models.map(x => x.id));
const database = createClient(Deno.env.get('SUPABASE_URL')!, Deno.env.get('SUPABASE_SERVICE_ROLE_KEY')!, {
  auth: { persistSession: false, autoRefreshToken: false, detectSessionInUrl: false }
});

Deno.serve(async (request: Request) => {
  try {
    if (request.method !== 'POST') throw new RequestError(405, 'method_not_allowed');
    const route = new URL(request.url).pathname.split('/').pop();
    if (route === 'ingest') {
      const event = validateEvent(await readJson(request), modelIds);
      const { data, error } = await database.rpc('telemetry_ingest', { event });
      if (error) throw new RequestError(503, 'service_unavailable');
      return jsonResponse(data, data.status || 200);
    }
    if (route !== 'login' && route !== 'stats') throw new RequestError(404, 'not_found');
    // Private settings are never sent to the browser or included in source code.
    const { data: settings, error: configError } = await database.rpc('telemetry_private_settings');
    if (configError || !settings?.owner_email || !settings?.site_origin) throw new RequestError(503, 'service_unavailable');
    if (route === 'login') {
      const body = await readJson(request, 512);
      if (!exactKeys(body, ['password']) || typeof body.password !== 'string' || !body.password.length || body.password.length > 128)
        throw new RequestError(400, 'invalid_credentials');
      // A dedicated client prevents a successful login from replacing the shared
      // database client's service-role authorization with an end-user session.
      const loginClient = createClient(Deno.env.get('SUPABASE_URL')!, Deno.env.get('SUPABASE_SERVICE_ROLE_KEY')!, {
        auth: { persistSession: false, autoRefreshToken: false, detectSessionInUrl: false }
      });
      const { data, error } = await loginClient.auth.signInWithPassword({ email: settings.owner_email, password: body.password });
      if (error || !data.session) {
        if (error?.status === 429) throw new RequestError(429, 'rate_limited');
        if (error && error.status >= 500) throw new RequestError(503, 'service_unavailable');
        throw new RequestError(401, 'invalid_credentials');
      }
      if (!data.user?.email_confirmed_at || data.user.email?.trim().toLowerCase() !== settings.owner_email)
        throw new RequestError(403, 'forbidden');
      return jsonResponse({ access_token: data.session.access_token, refresh_token: data.session.refresh_token });
    }
    const token = request.headers.get('authorization');
    if (!token?.startsWith('Bearer ') || token.length > 8192) throw new RequestError(401, 'unauthorized');
    // getUser verifies the token with Auth; never trust decoded JWT/email metadata alone.
    const { data: { user }, error: authError } = await database.auth.getUser(token.slice(7));
    if (authError || !user) throw new RequestError(401, 'unauthorized');
    if (!user.email_confirmed_at || user.email?.trim().toLowerCase() !== settings.owner_email)
      throw new RequestError(403, 'forbidden');
    const body = await readJson(request, 256);
    if (!exactKeys(body, ['start', 'end'])) throw new RequestError(400, 'invalid_range');
    const range = validateRange(body.start, body.end);
    const { data, error } = await database.rpc('telemetry_dashboard', { from_day: range.start, to_day: range.end });
    if (error) throw new RequestError(503, 'service_unavailable');
    return jsonResponse(data);
  } catch (error) {
    if (error instanceof RequestError) return jsonResponse({ error: error.code }, error.status);
    console.error('telemetry_request_failed');
    return jsonResponse({ error: 'service_unavailable' }, 503);
  }
});
