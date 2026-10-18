-- Private aggregate telemetry. Apply to a dedicated project, not the game database.
-- Runtime identity/configuration is provisioned separately and never committed.
create schema telemetry;
revoke all on schema telemetry from public, anon, authenticated;
grant usage on schema telemetry to service_role;

create table telemetry.settings (
  singleton boolean primary key default true check (singleton),
  owner_email text not null check (owner_email = lower(owner_email)),
  site_origin text not null check (site_origin ~ '^https://[a-z0-9.-]+$'),
  daily_event_limit integer not null default 25000 check (daily_event_limit between 1 and 25000),
  last_cleanup date not null default '1970-01-01'
);
create table telemetry.models (id text primary key, label text not null);
create table telemetry.clients (
  client_id uuid primary key,
  first_success_day date not null,
  last_success_day date not null,
  check (last_success_day >= first_success_day)
);
create index clients_first_success_day_idx on telemetry.clients (first_success_day);
create table telemetry.activity (
  day date not null,
  client_id uuid not null references telemetry.clients (client_id),
  primary key (day, client_id)
);
create index activity_client_day_idx on telemetry.activity (client_id, day);
create table telemetry.daily_counts (
  day date primary key,
  dau bigint not null default 0 check (dau >= 0)
);
create table telemetry.daily_models (
  day date not null,
  model text not null references telemetry.models (id),
  calls bigint not null check (calls > 0),
  successful_calls bigint not null check (successful_calls between 0 and calls),
  input_tokens bigint not null check (input_tokens >= 0),
  output_tokens bigint not null check (output_tokens >= 0),
  input_usage_calls bigint not null check (input_usage_calls between 0 and calls),
  output_usage_calls bigint not null check (output_usage_calls between 0 and calls),
  primary key (day, model)
);
create index daily_models_model_idx on telemetry.daily_models (model);
create table telemetry.receipts (
  event_id uuid primary key,
  client_id uuid not null,
  day date not null
);
create index receipts_client_day_idx on telemetry.receipts (client_id, day);
create index receipts_day_idx on telemetry.receipts (day);
create table telemetry.daily_budget (day date primary key, events integer not null);

alter table telemetry.settings enable row level security;
alter table telemetry.models enable row level security;
alter table telemetry.clients enable row level security;
alter table telemetry.activity enable row level security;
alter table telemetry.daily_counts enable row level security;
alter table telemetry.daily_models enable row level security;
alter table telemetry.receipts enable row level security;
alter table telemetry.daily_budget enable row level security;
revoke all on all tables in schema telemetry from public, anon, authenticated;
grant select, insert, update, delete on all tables in schema telemetry to service_role;

create function public.telemetry_private_settings() returns jsonb
language sql security invoker set search_path = '' as $$
  select jsonb_build_object('owner_email', owner_email, 'site_origin', site_origin)
  from telemetry.settings where singleton;
$$;

create function public.telemetry_ingest(event jsonb) returns jsonb
language plpgsql security invoker set search_path = '' as $$
declare
  today date := (current_timestamp at time zone 'Asia/Shanghai')::date;
  event_day date;
  client uuid;
  event_uuid uuid;
  field text;
  receipt telemetry.receipts%rowtype;
  inserted integer;
  quota integer;
  budget integer;
