// Integration checks use a separate simulated bridge and a disposable local account.
import assert from 'node:assert/strict';
import {spawn} from 'node:child_process';
import http from 'node:http';
import net from 'node:net';
import {readFile, mkdtemp, rm} from 'node:fs/promises';
import {existsSync} from 'node:fs';
import {tmpdir} from 'node:os';
import path from 'node:path';
const root=path.resolve(import.meta.dirname,'..');
const external=process.argv.includes('--existing');
const port=Number(process.env.F1_HUE_TEST_PORT??(external?8091:18091));
const data=external?(process.env.F1_HUE_TEST_DATA??path.join(root,'.runtime/qa2')):await mkdtemp(path.join(tmpdir(),'f1hue-api-'));
const base=`http://127.0.0.1:${port}`;
const dotnet=existsSync(path.join(root,'.tools/dotnet/dotnet'))?path.join(root,'.tools/dotnet/dotnet'):'dotnet';
const build=process.env.F1_HUE_TEST_BUILD??'Debug';
const executable=process.env.F1_HUE_TEST_EXECUTABLE??dotnet;
const prefix=process.env.F1_HUE_TEST_EXECUTABLE?[]:[path.join(root,`apps/host/bin/${build}/net10.0/f1-hue.dll`)];
const child=external?null:spawn(executable,[...prefix,'--simulate','--data',data,'--port',String(port)],{stdio:['ignore','pipe','pipe']});
let logs='';child?.stdout.on('data',b=>logs+=b);child?.stderr.on('data',b=>logs+=b);
let cookie='';let checks=0;
function check(value,label){assert.ok(value,label);checks++;console.log('PASS '+label);}
async function req(url,method='GET',body,expected=200,headers={}){
 const r=await fetch(base+url,{method,signal:AbortSignal.timeout(10000),headers:{'X-F1Hue-Request':'1',...(body===undefined?{}:{'Content-Type':'application/json'}),...(cookie?{Cookie:cookie}:{}),...headers},body:body===undefined?undefined:JSON.stringify(body)});
 assert.equal(r.status,expected,`${method} ${url}: ${r.status} ${await (r.status!==expected?r.text():Promise.resolve(''))}`);
 const token=r.headers.get('set-cookie');if(token)cookie=token.split(';')[0];
 return {r,body:await r.json().catch(()=>null)};
}
try{
 for(let i=0;;i++){try{const r=await fetch(base+'/health');if(r.ok)break;}catch{}if(i>100)throw new Error('Service did not start: '+logs);await new Promise(r=>setTimeout(r,100));}
 await req('/api/state','GET',undefined,401);check(true,'Anonymous callers cannot read state');
 await req('/api/settings','PATCH',{},403,{'X-F1Hue-Request':''});check(true,'Unsafe requests require custom anti-CSRF header');
 await req('/api/auth/status','GET',undefined,403,{Origin:'https://evil.example'});check(true,'Cross-origin requests rejected');
 const badHost=await new Promise((resolve,reject)=>{const request=http.request(base+'/health',{headers:{Host:'attacker.example'}},r=>{r.resume();resolve(r.statusCode);});request.on('error',reject);request.end();});check(badHost===403,'Unknown Host rejected against DNS rebinding');
 const status=(await req('/api/auth/status')).body;
 if(status.setupRequired){const code=(await readFile(path.join(data,'setup-code.txt'),'utf8')).trim();await req('/api/auth/setup','POST',{code,password:'local-qa-password-123'});}else await req('/api/auth/login','POST',{password:'local-qa-password-123'});
 check(cookie.startsWith('f1hue_session='),'Setup/login creates a session');
 const health=(await req('/health')).r;check(health.headers.get('content-security-policy').includes("frame-ancestors 'none'")&&health.headers.get('x-content-type-options')==='nosniff','Security headers present');
 let state=(await req('/api/state')).body;check(state.simulation&&!JSON.stringify(state).includes('applicationKey'),'State identifies simulation and exposes no Hue key');
 await req('/api/stop','POST');
 await req('/api/hue/discover','POST');await req('/api/hue/pair','POST',{ip:'192.168.1.10'});const inventory=(await req('/api/hue/inventory')).body;
 await req('/api/hue/select','POST',{lightIds:[],groupIds:[],entertainmentAreaId:null},400);
 await req('/api/hue/select','POST',{lightIds:['missing'],groupIds:[],entertainmentAreaId:null},400);
 await req('/api/hue/select','POST',{lightIds:[],groupIds:[inventory.groups[0].id],entertainmentAreaId:null});check(true,'Pairing and group selection routes work; invalid selection rejected');
 await req('/api/hue/import-selection','POST');
 await req('/api/settings','PATCH',{username:'bad'},400);
 await req('/api/settings','PATCH',{effects:{BLUE:{enabled:true,durationSeconds:.1}},offsetSeconds:0});
 const settings=(await req('/api/settings')).body;check(settings.effects.BLUE.enabled&&settings.effects.BLUE.durationSeconds===.1,'Settings patch saves validated values');
 await req('/api/test/preview','POST',{flag:'PURPLE'},400);
 await req('/api/test/preview','POST',{flag:'RED'});
 await req('/api/hue/select','POST',{lightIds:[inventory.lights[0].id],groupIds:[],entertainmentAreaId:null},409);
 await req('/api/test/sequence','POST',undefined,409);
 await req('/api/stop','POST');check(true,'Preview owns targets; conflicting modes and target changes rejected');
 for(let i=0;i<3;i++){
  await req('/api/test/preview','POST',{flag:'SC'});await req('/api/stop','POST');
  state=(await req('/api/state')).body;
  assert.ok(!state.runner.running&&!state.runner.cleanupPending&&!state.runner.stopping);
 }
 check(true,'Repeated Safety Car previews stop cleanly through the HTTP API');
 // A slow, unauthenticated login must not hold the lamp command gate.
 await req('/api/test/preview','POST',{flag:'SC'});
 const slowLogin=net.createConnection({host:'127.0.0.1',port});
 slowLogin.on('error',()=>{});
 await new Promise(resolve=>slowLogin.once('connect',resolve));
 slowLogin.write('POST /api/auth/login HTTP/1.1\r\nHost: 127.0.0.1:'+port+'\r\nX-F1Hue-Request: 1\r\nContent-Type: application/json\r\nContent-Length: 1000\r\n\r\n{');
 try {
  await new Promise(resolve=>setTimeout(resolve,100));
  const before=Date.now();await req('/api/stop','POST');
  check(Date.now()-before<1500&&!(await req('/api/state')).body.runner.running,'Slow anonymous login cannot delay Stop');
 } finally {slowLogin.destroy();}
 // A partially uploaded start received before Stop cannot start once its body arrives.
 const payload=JSON.stringify({flag:'SC'});
 const slowStart=net.createConnection({host:'127.0.0.1',port});slowStart.on('error',()=>{});
 let response='';slowStart.on('data',chunk=>response+=chunk);
 await new Promise(resolve=>slowStart.once('connect',resolve));
 slowStart.write('POST /api/test/preview HTTP/1.1\r\nHost: 127.0.0.1:'+port+'\r\nCookie: '+cookie+'\r\nX-F1Hue-Request: 1\r\nContent-Type: application/json\r\nConnection: close\r\nContent-Length: '+Buffer.byteLength(payload)+'\r\n\r\n'+payload[0]);
 try {
  await new Promise(resolve=>setTimeout(resolve,100));await req('/api/stop','POST');
  slowStart.write(payload.slice(1));
  await new Promise((resolve,reject)=>{slowStart.once('close',resolve);setTimeout(()=>reject(new Error('Slow start response timed out')),3000).unref();});
  check(response.includes('409')&&!(await req('/api/state')).body.runner.running,'Stop invalidates starts still uploading their body');
 } finally {slowStart.destroy();}
 if(!external){
  await req('/api/test/preview','POST',{flag:'SC'});
  const duplicate=spawn(executable,[...prefix,'--simulate','--data',data,'--port',String(port+1)],{stdio:'ignore'});
  const code=await new Promise((resolve,reject)=>{duplicate.once('exit',resolve);setTimeout(()=>{duplicate.kill();reject(new Error('Duplicate instance did not refuse ownership'));},5000).unref();});
  check(code===2&&(await req('/api/state')).body.runner.activeEffect==='SC','A second service refuses the occupied profile before touching the first effect');
  await req('/api/stop','POST');
 }
 await req('/api/test/sequence','POST');await req('/api/stop','POST');
 await req('/api/live/start','POST');await req('/api/settings','PATCH',{effects:{GREEN:{enabled:false}}});
 const send=(topic,payload)=>req('/api/simulation/event','POST',{topic,payload});
 await send('SessionInfo',{Key:9001,Name:'Course de test',Type:'Race'});await send('SessionStatus',{Status:'Started'});
 await send('TrackStatus',{Status:'1'});await send('TrackStatus',{Status:'5'});
 state=(await req('/api/state')).body;check(state.runner.lastFlag==='RED','Live mode receives decoded events');
 await req('/api/calibration/arm','POST',{mode:'lap'});await send('LapCount',{CurrentLap:2,TotalLaps:53});await req('/api/calibration/seen','POST');
 await send('ExtrapolatedClock',{Utc:new Date().toISOString(),Remaining:'00:10:00',Extrapolating:true});await req('/api/calibration/clock','POST',{remaining:'11:00'});await req('/api/calibration/cancel','POST');check(true,'Lap and countdown calibration routes succeed');
 await req('/api/calibration/arm','POST',{mode:'invalid'},400);
 await req('/api/calibration/clock','POST',{remaining:'bad'},400);
 const controller=new AbortController();const sse=await fetch(base+'/api/events',{headers:{Cookie:cookie},signal:controller.signal});const reader=sse.body.getReader();const event=new TextDecoder().decode((await reader.read()).value);check(event.startsWith('data: '),'Authenticated SSE delivers state');controller.abort();
 await req('/api/stop','POST');
 const journal=(await req('/api/journal')).body;check(journal.some(e=>e.value==='RED'&&e.receivedAt),'Journal contains timestamped live flag');
 await req('/api/replay/scenarios');await req('/api/replay/start','POST',{kind:'local',id:'9001',speed:60});await req('/api/stop','POST');
 await req('/api/replay/start','POST',{kind:'archive',id:'https://evil.example',speed:1},400);check(true,'Local replay works and archive URL injection is rejected');
 if(process.argv.includes('--network')){await req('/api/replay/start','POST',{kind:'archive',id:'baku-qualifying-2025',speed:100});await req('/api/stop','POST');check(true,'Official archive downloads and starts through new .NET replay');}
 await req('/api/does-not-exist','GET',undefined,404);
 await req('/api/hue/unpair','POST');await req('/api/hue/select','POST',{lightIds:[inventory.lights[0].id],groupIds:[],entertainmentAreaId:null});
 await req('/api/auth/logout','POST');await req('/api/state','GET',undefined,401);check(true,'Logout revokes session access');
 console.log(`\n${checks} API checks passed.`);
}finally{
 if(child){child.kill('SIGTERM');await new Promise(resolve=>{child.once('exit',resolve);setTimeout(resolve,10000).unref();});await rm(data,{recursive:true,force:true});}
}
