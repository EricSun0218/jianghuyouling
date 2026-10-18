export const MAX_BODY_BYTES = 1024;
export const MAX_COUNT = 2147483647;
export const DAY_MS = 86400000;
const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i;
const DAY = /^\d{4}-\d{2}-\d{2}$/;

export class RequestError extends Error {
  constructor(status, code) { super(code); this.status = status; this.code = code; }
}

export function shanghaiDay(now = new Date()) {
  return new Date(now.getTime() + 8 * 3600000).toISOString().slice(0, 10);
}

export function dayNumber(day) {
  if (typeof day !== 'string' || !DAY.test(day)) return NaN;
  const timestamp = Date.parse(`${day}T00:00:00Z`);
  return Number.isFinite(timestamp) && new Date(timestamp).toISOString().slice(0, 10) === day ? timestamp / DAY_MS : NaN;
}

export function exactKeys(value, keys) {
  return value !== null && typeof value === 'object' && !Array.isArray(value)
    && Object.keys(value).length === keys.length && keys.every(k => Object.hasOwn(value, k));
}

export function validateEvent(value, modelIds, now = new Date()) {
  const fail = () => { throw new RequestError(400, 'invalid_event'); };
  if (!exactKeys(value, ['schema', 'client_id', 'event_id', 'day', 'model', 'successful', 'input_tokens', 'output_tokens']) || value.schema !== 1
      || typeof value.client_id !== 'string' || !UUID.test(value.client_id)
      || typeof value.event_id !== 'string' || !UUID.test(value.event_id)) fail();
  const age = dayNumber(shanghaiDay(now)) - dayNumber(value.day);
  if (!Number.isFinite(age) || age < 0) fail();
  if (age > 1) throw new RequestError(410, 'event_expired');
  if (!modelIds.has(value.model) || typeof value.successful !== 'boolean') fail();
  for (const field of ['input_tokens', 'output_tokens']) {
    if (value[field] !== null && (!Number.isSafeInteger(value[field]) || value[field] < 0 || value[field] > MAX_COUNT)) fail();
  }
  return value;
}

export function validateRange(start, end, now = new Date()) {
  const today = dayNumber(shanghaiDay(now));
  const from = dayNumber(start), to = dayNumber(end);
  if (!Number.isFinite(from) || !Number.isFinite(to) || from > to || to > today || from < today - 89)
    throw new RequestError(400, 'invalid_range');
  return { start, end };
}

export async function readJson(request, limit = MAX_BODY_BYTES) {
  if (request.headers.get('content-type')?.split(';')[0].trim().toLowerCase() !== 'application/json')
    throw new RequestError(415, 'json_required');
  const contentLength = Number(request.headers.get('content-length') || 0);
  if (contentLength > limit) throw new RequestError(413, 'body_too_large');
  const reader = request.body?.getReader();
  if (!reader) throw new RequestError(400, 'invalid_json');
  const chunks = [];
  let length = 0;
  try {
    while (true) {
      const { done, value } = await reader.read();
      if (done) break;
      length += value.byteLength;
      if (length > limit) { await reader.cancel(); throw new RequestError(413, 'body_too_large'); }
      chunks.push(value);
    }
  } finally { reader.releaseLock(); }
  const bytes = new Uint8Array(length);
  let offset = 0;
  for (const chunk of chunks) { bytes.set(chunk, offset); offset += chunk.byteLength; }
  try { return JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(bytes)); }
  catch { throw new RequestError(400, 'invalid_json'); }
}

export function jsonResponse(body, status = 200) {
  return Response.json(body, { status, headers: { 'Cache-Control': 'no-store', 'X-Content-Type-Options': 'nosniff' } });
}