begin
  if jsonb_typeof(event) is distinct from 'object' then
    return jsonb_build_object('status',400,'error','invalid_event');
  end if;
  if (select count(*) from jsonb_object_keys(event)) <> 8
    or not event ?& array['schema','client_id','event_id','day','model','successful','input_tokens','output_tokens']
    or event->'schema' is distinct from '1'::jsonb
    or jsonb_typeof(event->'successful') is distinct from 'boolean'
    or jsonb_typeof(event->'client_id') is distinct from 'string'
    or jsonb_typeof(event->'event_id') is distinct from 'string'
    or jsonb_typeof(event->'day') is distinct from 'string'
    or event->>'client_id' !~* '^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$'
    or event->>'event_id' !~* '^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$'
    or event->>'day' !~ '^\d{4}-\d{2}-\d{2}$'
    or not exists (select 1 from telemetry.models where id = event->>'model') then
    return jsonb_build_object('status',400,'error','invalid_event');
  end if;
  begin
    event_day := (event->>'day')::date;
    client := (event->>'client_id')::uuid;
    event_uuid := (event->>'event_id')::uuid;
  exception when others then
    return jsonb_build_object('status',400,'error','invalid_event');
  end;
  if event_day > today then return jsonb_build_object('status',400,'error','invalid_event'); end if;
  if event_day < today - 1 then return jsonb_build_object('status',410,'error','event_expired'); end if;
  foreach field in array array['input_tokens','output_tokens'] loop
    if event->field <> 'null'::jsonb then
      if jsonb_typeof(event->field) is distinct from 'number' or event->>field !~ '^[0-9]{1,10}$' then
        return jsonb_build_object('status',400,'error','invalid_event');
      end if;
      if (event->>field)::bigint > 2147483647 then return jsonb_build_object('status',400,'error','invalid_event'); end if;
    end if;
  end loop;
  perform pg_advisory_xact_lock(hashtextextended(client::text, 0));
  select * into receipt from telemetry.receipts where event_id = event_uuid;
  if found then
    if receipt.client_id <> client or receipt.day <> event_day then
      return jsonb_build_object('status',409,'error','invalid_event');
    end if;
    return jsonb_build_object('ok',true,'duplicate',true);
  end if;
  if (select count(*) from telemetry.receipts where client_id = client and day = event_day) >= 5000 then
    return jsonb_build_object('status',429,'error','rate_limited');
  end if;
  select daily_event_limit into quota from telemetry.settings where singleton;
  if quota is null then return jsonb_build_object('status',503,'error','service_unavailable'); end if;
  insert into telemetry.daily_budget (day, events) values (today,0) on conflict do nothing;
  select events into budget from telemetry.daily_budget where day = today for update;
  if budget >= quota then return jsonb_build_object('status',429,'error','rate_limited'); end if;
  insert into telemetry.receipts (event_id,client_id,day) values (event_uuid,client,event_day) on conflict do nothing;
  get diagnostics inserted = row_count;
  if inserted = 0 then return jsonb_build_object('status',409,'error','invalid_event'); end if;
  update telemetry.daily_budget set events = events + 1 where day = today;
  if (event->>'successful')::boolean then
    insert into telemetry.clients as c (client_id,first_success_day,last_success_day) values (client,event_day,event_day)
    on conflict (client_id) do update set first_success_day = least(c.first_success_day,excluded.first_success_day),
      last_success_day = greatest(c.last_success_day,excluded.last_success_day);
    insert into telemetry.activity (day,client_id) values (event_day,client) on conflict do nothing;
    get diagnostics inserted = row_count;
    if inserted > 0 then
      insert into telemetry.daily_counts as d (day,dau) values (event_day,1)
      on conflict (day) do update set dau = d.dau + 1;
    end if;
  end if;
  insert into telemetry.daily_models as d (day,model,calls,successful_calls,input_tokens,output_tokens,input_usage_calls,output_usage_calls)
  values (event_day,event->>'model',1,case when (event->>'successful')::boolean then 1 else 0 end,
    coalesce((event->>'input_tokens')::bigint,0),coalesce((event->>'output_tokens')::bigint,0),
    case when event->>'input_tokens' is null then 0 else 1 end,case when event->>'output_tokens' is null then 0 else 1 end)
  on conflict (day,model) do update set calls = d.calls + 1, successful_calls = d.successful_calls + excluded.successful_calls,
    input_tokens = d.input_tokens + excluded.input_tokens, output_tokens = d.output_tokens + excluded.output_tokens,
    input_usage_calls = d.input_usage_calls + excluded.input_usage_calls, output_usage_calls = d.output_usage_calls + excluded.output_usage_calls;
  update telemetry.settings set last_cleanup = today where singleton and last_cleanup < today;
  if found then
    delete from telemetry.receipts where day < today - 1;
    delete from telemetry.daily_budget where day < today - 7;
    delete from telemetry.activity where day < today - 119;
  end if;
  return jsonb_build_object('ok',true,'duplicate',false);
end;
$$;

create function public.telemetry_dashboard(from_day date, to_day date) returns jsonb
language plpgsql security invoker set search_path = '' as $$
declare
  today date := (current_timestamp at time zone 'Asia/Shanghai')::date;
  report jsonb;
