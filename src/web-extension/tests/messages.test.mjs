import test from 'node:test';
import assert from 'node:assert/strict';
import vm from 'node:vm';
import fs from 'node:fs';
import ts from 'typescript';
const source = fs.readFileSync(new URL('../entrypoints/background.ts', import.meta.url), 'utf8').replace(/^import .*;\n/gm, '');
async function harness(legacy = false) {
  let listener, state, time=1500, focused=true, active=true, httpStatus=204;
  const requests=[], saved={};
  const background = {
    browser: { storage:{local:{get:async()=>saved,set:async value=>Object.assign(saved,value),setAccessLevel:async()=>{}}},
      action:{setTitle:async()=>{},setIcon:async()=>{},onClicked:{addListener:()=>{}}},
      runtime:{id:'test-extension',getURL:p=>'chrome-extension://test-extension'+p,openOptionsPage:()=>{},onInstalled:{addListener:()=>{}},onMessage:{addListener:f=>listener=f}},
      contextMenus:{removeAll:async()=>{},create:()=>{},onClicked:{addListener:()=>{}}},
      tabs:{get:async id=>({id,windowId:1,active,url:'https://example.test/page'}),query:async()=>[{id:1}]},windows:{get:async()=>({focused})}},
    connectionStatusStorage:{setValue:async s=>state=s},
    defineBackground:f=>f(),performance:{now:()=>time},AbortSignal,URL,Uint8Array,DOMException,TypeError,Math,
    fetch:async (url,options)=>{ requests.push({url,options}); if (url.endsWith('/auth/pair')) return new Response(JSON.stringify({token:'a'.repeat(64)}),{status:200});return new Response(null,{status:httpStatus}); },
  };
  if (legacy) { background.browser.browserAction = background.browser.action; delete background.browser.action; }
  const output=ts.transpileModule(source.replace('export default ',''),{compilerOptions:{target:ts.ScriptTarget.ES2022,module:ts.ModuleKind.None}}).outputText;
  vm.runInNewContext(output, background);
  await new Promise(resolve=>setTimeout(resolve,0));
  return {send:(m,s)=>listener(m,s),requests,saved,get state(){return state},advance:()=>time+=1500,setFocused:v=>focused=v,setActive:v=>active=v,setHttpStatus:v=>httpStatus=v};
}
const pageSender={id:'test-extension',frameId:0,tab:{id:1},url:'https://example.test/page'};
const optionsSender={id:'test-extension',tab:{id:99},url:'chrome-extension://test-extension/options.html'};
async function pair(h){return h.send({type:'pair',code:'b'.repeat(16),baseUrl:'http://127.0.0.1:5124'},optionsSender);}
test('pairing is restricted to packaged options and never returns token',async()=>{
 const h=await harness();
 await h.send({type:'pair',code:'b'.repeat(16),baseUrl:'http://127.0.0.1:5124'},pageSender); assert.equal(h.requests.length,0);
 assert.equal((await h.send({type:'pair',code:'b'.repeat(16),baseUrl:'https://evil.example'},optionsSender)).ok,false);
 const result=await pair(h);assert.equal(result.ok,true);assert.equal(result.token,undefined);assert.equal(h.saved.extensionToken,'a'.repeat(64));
});
test('unpaired, inactive, unfocused, stale and child frame reports cannot fetch',async()=>{
 const h=await harness();await h.send({type:'activity'},pageSender);assert.equal(h.requests.length,0);assert.equal(h.state,'unpaired');
 await pair(h);h.requests.length=0;
 await h.send({type:'activity'},{...pageSender,frameId:1});
 await h.send({type:'activity'},{...pageSender,url:'https://evil.example'});
 h.setFocused(false);await h.send({type:'activity'},pageSender);h.setFocused(true);
 h.setActive(false);await h.send({type:'activity'},pageSender);assert.equal(h.requests.length,0);
});
test('trusted tab URL wins over forged content message and token stays in background',async()=>{
 const h=await harness();await pair(h);h.requests.length=0;
 await h.send({type:'activity',host:'evil.example',baseUrl:'https://evil.example',name:'X'.repeat(1000),durationMilliseconds:999999},pageSender);
 assert.equal(h.requests.length,1);const r=h.requests[0];assert.equal(new URL(r.url).origin,'http://127.0.0.1:5124');assert.equal(r.options.headers.Authorization,'Bearer '+'a'.repeat(64));
 const body=JSON.parse(r.options.body);assert.equal(body.host,'example.test');assert.equal(body.name.length,256);assert.equal(body.durationMilliseconds,1500);assert.equal(h.state,'connected');
});
test('HTTP failures cannot become a false connected status',async()=>{
 const h=await harness();await pair(h);h.setHttpStatus(500);await h.send({type:'activity'},pageSender);assert.equal(h.state,'http-error');
 h.advance();h.setHttpStatus(401);await h.send({type:'activity'},pageSender);assert.equal(h.state,'expired');
});

test('Firefox MV2 uses browserAction when action API is absent', async()=> {
  const h=await harness(true);assert.equal((await pair(h)).ok,true);await h.send({type:'activity'},pageSender);assert.equal(h.state,'connected');
});
