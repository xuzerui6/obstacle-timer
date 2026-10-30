import {chromium} from 'playwright';
import {fileURLToPath} from 'node:url';import {tmpdir} from 'node:os';
import {spawn} from 'node:child_process';
import {existsSync,readFileSync,writeFileSync,mkdirSync} from 'node:fs';
import {join} from 'node:path';import assert from 'node:assert/strict';
const root=fileURLToPath(new URL('../../../',import.meta.url)),output=process.env.E2E_OUTPUT??join(tmpdir(),'obstacle-e2e-'+Date.now());mkdirSync(output,{recursive:true});
const dotnet=process.env.DOTNET_PATH??(existsSync(join(root,'.tools/dotnet/dotnet.exe'))?join(root,'.tools/dotnet/dotnet.exe'):'dotnet'),port=process.env.E2E_PORT??String(30000+Math.floor(Math.random()*25000));
const mode=process.env.FORCE_WEBM==='1'?'webm':'mp4',data=join(tmpdir(),'obstacle-qa-'+mode+'-'+Date.now());mkdirSync(data,{recursive:true});
const host=spawn(process.env.E2E_EXECUTABLE??dotnet,[...(process.env.E2E_EXECUTABLE?[]:[join(root,'src/ObstacleTimer.Desktop/bin/Release/net10.0-windows/ObstacleTimer.dll')]),'--headless','--simulate','--data',data,'--port',port],{stdio:['pipe','pipe','pipe'],windowsHide:true});
let incoming='',counter=0;const pending=new Map(),checks=[],errors=[],uploads=[];
host.stdout.setEncoding('utf8');host.stdout.on('data',text=>{incoming+=text;while(incoming.includes('\n')){const end=incoming.indexOf('\n'),line=incoming.slice(0,end);incoming=incoming.slice(end+1);try{const r=JSON.parse(line),p=pending.get(r.id);if(p){pending.delete(r.id);r.ok?p.resolve(r.result):p.reject(new Error(r.error));}}catch{}}});host.stderr.on('data',text=>process.stderr.write(text));
function command(type,body={}){return new Promise((resolve,reject)=>{const id='qa-'+(++counter);pending.set(id,{resolve,reject});host.stdin.write(JSON.stringify({id,type,...body})+'\n');setTimeout(()=>{if(pending.delete(id))reject(Error('Command timeout '+type));},10000).unref();});}
async function until(fn,label,timeout=25000){const end=Date.now()+timeout;while(Date.now()<end){if(await fn())return;await new Promise(r=>setTimeout(r,150));}throw Error('Timeout '+label);}
function pass(name){checks.push(name);console.log('PASS '+name);}
let browser,context,page;
try{
 await until(()=>existsSync(join(data,'headless-pair.json')),'host');const pair=JSON.parse(readFileSync(join(data,'headless-pair.json'),'utf8'));
 browser=await chromium.launch({headless:true,args:['--use-fake-ui-for-media-stream','--use-fake-device-for-media-stream','--autoplay-policy=no-user-gesture-required']});
 context=await browser.newContext({ignoreHTTPSErrors:true,viewport:{width:390,height:844},permissions:['camera']});
 if(process.env.PORTRAIT==='1')await context.addInitScript(()=>{const original=navigator.mediaDevices.getUserMedia.bind(navigator.mediaDevices);navigator.mediaDevices.getUserMedia=c=>original({...c,video:{...c.video,width:{exact:480},height:{exact:854}}});});
 if(process.env.SEED_V2==='1')await context.addInitScript(()=>{
  if(localStorage.getItem('qa-seeded-v2'))return;localStorage.setItem('qa-seeded-v2','1');
  const open=indexedDB.open('obstacle-finish-v1',2);open.onupgradeneeded=()=>{const d=open.result;d.createObjectStore('clips',{keyPath:'id'});d.createObjectStore('chunks',{keyPath:['clipId','index']});d.createObjectStore('outbox',{keyPath:'id'});d.createObjectStore('vision',{keyPath:'id'});};
  open.onsuccess=()=>{const d=open.result,tx=d.transaction(['clips','chunks','outbox','vision'],'readwrite');tx.objectStore('clips').put({id:'legacy-clip',created:1,chunkCount:1,complete:false,registrationPending:true,mime:'video/webm',width:640,height:480,fps:30,run:{athlete:{name:'旧版保留原片'}}});tx.objectStore('chunks').put({clipId:'legacy-clip',index:0,blob:new Blob(['saved-old-chunk'])});tx.objectStore('outbox').put({id:'legacy-review',clipId:'legacy-clip',created:1,payload:{requestId:'immutable-old-review'},complete:false});tx.objectStore('vision').put({id:'legacy-vision',clipId:'legacy-clip',created:1,payload:{requestId:'immutable-old-vision'},complete:false});tx.oncomplete=()=>d.close();};
 });
 if(mode==='webm')await context.addInitScript(()=>{const original=MediaRecorder.isTypeSupported.bind(MediaRecorder);Object.defineProperty(MediaRecorder,'isTypeSupported',{value:type=>!type.startsWith('video/mp4')&&original(type)});});
 await context.addInitScript(()=>{window.__cameraCalls=0;window.__failCameraOnce=false;window.__dropReady=false;if(navigator.mediaDevices){const original=navigator.mediaDevices.getUserMedia.bind(navigator.mediaDevices);navigator.mediaDevices.getUserMedia=(...args)=>{window.__cameraCalls++;if(window.__failCameraOnce){window.__failCameraOnce=false;return Promise.reject(new DOMException('模拟相机权限拒绝','NotAllowedError'));}return original(...args);};}const send=WebSocket.prototype.send;WebSocket.prototype.send=function(data){if(window.__dropReady){try{if(JSON.parse(data).type==='recordingReady'){window.__dropReady=false;return;}}catch{}}return send.call(this,data);};});
 page=await context.newPage();page.on('pageerror',e=>errors.push(e.message));page.on('console',m=>{if(m.type()==='error'&&!m.text().includes('ERR_INTERNET_DISCONNECTED'))errors.push(m.text());});
 page.on('request',r=>{if(r.method()==='POST'&&r.url().includes('/video?'))uploads.push(r.url());});
 await page.goto(pair.https+'/phone/');await page.getByRole('heading',{name:'等待电脑创建组次'}).waitFor();assert.match(await page.title(),/终点相机/);
 assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),true);await page.screenshot({path:join(output,mode+'-initial.png'),fullPage:true});pass('390x844 page identity, meaningful content and no horizontal overflow');
 await page.getByRole('button',{name:'恢复本机录像',exact:true}).click();await page.getByText(process.env.SEED_V2==='1'?'旧版 · 旧版保留原片':'本机还没有录像',{exact:true}).waitFor();await page.getByRole('button',{name:'关闭',exact:true}).click();pass(process.env.SEED_V2==='1'?'v2 saved clip remains in recovery after v3 storage migration':'empty recovery and modal close');
 if(process.env.SEED_V2==='1'){
  const retained=await page.evaluate(()=>new Promise((resolve,reject)=>{const open=indexedDB.open('obstacle-finish-v1',3);open.onsuccess=()=>{const d=open.result,tx=d.transaction(['chunks','outbox','vision']);const a=tx.objectStore('chunks').get(['legacy-clip',0]),b=tx.objectStore('outbox').get('legacy-review'),c=tx.objectStore('vision').get('legacy-vision');tx.oncomplete=async()=>{const result={version:d.version,jobs:d.objectStoreNames.contains('analysisJobs'),raw:await a.result.blob.text(),review:b.result.payload.requestId,visual:c.result.payload.requestId};d.close();resolve(result);};};open.onerror=()=>reject(open.error);}));
  assert.deepEqual(retained,{version:3,jobs:true,raw:'saved-old-chunk',review:'immutable-old-review',visual:'immutable-old-vision'});pass('IndexedDB v2 to v3 retains original chunks and both immutable pending queues');
 }
 await page.setViewportSize({width:768,height:1024});assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),true);await page.screenshot({path:join(output,mode+'-tablet.png'),fullPage:true});await page.setViewportSize({width:390,height:844});pass('768x1024 responsive layout');
 await page.goto(pair.https+'/phone/#secret='+pair.secret);await page.getByText('电脑已连接',{exact:true}).waitFor();pass('v4 QR pairing and WSS state');
 const a=await command('athlete',{bib:'1',name:'集体选手甲'}),b=await command('athlete',{bib:'2',name:'集体选手乙'});
 const outbox=()=>page.evaluate(()=>new Promise((resolve,reject)=>{const request=indexedDB.open('obstacle-finish-v1',3);request.onsuccess=()=>{const db=request.result,r=db.transaction('outbox').objectStore('outbox').getAll();r.onsuccess=()=>{db.close();resolve(r.result.map(x=>({complete:x.complete,runId:x.payload.runId,heatId:x.payload.heatId})));};r.onerror=()=>reject(r.error);};request.onerror=()=>reject(request.error);}));
 async function runHeat(number,offline){
  const h=await command('prepareHeat',{athleteIds:[a.id,b.id]});await page.getByRole('heading',{name:'网页测试组 · 2 人'}).waitFor();
  assert.equal(h.requireRecordingBeforeStart,false);await command('armHeat',{heatId:h.id});const minute=number===1?'00':'01';await command('impulse',{raw:`0001 C0M 12:${minute}:00.00 00`});
  assert.equal(await page.evaluate(()=>window.__cameraCalls),0);assert.equal(await page.evaluate(()=>Array.from(document.querySelectorAll('video')).every(v=>!v.srcObject||!v.srcObject.getTracks().some(t=>t.readyState==='live'))),true);
  pass(`heat ${number}: Timy3 starts before camera permission/request; camera remains off`);
  await page.screenshot({path:join(output,mode+'-before-manual-camera.png'),fullPage:true});
  if(number===1){await page.evaluate(()=>{window.__failCameraOnce=true;});await page.getByRole('button',{name:'开启相机并录像',exact:true}).click();await page.getByRole('status').filter({hasText:'模拟相机权限拒绝'}).waitFor();const failureState=await command('snapshot');assert.equal(failureState.currentHeat.heat.state,'running');assert.equal(failureState.currentHeat.heat.recordingId,'');pass('permission failure leaves hardware race running and permits manual retry');await page.evaluate(()=>{window.__dropReady=true;});}
  await page.getByRole('button',{name:'开启相机并录像',exact:true}).click();
  await until(async()=>{const state=await command('snapshot');return state.currentHeat.heat.state==='running'&&state.currentHeat.heat.recordingId.length>0;},'late recording acknowledgment');
  pass(`heat ${number}: one manual click opens and records after C0 without resetting race`);
  if(process.env.PORTRAIT==='1'){const state=await command('snapshot');assert.ok(state.currentHeat.heat.capture.height>state.currentHeat.heat.capture.width);pass(`heat ${number}: portrait camera dimensions remain portrait in raw metadata`);}
  await new Promise(r=>setTimeout(r,700));let s=await command('impulse',{raw:`0001 C1M 12:${minute}:01.70 00`});
  await page.locator('.recording-indicator').waitFor();assert.equal(await page.getByRole('button',{name:'结束整组录像',exact:true}).isVisible(),true);pass(`heat ${number}: first finish does not stop group recording`);
  await new Promise(r=>setTimeout(r,700));s=await command('impulse',{raw:`0002 C1M 12:${minute}:02.11 00`});
  const runs=s.currentHeat.runs,finishes=s.currentHeat.heat.finishes;
  await command('assign',{heatId:h.id,eventId:finishes[0].id,runId:runs[1].id});await command('assign',{heatId:h.id,eventId:finishes[1].id,runId:runs[0].id});
  const beforeSource=await command('snapshot');assert.ok(beforeSource.currentHeat.runs.every(r=>r.elapsedUs==null&&r.backupElapsedUs>0));pass('C1 binding alone produces no official camera score');
  for(const run of runs)await command('selectSource',{heatId:h.id,runId:run.id,source:'photocell-backup'});
  await until(()=>page.locator('[aria-label="本组设备成绩"]').innerText().then(t=>t.includes('1.70 s')&&t.includes('2.11 s')),'device scores');
  if(offline){await context.setOffline(true);await until(async()=>!(await page.locator('.connection').innerText()).includes('电脑已连接'),'offline WebSocket');}
  await page.getByRole('button',{name:'结束整组录像',exact:true}).click();await page.getByRole('combobox',{name:'截图所属选手'}).waitFor();await until(()=>page.locator('.frame-label').innerText().then(t=>/帧 1 \/ [1-9]/.test(t)),'presented frame');
  await page.getByRole('button',{name:'下一帧',exact:true}).click();await until(()=>page.locator('.frame-label').innerText().then(t=>t.startsWith('帧 2 /')),'next actual frame');
  await page.screenshot({path:join(output,`${mode}-heat-${number}-review.png`),fullPage:true});
  const initialUploads=uploads.length;
  for(const run of runs){await page.getByRole('combobox',{name:'截图所属选手'}).selectOption(run.id);await page.getByRole('button',{name:'保存此人的过线证据',exact:true}).click();}
  pass(`heat ${number}: shared clip, independent per-athlete frame reviews`);
  if(offline){await until(async()=>{const rows=await outbox();return rows.filter(x=>x.heatId===h.id&&!x.complete).length===2;},'two offline reviews');pass('two offline reviews retained durably');await context.setOffline(false);await page.getByText('电脑已连接',{exact:true}).waitFor();}
  await until(async()=>{const rows=await outbox();return rows.filter(x=>x.heatId===h.id&&x.complete).length===2;},'both screenshots and original acknowledged',35000);
  assert.equal(uploads.length-initialUploads,1);pass(`heat ${number}: both screenshots acknowledged, shared original uploaded once`);
  const final=await command('snapshot');assert.deepEqual(final.currentHeat.runs.map(r=>r.elapsedUs),[2110000,1700000]);assert.deepEqual(final.currentHeat.runs.map(r=>r.revision),[1,1]);assert.deepEqual(final.currentHeat.runs.map(r=>r.reviews.length),[1,1]);pass(`heat ${number}: video evidence cannot change hardware time or timing revisions`);
  await command('closeHeat',{heatId:h.id});return h;
 }
 await page.getByText('本地躯干模型就绪 · WASM',{exact:true}).waitFor({timeout:90000});assert.equal(await page.evaluate(()=>window.__cameraCalls),0);pass('local BodyPix model preloads without opening camera');
 const first=await runHeat(1,false);
 await page.reload();await page.getByRole('button',{name:'恢复本机录像',exact:true}).click();await page.getByRole('button',{name:/网页测试组/}).click();await page.getByRole('combobox',{name:'截图所属选手'}).waitFor();await until(()=>page.locator('.frame-label').innerText().then(t=>t.startsWith('帧 1 /')),'reload frame index');pass('page reload restores one group clip and both athlete reviews');
 await page.getByRole('button',{name:'返回相机，准备下一组',exact:true}).click();await runHeat(2,true);
 assert.deepEqual(errors,[]);pass('no unexpected console errors or unhandled page errors');
 writeFileSync(join(output,mode+'-qa-results.json'),JSON.stringify({browser:'Playwright Chromium; Browser plugin not available',mode,syntheticCamera:true,physicalPhoneValidated:false,viewports:['390x844','768x1024'],checks,data},null,2));
 console.log(`${checks.length} checks passed (${mode})`);
}catch(error){writeFileSync(join(output,mode+'-failure.json'),JSON.stringify({error:String(error),checks,errors,data},null,2));if(page)await page.screenshot({path:join(output,mode+'-failure.png'),fullPage:true}).catch(()=>{});console.error(error);process.exitCode=1;}
finally{await context?.close();await browser?.close();try{await command('shutdown');}catch{}host.kill();}
