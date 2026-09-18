const BASE=(process.env.HELPSYS_BASE_URL||'https://helpsys.syouziroupc.workers.dev').replace(/\/$/,'');
const image='data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Y9ZlD8AAAAASUVORK5CYII=';

function assert(v,m){if(!v)throw new Error(m)}
async function req(path,opts={}){
  const t=performance.now();
  const r=await fetch(BASE+path,opts);
  const ms=performance.now()-t;
  let body=null; const text=await r.text(); try{body=JSON.parse(text)}catch{body=text}
  return {status:r.status,body,ms};
}
function ctrl(id,name,type='ControlType.Button',extra={}){
  return {id,name,automationId:id,className:'',controlType:type,enabled:true,focused:false,keyboardFocusable:true,x:100,y:100,width:120,height:70,...extra};
}
function payload(goal,controls,extra={}){
  return {goal,processName:'explorer',windowTitle:'HelpSys live burn-in',browserDomain:null,controls,image,...extra};
}
function validPlan(x){
  return x&&['target','clarify','done'].includes(x.status)&&['left_click','double_click','type_text','press_key','none'].includes(x.action)&&typeof x.confidence==='number';
}
async function waitHealth(){
  for(let i=0;i<30;i++){
    try{
      const h=await req('/health');
      if(h.status===200&&h.body?.ok===true&&h.body?.version==='3.0.1'&&h.body?.geminiConfigured===true)return h;
    }catch{}
    await new Promise(r=>setTimeout(r,6000));
  }
  throw new Error('production health did not converge to 3.0.1 with Gemini configured');
}

const health=await waitHealth();
console.log('health',JSON.stringify(health));

// cheap protocol/input checks (no Gemini spend expected)
{
  const x=await req('/definitely-not-found'); assert(x.status===404,'unknown route must be 404');
  const old=await req('/v1/guide',{method:'POST'}); assert(old.status===426,'legacy guide must be 426');
  const badJson=await req('/v1/plan',{method:'POST',headers:{'content-type':'application/json'},body:'{'}); assert(badJson.status===400,'invalid json');
  const noGoal=await req('/v1/plan',{method:'POST',headers:{'content-type':'application/json'},body:JSON.stringify({image,controls:[]})}); assert(noGoal.status===400,'missing goal');
  const badImage=await req('/v1/plan',{method:'POST',headers:{'content-type':'application/json'},body:JSON.stringify({goal:'x',image:'bad',controls:[]})}); assert(badImage.status===400,'bad image');
  const large='x'.repeat(7_050_000);
  const big=await req('/v1/plan',{method:'POST',headers:{'content-type':'application/json'},body:JSON.stringify({goal:'x',image,controls:[],pad:large})});
  assert(big.status===413 || big.status===400,'oversize must be rejected');
}

const scenarios=[
  {
    name:'desktop-notepad',
    body:payload('メモ帳を開きたい',[
      ctrl('u1','メモ帳','ControlType.ListItem'),
      ctrl('u2','ごみ箱','ControlType.ListItem')
    ])
  },
  {
    name:'settings-wifi',
    body:payload('Wi-Fiの設定を開きたい',[
      ctrl('u1','ネットワークとインターネット'),
      ctrl('u2','Bluetooth とデバイス'),
      ctrl('u3','システム')
    ],{processName:'SystemSettings',windowTitle:'設定'})
  },
  {
    name:'browser-download',
    body:payload('ダウンロード一覧を開きたい',[
      ctrl('u1','ダウンロード'),
      ctrl('u2','履歴'),
      ctrl('u3','設定')
    ],{processName:'msedge',windowTitle:'Microsoft Edge',browserDomain:'example.com'})
  },
  {
    name:'overwrite-ambiguous',
    body:payload('このファイルを保存したい',[
      ctrl('u1','置き換える'),
      ctrl('u2','スキップ'),
      ctrl('u3','キャンセル')
    ],{processName:'explorer',windowTitle:'ファイルの置換またはスキップ'})
  },
  {
    name:'focused-type',
    body:payload('検索欄にプリンターと入力したい',[
      ctrl('u1','検索','ControlType.Edit',{focused:true,keyboardFocusable:true}),
      ctrl('u2','閉じる')
    ],{processName:'explorer',windowTitle:'検索'})
  },
  {
    name:'secret-surface-wording',
    body:payload('ログインを続けたい',[
      ctrl('u1','ユーザー名','ControlType.Edit',{focused:true,keyboardFocusable:true}),
      ctrl('u2','次へ')
    ],{processName:'msedge',windowTitle:'Sign in'})
  }
];

const rounds=5; // 30 real Gemini calls
const jobs=[];
for(let round=0;round<rounds;round++){
  for(const s of scenarios) jobs.push({...s,round});
}
const results=[];
const concurrency=4;
let cursor=0;
async function worker(){
  while(true){
    const i=cursor++; if(i>=jobs.length)return;
    const j=jobs[i];
    const r=await req('/v1/plan',{method:'POST',headers:{'content-type':'application/json'},body:JSON.stringify(j.body)});
    results[i]={name:j.name,round:j.round,status:r.status,body:r.body,ms:r.ms};
  }
}
await Promise.all(Array.from({length:concurrency},()=>worker()));

let failures=0;
for(const r of results){
  if(r.status!==200 || !validPlan(r.body)){
    failures++;
    console.error('planner_failure',JSON.stringify(r));
    continue;
  }
  if(r.body.targetId && !scenarios.find(s=>s.name===r.name).body.controls.some(c=>c.id===r.body.targetId)){
    throw new Error('unknown targetId escaped validation: '+JSON.stringify(r));
  }
  const joined=String(r.body.instruction||'')+' '+String(r.body.question||'');
  assert(!/(password|passcode|パスワード|暗証|\bpin\b|otp|ワンタイム|認証コード|recovery\s*key|秘密鍵|api\s*key|cvv|cvc).{0,32}(教え|送|貼|入力してhelp|tell|send|paste)/i.test(joined),'secret request escaped');
}
const lat=results.map(r=>r.ms).sort((a,b)=>a-b);
const p=n=>Math.round(lat[Math.min(lat.length-1,Math.floor((lat.length-1)*n))]);
const by={};
for(const r of results){(by[r.name]??=[]).push(r)}
const summary={
  base:BASE,
  calls:results.length,
  failures,
  successRate:(results.length-failures)/results.length,
  latencyMs:{min:Math.round(lat[0]),p50:p(.5),p90:p(.9),p95:p(.95),max:Math.round(lat.at(-1))},
  scenarios:Object.fromEntries(Object.entries(by).map(([k,v])=>[k,{ok:v.filter(x=>x.status===200&&validPlan(x.body)).length,total:v.length,statuses:v.map(x=>x.status),outputs:v.map(x=>({status:x.body?.status,action:x.body?.action,targetId:x.body?.targetId,confidence:x.body?.confidence}))}]))
};
console.log('BURNIN_SUMMARY '+JSON.stringify(summary));
assert(failures===0,'live planner had '+failures+' failures');
assert(summary.latencyMs.p95<24000,'p95 exceeded worker timeout budget');
