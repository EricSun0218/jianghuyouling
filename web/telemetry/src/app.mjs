import { createClient } from '@supabase/supabase-js';
import { Chart, LineController, LineElement, PointElement, CategoryScale, LinearScale, Tooltip, Legend } from 'chart.js';
import { shanghaiDay, DAY_MS, validateRange } from '../shared/protocol.mjs';
Chart.register(LineController, LineElement, PointElement, CategoryScale, LinearScale, Tooltip, Legend);
const $ = id => document.getElementById(id);
const fmt = n => new Intl.NumberFormat('zh-CN', { maximumFractionDigits: 1 }).format(n);
const pct = (n,d) => d ? `${(100*n/d).toFixed(1)}%` : '—';
const text = (id,value) => { $(id).textContent=value; };
let auth, session, report, chart, loading=false, loadVersion=0;
const errorText = { forbidden:'没有看板访问权限。', unauthorized:'登录已失效，请重新登录。', invalid_credentials:'密码不正确，请重新输入。', invalid_password:'请输入有效密码。', rate_limited:'尝试过于频繁，请稍后再试。', service_unavailable:'服务暂时不可用，请稍后重试。', invalid_range:'请选择最近 90 天内的有效日期范围。' };
async function api(path,options={}) {
  const response=await fetch(path,{...options,cache:'no-store',signal:AbortSignal.timeout(18000)});
  const data=await response.json();
  if (!response.ok) { const error=new Error(errorText[data.error] || '请求未完成，请稍后重试。'); error.status=response.status; throw error; }
  return data;
}
function message(id,value,error=false) { text(id,value); $(id).classList.toggle('error',error); }
function dates(days) {
  const end=shanghaiDay(), start=new Date(Date.parse(`${end}T00:00:00Z`)-(days-1)*DAY_MS).toISOString().slice(0,10);
  $('start').value=start; $('end').value=end;
  document.querySelectorAll('[data-days]').forEach(b=>{ const selected=Number(b.dataset.days)===days; b.classList.toggle('selected',selected);b.setAttribute('aria-pressed',String(selected)); });
}
function row(target,values) {
  const tr=document.createElement('tr');
  for (const value of values) { const td=document.createElement('td'); td.textContent=String(value);tr.append(td); }
  target.append(tr);return tr;
}
function noRows(target,columns,label) { const tr=row(target,[label]);tr.firstChild.colSpan=columns; }
function render(data) {
  report=data;
  const s=data.summary,u=data.usage;
  for (const [id,key] of [['dau','dau'],['wau','wau'],['mau','mau'],['new-clients','new_clients'],['cumulative-clients','cumulative_clients'],['active-clients','active_clients']]) text(id,fmt(s[key]));
  for (const [id,value] of [['input-tokens',u.input_tokens],['output-tokens',u.output_tokens],['success-calls',u.successful_calls],['all-calls',u.calls]]) text(id,fmt(value));
  text('calls-per-client',s.active_clients?fmt(u.successful_calls/s.active_clients):'—');
  text('tokens-per-client',s.active_clients?fmt((u.input_tokens+u.output_tokens)/s.active_clients):'—');
  text('coverage',`用量返回覆盖：输入 ${pct(u.input_usage_calls,u.calls)} · 输出 ${pct(u.output_usage_calls,u.calls)}。未返回的用量不计入 Token 总量。`);
  text('range-description',`${data.start} 至 ${data.end} · 按北京时间统计${data.end===data.today?' · 今日尚未结束':''}`);
  $('token-table').replaceChildren();
  for (const [label,prefix] of [['结束日','day'],['结束日所在月（截至该日）','month'],['累计（截至结束日）','all']]) row($('token-table'),[label,fmt(data.tokens[`${prefix}_input`]),fmt(data.tokens[`${prefix}_output`])]);
  $('daily-table').replaceChildren();
  for (const d of data.daily) row($('daily-table'),[d.day,fmt(d.dau),fmt(d.new_clients),fmt(d.successful_calls),fmt(d.input_tokens),fmt(d.output_tokens)]);
  $('model-table').replaceChildren();text('model-count',`${data.models.length} 个模型分类`);
  for (const m of data.models) {
    const tr=row($('model-table'),[m.label,`${fmt(m.calls)} / ${pct(m.calls,u.calls)}`,fmt(m.successful_calls),fmt(m.input_tokens),fmt(m.output_tokens),pct(m.input_tokens+m.output_tokens,u.input_tokens+u.output_tokens)]);
    const cell=tr.children[1];cell.className='share-cell';const track=document.createElement('div');track.className='share-track';const bar=document.createElement('span');bar.className='share-bar';bar.style.width=`${u.calls?Math.min(100,100*m.calls/u.calls):0}%`;track.append(bar);cell.append(track);
  }
  if (!data.models.length) noRows($('model-table'),6,'所选期间暂无模型使用数据。');
  const r=data.retention;text('d7-rate',pct(r.retained,r.eligible));
  text('retention-note',r.eligible?`${fmt(r.eligible)} 个已成熟新增客户端中，${fmt(r.retained)} 个在第 7 天再次成功使用 AI。`:'暂无已成熟的新增客户端。首次成功使用后的第 7 天结束后，才计入留存。');
  $('retention-table').replaceChildren();
  for (const c of [...r.cohorts].reverse()) row($('retention-table'),[c.day,fmt(c.clients),c.mature?fmt(c.retained):'尚未成熟',c.mature?pct(c.retained,c.clients):'—']);
  if (!r.cohorts.length) noRows($('retention-table'),4,'所选期间暂无新增客户端。');
  $('empty-note').hidden=u.calls!==0;
  $('report').hidden=false;drawChart();
}
function drawChart() {
  if (!report) return;
  const mode=$('chart-metric').value;
  const lines=mode==='tokens'?[['输入 Token','input_tokens','#2d5e9d'],['输出 Token','output_tokens','#68877b']]:mode==='calls'?[['成功调用','successful_calls','#2d5e9d'],['全部调用','calls','#68877b']]:[['AI 日活','dau','#2d5e9d'],['新增客户端','new_clients','#68877b']];
  const data={labels:report.daily.map(d=>d.day.slice(5)),datasets:lines.map(([label,key,color])=>({label,data:report.daily.map(d=>d[key]),borderColor:color,backgroundColor:color,borderWidth:2,pointRadius:report.daily.length>31?0:2,pointHoverRadius:5,tension:.2}))};
  if (chart) { chart.data=data;chart.update();return; }
  chart=new Chart($('trend-chart'),{type:'line',data,options:{responsive:true,maintainAspectRatio:false,animation:matchMedia('(prefers-reduced-motion: reduce)').matches?false:{duration:300},interaction:{mode:'index',intersect:false},plugins:{legend:{position:'bottom',align:'start',labels:{usePointStyle:true,pointStyle:'circle',boxWidth:7,padding:24,color:'#617187',font:{size:11}}},tooltip:{backgroundColor:'#192c44',padding:12}},scales:{x:{grid:{display:false},border:{display:false},ticks:{maxTicksLimit:12,color:'#718096',font:{size:10}}},y:{beginAtZero:true,border:{display:false},grid:{color:'#edf1f6'},ticks:{precision:0,maxTicksLimit:5,color:'#718096',font:{size:10}}}}}});
}
function showLogin() {
  loadVersion++;session=null;report=null;chart?.destroy();chart=null;
  $('password').value='';
  $('report').hidden=true;$('dashboard').hidden=true;$('login').hidden=false;$('logout').hidden=true;
}
async function refresh() {
  if (loading || !session) return;
  const version=++loadVersion;loading=true;$('refresh').disabled=true;
  try {
    validateRange($('start').value,$('end').value);
    message('dashboard-status','正在读取统计…');
    const {data:{session:latest}}=await auth.auth.getSession();
    if (!latest) throw Object.assign(new Error(errorText.unauthorized),{status:401});
    const result=await api(`/api/stats?start=${$('start').value}&end=${$('end').value}`,{headers:{Authorization:`Bearer ${latest.access_token}`}});
    if (version!==loadVersion) return;
    render(result);message('dashboard-status',`已更新 · ${new Intl.DateTimeFormat('zh-CN',{timeZone:'Asia/Shanghai',hour:'2-digit',minute:'2-digit',second:'2-digit'}).format(new Date())} 北京时间`);
  } catch(error) {
    if (version!==loadVersion) return;
    $('report').hidden=true;
    if (error.status===401 || error.status===403) { await auth.auth.signOut({scope:'local'});showLogin();message('login-message',error.message,true); }
    else message('dashboard-status',error.code==='invalid_range'?errorText.invalid_range:(error.name==='TimeoutError'?'读取超时，请重试。':error.message),true);
  } finally { loading=false;$('refresh').disabled=false; }
}
async function enter(next) { session=next;$('login').hidden=true;$('dashboard').hidden=false;$('logout').hidden=false;await refresh(); }
$('login-form').addEventListener('submit',async e=>{
  e.preventDefault();$('login-submit').disabled=true;message('login-message','正在登录…');
  try {
    const tokens=await api('/api/login',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({password:$('password').value})});
    const {data,error}=await auth.auth.setSession(tokens);
    if(error || !data.session) throw new Error('登录未完成，请重试。');
    $('password').value='';message('login-message','');await enter(data.session);
  }
  catch(error){message('login-message',error.message,true);}finally{$('login-submit').disabled=false;}
});
$('logout').addEventListener('click',async()=>{showLogin();await auth.auth.signOut({scope:'local'});message('login-message','已退出登录。');});
$('range-form').addEventListener('submit',e=>{e.preventDefault();document.querySelectorAll('[data-days]').forEach(b=>{b.classList.remove('selected');b.setAttribute('aria-pressed','false');});void refresh();});
document.querySelectorAll('[data-days]').forEach(b=>b.addEventListener('click',()=>{dates(Number(b.dataset.days));void refresh();}));
$('refresh').addEventListener('click',()=>void refresh());$('chart-metric').addEventListener('change',drawChart);
dates(30);const today=shanghaiDay(),earliest=new Date(Date.parse(`${today}T00:00:00Z`)-89*DAY_MS).toISOString().slice(0,10);
for(const id of ['start','end']){$(id).min=earliest;$(id).max=today;}
try {
  const config=await api('/api/config');
  auth=createClient(config.supabaseUrl,config.publishableKey,{auth:{storage:sessionStorage,persistSession:true,autoRefreshToken:true,detectSessionInUrl:false}});
  const {data,error}=await auth.auth.getSession();if(error)throw error;
  $('password').disabled=false;$('login-submit').disabled=false;message('login-message','');
  if(data.session)await enter(data.session);
  auth.auth.onAuthStateChange((event,next)=>{if(event==='SIGNED_OUT')showLogin();else if(next)session=next;});
} catch(error){message('login-message','登录服务暂时不可用，请刷新页面重试。',true);}
