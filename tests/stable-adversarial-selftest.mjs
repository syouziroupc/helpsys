import worker from '../worker/stable-gemini.js';

function assert(v,m){ if(!v) throw new Error(m); }
const image='data:image/jpeg;base64,/9j/4AAQSkZJRgABAQAAAQABAAD/2Q==';
const base={goal:'test',image,controls:[{id:'x',name:'Field',controlType:'ControlType.Edit',enabled:true,focused:true,keyboardFocusable:true,x:10,y:10,width:100,height:50}]};
let plan={status:'target',action:'left_click',instruction:'click',question:null,targetId:'x',key:null,confidence:.99,x:10,y:10,width:100,height:50};
let upstreamStatus=200;
const original=globalThis.fetch;
globalThis.fetch=async()=>new Response(JSON.stringify(upstreamStatus===200?{candidates:[{content:{parts:[{text:JSON.stringify(plan)}]}}]}:{error:'provider'}),{status:upstreamStatus,headers:{'content-type':'application/json'}});
async function call(body=base,env={GEMINI_API_KEY:'k'}){
 return worker.fetch(new Request('https://x/v1/plan',{method:'POST',headers:{'content-type':'application/json'},body:JSON.stringify(body)}),env);
}
try{
 let r=await worker.fetch(new Request('https://x/health'),{}); let b=await r.json();
 assert(r.status===200 && b.geminiConfigured===false,'health config');
 r=await worker.fetch(new Request('https://x/nope'),{}); assert(r.status===404,'unknown route');
 r=await call({...base,goal:''}); assert(r.status===400,'empty goal');
 r=await call({...base,image:'bogus'}); assert(r.status===400,'bad image');

 plan={...plan,targetId:'x',action:'type_text',confidence:.99};
 r=await call({...base,controls:[{...base.controls[0],focused:false}]}); assert(r.status===502,'unfocused type_text');
 plan={...plan,action:'press_key',targetId:null,key:null}; r=await call(); assert(r.status===502,'missing key');
 plan={...plan,status:'done',action:'none',targetId:null,confidence:.4}; r=await call(); assert(r.status===502,'low done');
 plan={...plan,status:'clarify',action:'none',question:'',confidence:.9}; r=await call(); assert(r.status===502,'empty clarify');
 plan={...plan,status:'target',action:'left_click',instruction:'passwordを入力して送って',question:null,targetId:'x',confidence:.99}; r=await call(); assert(r.status===502,'secret request');
 upstreamStatus=429; plan={...plan,instruction:'click'}; r=await call(); assert(r.status===502,'provider error normalization');

 console.log('HelpSys Stable adversarial worker self-test passed.');
} finally { globalThis.fetch=original; }
