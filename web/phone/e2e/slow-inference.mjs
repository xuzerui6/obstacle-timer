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
 browser=await chromium.launch({headless:true,args:['--autoplay-policy=no-user-gesture-required']});context=await browser.newContext({ignoreHTTPSErrors:true,viewport:{width:390,height:844}});page=await context.newPage();
 page.on('pageerror',e=>errors.push(e.message));page.on('console',m=>{if(m.type()==='error')errors.push(m.text());});page.on('dialog',d=>d.accept());
 await page.route('**/qa-person.jpg',r=>r.fulfill({contentType:'image/jpeg',body:readFileSync(process.env.E2E_FIXTURE??join(root,'.tools/e2e-fixtures/person.jpg'))}));
 await page.addInitScript(()=>{
  const NativeWorker=window.Worker;
  window.__modelErrors=0;window.__forceModelErrors=false;
  window.Worker=class {onmessage=null;onerror=null;constructor(...args){this.inner=new NativeWorker(...args);this.inner.onmessage=e=>setTimeout(()=>this.onmessage?.(e),e.data.type==='analyzed'?1200:0);this.inner.onerror=e=>this.onerror?.(e);}postMessage(data,...rest){if(data.type==='frame'&&window.__forceModelErrors){window.__modelErrors++;queueMicrotask(()=>this.onmessage?.({data:{type:'error',message:'QA 模型故障',requestId:data.requestId,sessionId:data.sessionId}}));}else this.inner.postMessage(data,...rest);}terminate(){this.inner.terminate();}};
  navigator.mediaDevices.getUserMedia=async()=>{
   const bitmap=await createImageBitmap(await (await fetch('/qa-person.jpg')).blob());const canvas=document.createElement('canvas');canvas.width=480;canvas.height=480;const ctx=canvas.getContext('2d');const origin=performance.now();
   function draw(){ctx.fillStyle='#bcbcbc';ctx.fillRect(0,0,480,480);const x=20+Math.max(0,Math.min(230,(performance.now()-origin-2000)/100));ctx.drawImage(bitmap,690,0,362,861,x,15,190,450);requestAnimationFrame(draw);}draw();return canvas.captureStream(15);
  };
 });
 await page.goto(pair.https+'/phone/#secret='+pair.secret);await page.getByText('本地躯干模型就绪 · WASM',{exact:true}).waitFor({timeout:90000});
 const a=await command('athlete',{bib:'100',name:'自动候选链路回归'});const h=await command('prepareHeat',{athleteIds:[a.id]});await command('armHeat',{heatId:h.id});await command('impulse',{raw:'0001 C0 12:00:00.00 00'});
 await page.getByLabel('手机已固定在终点线延长线上侧拍').check();await page.getByRole('button',{name:'开启相机并录像',exact:true}).click();
 await until(async()=>{const state=await command('snapshot'),d=state.visionDiagnostics[0];return d?.captured>100&&d?.analyzed>=3;},'slow inference telemetry',30000);
 const slow=await command('snapshot'),d=slow.visionDiagnostics[0];assert.ok(d.queued<=1);assert.ok(d.captured>d.analyzed*5);assert.equal(slow.currentHeat.heat.recordingLive,true);
 assert.ok(!(await page.locator('main').innerText()).includes('缓冲区过载'));
 pass('1.2-second delayed inference keeps latest-frame queue <=1 and raw video recording alive');
 await page.evaluate(()=>window.__forceModelErrors=true);
 await page.getByRole('note').filter({hasText:'检测已暂停，请重试模型'}).waitFor({timeout:30000});
 assert.equal(await page.evaluate(()=>window.__modelErrors),3);
 const beforeRecovery=await command('snapshot');assert.equal(beforeRecovery.currentHeat.heat.recordingLive,true);assert.equal(beforeRecovery.currentHeat.heat.recordingStopped,false);
 pass('model errors stop after two automatic retries while raw video keeps recording');
 await page.evaluate(()=>window.__forceModelErrors=false);await page.getByRole('button',{name:'重试模型（不停止录像）',exact:true}).click();
 await until(async()=>{const state=await command('snapshot');return state.visionDiagnostics.some(v=>v.analyzed>d.analyzed&&v.lastError==='');},'manual model recovery',30000);
 assert.equal((await command('snapshot')).currentHeat.heat.recordingId,beforeRecovery.currentHeat.heat.recordingId);
 pass('manual retry recovers the model without replacing or restarting the group original');
 const v={eligible:false};
 await command('closeForReview',{heatId:h.id});await page.getByRole('combobox',{name:'截图所属选手'}).waitFor({timeout:20000});
 const closed=await command('snapshot');assert.equal(closed.currentHeat.heat.state,'closed');assert.equal(closed.currentHeat.heat.closedWithPending,true);assert.equal(closed.currentHeat.runs[0].elapsedUs,null);
 pass('ending group retains unmatched visual event and tells phone to stop recording without deleting clip');
 assert.deepEqual(errors,[]);pass('no unhandled page or console errors in candidate generation flow');
 writeFileSync(join(output,'slow-inference-results.json'),JSON.stringify({checks,simulation:true,physicalAcceptance:false,state:closed.currentHeat.heat,diagnostics:closed.visionDiagnostics,errors},null,2));
}catch(error){console.error(error);if(page){console.error(await page.locator('main').innerText());await page.screenshot({path:join(output,'slow-inference-failure.png'),fullPage:true});}process.exitCode=1;}
finally{await context?.close();await browser?.close();try{await command('shutdown');}catch{}host.kill();}
