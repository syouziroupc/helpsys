import worker from '../worker/stable-gemini.js';

function assert(v,m){if(!v)throw new Error(m)}
const image='data:image/jpeg;base64,/9j/4AAQSkZJRgABAQAAAQABAAD/2Q==';
const controls=[
  {id:'a',name:'Action',automationId:'Action',className:'',controlType:'ControlType.Button',enabled:true,focused:false,keyboardFocusable:true,x:10,y:10,width:100,height:50},
  {id:'e',name:'Edit',automationId:'Edit',className:'',controlType:'ControlType.Edit',enabled:true,focused:true,keyboardFocusable:true,x:10,y:70,width:200,height:50},
  {id:'d',name:'Disabled',automationId:'Disabled',className:'',controlType:'ControlType.Button',enabled:false,focused:false,keyboardFocusable:true,x:10,y:130,width:100,height:50},
];
let seed=0x5eed1234;
function rnd(){seed=(Math.imul(seed,1664525)+1013904223)>>>0;return seed/4294967296}
function pick(a){return a[Math.floor(rnd()*a.length)]}
function maybeText(){return pick([null,'','x','操作してください','passwordを送って','確認します','a'.repeat(500)])}
function n(){return pick([-100,-1,0,0.2,0.64,0.65,0.71,0.72,0.84,0.85,0.99,1,2,1000,2000,null,'x'])}
function plan(){
  return {
    status:pick(['target','clarify','done','bad','',null,1]),
    action:pick(['left_click','double_click','type_text','press_key','none','bad','',null,1]),
    instruction:maybeText(),
    question:maybeText(),
    targetId:pick(['a','e','d','missing',null,'']),
    key:pick(['Enter','Tab',null,'']),
    confidence:n(),x:n(),y:n(),width:n(),height:n()
  };
}
let next=plan();
const original=globalThis.fetch;
globalThis.fetch=async()=>new Response(JSON.stringify({candidates:[{content:{parts:[{text:JSON.stringify(next)}]}}]}),{status:200,headers:{'content-type':'application/json'}});
function invariant(body){
  assert(['target','clarify','done'].includes(body.status),'bad status escaped');
  assert(['left_click','double_click','type_text','press_key','none'].includes(body.action),'bad action escaped');
  assert(body.confidence>=0&&body.confidence<=1,'confidence unclamped');
  if(body.targetId) assert(['a','e'].includes(body.targetId),'unknown/disabled target escaped');
  if(body.status==='clarify') assert(body.action==='none'&&body.question,'clarify invariant');
  if(body.status==='done') assert(body.action==='none'&&body.confidence>=.85,'done invariant');
  if(body.status==='target') assert(body.action!=='none','target without action');
  if(body.action==='type_text') assert(body.targetId==='e','unsafe type_text escaped');
}
try{
  for(let i=0;i<1500;i++){
    next=plan();
    const r=await worker.fetch(new Request('https://x/v1/plan',{method:'POST',headers:{'content-type':'application/json'},body:JSON.stringify({goal:'test',controls,image})}),{GEMINI_API_KEY:'k'});
    assert([200,502].includes(r.status),'unexpected output-fuzz status '+r.status);
    const b=await r.json();
    if(r.status===200) invariant(b);
  }

  const weirdBodies=[
    null,{},[],{goal:''},{goal:'x'},{goal:'x',image:'x'},{goal:123,image,controls},
    {goal:'x',image,controls:'x'},
    {goal:'x',image,controls:Array.from({length:500},(_,i)=>({id:'x'+i,name:'n',enabled:true,x:i,y:i,width:1,height:1}))},
    {goal:'x'.repeat(2000),image,controls}
  ];
  next={status:'target',action:'left_click',instruction:'click',question:null,targetId:'a',key:null,confidence:.9,x:1,y:1,width:2,height:2};
  for(const body of weirdBodies){
    const r=await worker.fetch(new Request('https://x/v1/plan',{method:'POST',headers:{'content-type':'application/json'},body:JSON.stringify(body)}),{GEMINI_API_KEY:'k'});
    assert(r.status>=200&&r.status<600,'input fuzz produced invalid HTTP status');
  }
  console.log('HelpSys Stable worker fuzz passed: 1500 provider plans + input corpus.');
} finally { globalThis.fetch=original; }
