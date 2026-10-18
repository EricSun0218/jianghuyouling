import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import models from '../shared/models.json' with { type: 'json' };
import { shanghaiDay, validateEvent, validateRange, readJson } from '../shared/protocol.mjs';
import { createHandler } from '../lib/handler.mjs';
const now = new Date('2026-10-19T02:00:00Z');
const modelIds = new Set(models.map(x => x.id));
const event = () => ({ schema:1, client_id:'c125924b-1648-462d-9190-c9eac14fcde1', event_id:'ef45997e-30a9-4207-813f-5a2d079c10a2',
  day:'2026-10-19', model:'other', successful:true, input_tokens:0, output_tokens:null });
const invalid = (value,status=400) => assert.throws(() => validateEvent(value,modelIds,now),e => e.status === status);
test('UTC+8 dates and immediate upload midnight boundary', () => {
  assert.equal(shanghaiDay(new Date('2026-10-18T16:00:00Z')),'2026-10-19');
  validateEvent({...event(),day:'2026-10-18'},modelIds,now);
  invalid({...event(),day:'2026-10-17'},410);
  invalid({...event(),day:'2026-10-20'}); invalid({...event(),day:'2026-02-30'});
});
test('strict event contains no free text, bulk rows, or private model names', () => {
  validateEvent(event(),modelIds,now);
  invalid({...event(),prompt:'never accepted'}); invalid({...event(),rows:[]});
  invalid({...event(),model:'secret-custom-model'}); invalid({...event(),successful:1});
  invalid({...event(),event_id:'not-an-id'});
});
test('null is missing usage; zero is measured usage', () => {
  for (const field of ['input_tokens','output_tokens']) {
    for (const v of [-1,0.5,2147483648,'1',undefined]) invalid({...event(),[field]:v});
    for (const v of [null,0,2147483647]) validateEvent({...event(),[field]:v},modelIds,now);
  }
});
test('dashboard range retains enough source activity for rolling MAU', () => {
  validateRange('2026-07-22','2026-10-19',now);
  for (const [a,b] of [['2026-07-21','2026-10-19'],['2026-10-20','2026-10-20'],['2026-10-19','2026-10-18'],['bad','2026-10-19']])
    assert.throws(() => validateRange(a,b,now),e => e.status === 400);
});
test('bounded JSON rejects oversized and malformed input', async () => {
  for (const [body,status,type] of [['x'.repeat(2000),413,'application/json'],['{',400,'application/json'],['{}',415,'text/plain']])
    await assert.rejects(readJson(new Request('https://example.com',{method:'POST',headers:{'Content-Type':type},body})),e => e.status === status);
});
test('proxy rejects unauthorized stats and cross-origin login before upstream', async () => {
  let calls=0;
  const handler=createHandler({now:()=>now,env:{SUPABASE_URL:'https://example.supabase.co',SUPABASE_PUBLISHABLE_KEY:'public',SITE_ORIGIN:'https://stats.example.com'},
    fetchImpl:async()=>{calls++;return Response.json({ok:true});}});
  assert.equal((await handler(new Request('https://stats.example.com/api/stats?start=2026-10-01&end=2026-10-19'))).status,401);
  assert.equal((await handler(new Request('https://stats.example.com/api/login',{method:'POST',headers:{Origin:'https://evil.invalid','Content-Type':'application/json'},body:'{"email":"owner@example.com"}'}))).status,403);
  assert.equal((await handler(new Request('https://stats.example.com/api/unknown'))).status,404); assert.equal(calls,0);
});
test('proxy strips upstream diagnostics and never forwards client cookies',async()=>{
  let sent;
  const handler=createHandler({now:()=>now,env:{SUPABASE_URL:'https://example.supabase.co',SUPABASE_PUBLISHABLE_KEY:'public'},
    fetchImpl:async(url,options)=>{sent={url,options};return Response.json({error:'private database detail'},{status:500});}});
  const response=await handler(new Request('https://stats.example.com/api/telemetry',{method:'POST',headers:{'Content-Type':'application/json',Cookie:'secret'},body:JSON.stringify(event())}));
  assert.equal(response.status,503); assert.deepEqual(await response.json(),{error:'service_unavailable'});
  assert.equal(sent.options.headers.Cookie,undefined); assert.equal(sent.options.redirect,'error');
});

