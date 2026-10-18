-- Synthetic data exists only within the rolled-back test transaction.
begin;
do $$
declare
  today date := (current_timestamp at time zone 'Asia/Shanghai')::date;
  a uuid := 'a9899d33-b907-45eb-8f04-d07e4d0a1001';
  b uuid := 'a9899d33-b907-45eb-8f04-d07e4d0a1002';
  c uuid := 'a9899d33-b907-45eb-8f04-d07e4d0a1003';
  payload jsonb;
  response jsonb;
  report jsonb;
  before_calls numeric;
  after_calls numeric;
begin
  if has_function_privilege('anon','public.telemetry_ingest(jsonb)','execute')
    or has_function_privilege('authenticated','public.telemetry_dashboard(date,date)','execute')
    or has_function_privilege('anon','public.telemetry_private_settings()','execute')
    or has_schema_privilege('anon','telemetry','usage') then raise exception 'private access leaked'; end if;
  payload := jsonb_build_object('schema',1,'client_id',a,'event_id',gen_random_uuid(),'day',today,
    'model','other','successful',true,'input_tokens',0,'output_tokens',20);
  select coalesce(sum(calls),0) into before_calls from telemetry.daily_models;
  response := public.telemetry_ingest(payload);
  if response->>'ok' is distinct from 'true' then raise exception 'valid event failed: %',response; end if;
  response := public.telemetry_ingest(payload);
  if response->>'duplicate' is distinct from 'true' then raise exception 'dedupe failed'; end if;
  select coalesce(sum(calls),0) into after_calls from telemetry.daily_models;
  if after_calls - before_calls <> 1 then raise exception 'duplicate counted'; end if;
  response := public.telemetry_ingest(payload || '{"prompt":"never stored"}'::jsonb);
  if response->>'status' is distinct from '400' then raise exception 'extra field accepted'; end if;
  response := public.telemetry_ingest(jsonb_set(payload,'{model}','"private-custom-name"'));
  if response->>'status' is distinct from '400' then raise exception 'unknown model accepted'; end if;
  response := public.telemetry_ingest(jsonb_set(payload,'{input_tokens}','-1'));
  if response->>'status' is distinct from '400' then raise exception 'negative count accepted'; end if;
  response := public.telemetry_ingest(jsonb_set(payload,'{day}',to_jsonb((today - 2)::text)));
  if response->>'status' is distinct from '410' then raise exception 'expired event accepted'; end if;
  response := public.telemetry_ingest(jsonb_set(payload,'{day}',to_jsonb((today + 1)::text)));
  if response->>'status' is distinct from '400' then raise exception 'future event accepted'; end if;
  payload := jsonb_set(jsonb_set(payload,'{event_id}',to_jsonb(gen_random_uuid())),'{day}',to_jsonb((today - 1)::text));
  response := public.telemetry_ingest(payload);
  if response->>'ok' is distinct from 'true' then raise exception 'midnight event failed'; end if;
  if (select first_success_day from telemetry.clients where client_id = a) <> today - 1 then
    raise exception 'out of order first-success failed'; end if;
  payload := jsonb_set(jsonb_set(payload,'{event_id}',to_jsonb(gen_random_uuid())),'{client_id}',to_jsonb(c));
  payload := jsonb_set(jsonb_set(jsonb_set(payload,'{successful}','false'),'{input_tokens}','null'),'{output_tokens}','null');
  response := public.telemetry_ingest(payload);
  if response->>'ok' is distinct from 'true' then raise exception 'failed attempt event failed'; end if;
  if exists (select 1 from telemetry.clients where client_id = c) then raise exception 'failed-only client counted active'; end if;
  insert into telemetry.clients values (b,today - 8,today - 1),(c,today - 8,today - 8);
  insert into telemetry.activity values (today - 1,b),(today - 8,b),(today - 8,c);
  report := public.telemetry_dashboard(today - 8,today);
  if report #>> '{retention,eligible}' <> '2' or report #>> '{retention,retained}' <> '1' then
    raise exception 'D7 maturity/weighted denominator failed: %',report->'retention'; end if;
  if jsonb_array_length(report->'daily') <> 9 then raise exception 'date zero filling failed'; end if;
  if report #>> '{usage,input_usage_calls}' <> '2' or report #>> '{usage,calls}' <> '3' then
    raise exception 'missing usage was treated as reported zero'; end if;
end $$;
select 'all telemetry database checks passed; fixtures rolled back' as verification;
rollback;
