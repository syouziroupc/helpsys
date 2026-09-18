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
 plan={...plan,targetId:'x',action:'left_click',confidence:.99};
 r=await call({...base,controls:[{...base.controls[0],enabled:undefined}]}); assert(r.status===502,'missing enabled flag must fail closed');
 const huge = await call({...base,pad:'x'.repeat(7_000_100)}); assert(huge.status===413,'chunked/undeclared oversized body');

 plan={...plan,targetId:'x',action:'type_text',confidence:.99};
 r=await call({...base,controls:[{...base.controls[0],focused:false}]}); assert(r.status===502,'unfocused type_text');
 plan={...plan,action:'press_key',targetId:null,key:null}; r=await call(); assert(r.status===502,'missing key');
 plan={...plan,status:'target',action:'left_click',targetId:123,key:null,confidence:.99,x:10,y:10,width:100,height:50}; r=await call(); assert(r.status===502,'non-string targetId');
 plan={...plan,targetId:'x',confidence:'0.99'}; r=await call(); assert(r.status===502,'string confidence');
 plan={...plan,confidence:1.5}; r=await call(); assert(r.status===502,'out-of-range confidence');
 plan={...plan,confidence:.99,x:-1}; r=await call(); assert(r.status===502,'negative geometry');
 plan={...plan,status:'done',action:'none',targetId:null,confidence:.4}; r=await call(); assert(r.status===502,'low done');
 plan={...plan,status:'clarify',action:'none',question:'',confidence:.9}; r=await call(); assert(r.status===502,'empty clarify');
 plan={...plan,status:'target',action:'left_click',instruction:'passwordを入力して送って',question:null,targetId:'x',confidence:.99}; r=await call(); assert(r.status===502,'secret request');
 plan={...plan,instruction:'パスワードを入力してください。'}; r=await call(); assert(r.status===502,'natural Japanese password entry');
 plan={...plan,instruction:'Enter your verification code.'}; r=await call(); assert(r.status===502,'English verification-code entry');
 plan={...plan,instruction:'APIキーを貼り付けてください。'}; r=await call(); assert(r.status===502,'API key paste');
 plan={...plan,instruction:'Proceed anyway to the unsafe site.'}; r=await call(); assert(r.status===502,'warning bypass English');
 plan={...plan,instruction:'詳細設定を開いてこのサイトに進んでください。'}; r=await call(); assert(r.status===502,'warning bypass Japanese');
 upstreamStatus=429; plan={...plan,instruction:'click'}; r=await call(); assert(r.status===502,'provider error normalization');
 upstreamStatus=200;

 const malformedPayloads=[
   {},
   {candidates:[]},
   {candidates:[{content:{parts:[]}}]},
   {candidates:[{content:{parts:[{thought:true,text:'{}'}]}}]},
   {candidates:[{content:{parts:[{text:'not-json'}]}}]},
   {candidates:[{content:{parts:[{text:'null'}]}}]},
   {candidates:[{content:{parts:[{text:'[]'}]}}]}
 ];
 for(const payload of malformedPayloads){
   globalThis.fetch=async()=>new Response(JSON.stringify(payload),{status:200,headers:{'content-type':'application/json'}});
   r=await call(); assert(r.status===502,'malformed Gemini payload must fail closed');
 }

 console.log('HelpSys Stable adversarial worker self-test passed.');
} finally { globalThis.fetch=original; }