begin
  if from_day is null or to_day is null or from_day > to_day or to_day > today or from_day < today - 89 then
    raise exception 'invalid_range' using errcode = '22023';
  end if;
  with
  period as (
    select coalesce(sum(calls),0) calls, coalesce(sum(successful_calls),0) successful_calls,
      coalesce(sum(input_tokens),0) input_tokens, coalesce(sum(output_tokens),0) output_tokens,
      coalesce(sum(input_usage_calls),0) input_usage_calls, coalesce(sum(output_usage_calls),0) output_usage_calls
    from telemetry.daily_models where day between from_day and to_day
  ),
  tokens as (
    select coalesce(sum(input_tokens) filter (where day = to_day),0) day_input,
      coalesce(sum(output_tokens) filter (where day = to_day),0) day_output,
      coalesce(sum(input_tokens) filter (where day >= date_trunc('month', to_day)::date),0) month_input,
      coalesce(sum(output_tokens) filter (where day >= date_trunc('month', to_day)::date),0) month_output,
      coalesce(sum(input_tokens),0) all_input, coalesce(sum(output_tokens),0) all_output
    from telemetry.daily_models where day <= to_day
  ),
  daily_usage as (
    select day, sum(successful_calls) successful_calls, sum(calls) calls,
      sum(input_tokens) input_tokens, sum(output_tokens) output_tokens
    from telemetry.daily_models where day between from_day and to_day group by day
  ),
  new_clients as (
    select first_success_day as day, count(*) total from telemetry.clients
    where first_success_day between from_day and to_day group by first_success_day
  ),
  days as (select generate_series(from_day, to_day, interval '1 day')::date as day),
  daily as (
    select days.day, coalesce(d.dau,0) dau, coalesce(n.total,0) new_clients,
      coalesce(u.calls,0) calls, coalesce(u.successful_calls,0) successful_calls,
      coalesce(u.input_tokens,0) input_tokens, coalesce(u.output_tokens,0) output_tokens
    from days left join telemetry.daily_counts d using (day)
      left join daily_usage u using (day) left join new_clients n using (day)
  ),
  model_totals as (
    select m.id, m.label, sum(d.calls) calls, sum(d.successful_calls) successful_calls,
      sum(d.input_tokens) input_tokens, sum(d.output_tokens) output_tokens
    from telemetry.daily_models d join telemetry.models m on d.model = m.id
    where d.day between from_day and to_day group by m.id, m.label
  ),
  cohorts as (
    select c.first_success_day as day, count(*) clients, count(a.client_id) retained,
      c.first_success_day + 7 < today mature
    from telemetry.clients c left join telemetry.activity a on a.client_id = c.client_id and a.day = c.first_success_day + 7
    where c.first_success_day between from_day and to_day group by c.first_success_day
  )
  select jsonb_build_object(
    'timezone', 'Asia/Shanghai', 'today', today, 'start', from_day, 'end', to_day,
    'summary', jsonb_build_object(
      'dau', coalesce((select dau from telemetry.daily_counts where day = to_day),0),
      'wau', (select count(distinct client_id) from telemetry.activity where day between to_day - 6 and to_day),
      'mau', (select count(distinct client_id) from telemetry.activity where day between to_day - 29 and to_day),
      'active_clients', (select count(distinct client_id) from telemetry.activity where day between from_day and to_day),
      'new_clients', (select count(*) from telemetry.clients where first_success_day between from_day and to_day),
      'cumulative_clients', (select count(*) from telemetry.clients where first_success_day <= to_day)),
    'usage', (select to_jsonb(period) from period), 'tokens', (select to_jsonb(tokens) from tokens),
    'daily', coalesce((select jsonb_agg(to_jsonb(daily) order by day) from daily),'[]'::jsonb),
    'models', coalesce((select jsonb_agg(to_jsonb(model_totals) order by calls desc, id) from model_totals),'[]'::jsonb),
    'retention', jsonb_build_object(
      'eligible', coalesce((select sum(clients) from cohorts where mature),0),
      'retained', coalesce((select sum(retained) from cohorts where mature),0),
      'cohorts', coalesce((select jsonb_agg(jsonb_build_object('day', day, 'clients', clients,
        'retained', case when mature then retained else null end, 'mature', mature) order by day) from cohorts),'[]'::jsonb))
  ) into report;
  return report;
end;
$$;

revoke all on function public.telemetry_private_settings() from public, anon, authenticated;
revoke all on function public.telemetry_ingest(jsonb) from public, anon, authenticated;
revoke all on function public.telemetry_dashboard(date, date) from public, anon, authenticated;
grant execute on function public.telemetry_private_settings() to service_role;
grant execute on function public.telemetry_ingest(jsonb) to service_role;
grant execute on function public.telemetry_dashboard(date, date) to service_role;