test('proxy routes exact observations and authenticated queries without a private key',async()=>{
  const sent=[];
  const handler=createHandler({now:()=>now,env:{SUPABASE_URL:'https://example.supabase.co',SUPABASE_PUBLISHABLE_KEY:'public',SITE_ORIGIN:'https://stats.example.com'},
    fetchImpl:async(url,options)=>{sent.push({url,options});return Response.json({ok:true});}});
  const config=await handler(new Request('https://stats.example.com/api/config'));
  assert.deepEqual(await config.json(),{supabaseUrl:'https://example.supabase.co',publishableKey:'public'});
  const ingest=await handler(new Request('https://stats.example.com/api/telemetry',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(event())}));
  assert.equal(ingest.status,200); assert.equal(ingest.headers.get('cache-control'),'no-store');
  assert.deepEqual(JSON.parse(sent[0].options.body),event());
  assert.equal(sent[0].url,'https://example.supabase.co/functions/v1/telemetry-gateway/ingest');
  const stats=await handler(new Request('https://stats.example.com/api/stats?start=2026-10-01&end=2026-10-19',{headers:{Authorization:'Bearer example-token'}}));
  assert.equal(stats.status,200);assert.equal(sent[1].options.headers.Authorization,'Bearer example-token');
  assert.deepEqual(JSON.parse(sent[1].options.body),{start:'2026-10-01',end:'2026-10-19'});
  const wrongMethod=await handler(new Request('https://stats.example.com/api/telemetry'));
  assert.equal(wrongMethod.status,405);assert.equal(sent.length,2);
});

test('password-only login rejects the retired email body and preserves password characters',async()=>{
  const sent=[];
  const handler=createHandler({env:{SUPABASE_URL:'https://example.supabase.co',SUPABASE_PUBLISHABLE_KEY:'public',SITE_ORIGIN:'https://stats.example.com'},
    fetchImpl:async(url,options)=>{sent.push({url,options});return Response.json({access_token:'fixture-access',refresh_token:'fixture-refresh'});}});
  const post=body=>new Request('https://stats.example.com/api/login',{method:'POST',headers:{'Content-Type':'application/json',Origin:'https://stats.example.com'},body:JSON.stringify(body)});
  for (const value of [{email:'old@example.com'},{password:''},{password:'x'.repeat(129)},{password:'x',email:'unexpected@example.com'}])
    assert.equal((await handler(post(value))).status,400);
  assert.equal(sent.length,0);
  const response=await handler(post({password:' Example password '}));
  assert.equal(response.status,200);assert.equal(response.headers.get('cache-control'),'no-store');
  assert.deepEqual(JSON.parse(sent[0].options.body),{password:' Example password '});
  assert.equal(sent[0].url,'https://example.supabase.co/functions/v1/telemetry-gateway/login');
});

test('password login has one credential field and cannot replace the service-role database session',async()=>{
  const html=await readFile(new URL('../public/index.html',import.meta.url),'utf8');
  const form=html.match(/<form id="login-form"[\s\S]*?<\/form>/)[0];
  assert.equal((form.match(/<input\b/g)||[]).length,1);assert.ok(form.includes('type="password"'));
  assert.ok(!html.includes('发送登录链接'));
  const edge=await readFile(new URL('../supabase/functions/telemetry-gateway/index.ts',import.meta.url),'utf8');
  assert.ok(edge.includes('loginClient.auth.signInWithPassword'));
  assert.ok(!edge.includes('database.auth.signInWithPassword'));assert.ok(!edge.includes('signInWithOtp'));
  const app=await readFile(new URL('../src/app.mjs',import.meta.url),'utf8');
  assert.ok(app.includes('detectSessionInUrl:false'));assert.ok(app.includes('auth.auth.setSession(tokens)'));
});
