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
 page.on('response',async r=>{if(r.status()>=400)console.error('HTTP_FAILURE',r.status(),r.url(),await r.text());});page.on('pageerror',e=>errors.push(e.message));page.on('console',m=>{if(m.type()==='error')errors.push(m.text());});page.on('dialog',d=>d.accept());
 await page.route('**/qa-person.jpg',r=>r.fulfill({contentType:'image/jpeg',body:readFileSync(process.env.E2E_FIXTURE??join(root,'.tools/e2e-fixtures/person.jpg'))}));
 await page.addInitScript(()=>{
  navigator.mediaDevices.getUserMedia=async()=>{
   const bitmap=await createImageBitmap(await (await fetch('/qa-person.jpg')).blob());const canvas=document.createElement('canvas');canvas.width=480;canvas.height=480;const ctx=canvas.getContext('2d');const origin=performance.now();
   function draw(){ctx.fillStyle='#bcbcbc';ctx.fillRect(0,0,480,480);const x=20+Math.max(0,Math.min(230,(performance.now()-origin-2000)/100));ctx.drawImage(bitmap,690,0,362,861,x,15,190,450);requestAnimationFrame(draw);}draw();return canvas.captureStream(15);
  };
 });
 await page.goto(pair.https+'/phone/#secret='+pair.secret);await page.getByText('本地躯干模型就绪 · WASM',{exact:true}).waitFor({timeout:90000});
 const a=await command('athlete',{bib:'100',name:'自动候选链路回归'});const h=await command('prepareHeat',{athleteIds:[a.id]});await command('armHeat',{heatId:h.id});await command('impulse',{raw:'0001 C0 12:00:00.00 00'});
 await page.getByLabel('手机已固定在终点线延长线上侧拍').check();await page.getByRole('button',{name:'开启相机并录像',exact:true}).click();
 await until(async()=>{const s=await command('snapshot');return s.currentHeat.heat.visionEvents.length>0;},'real BodyPix crossing through complete phone/store/HTTP pipeline',90000);
 await until(async()=>{const state=await command('snapshot');return state.visionDiagnostics.some(d=>d.analyzed>0&&d.generated>0);},'diagnostics after candidate persistence',10000);
 const state=await command('snapshot'),v=state.currentHeat.heat.visionEvents[0];assert.equal(v.eligible,false);assert.ok(v.reasons.some(r=>r.includes('100次')));assert.equal(state.currentHeat.runs[0].elapsedUs,null);
 assert.ok(state.visionDiagnostics[0].analyzed>0&&state.visionDiagnostics[0].generated>0);
 pass('actual BodyPix masks from translated official sample create persisted phone candidate accepted by server');
 pass('empty queue diagnostics report analyzed frames, visible torsos and saved candidate counters');
 pass('unvalidated visual candidate displays without becoming official score');
 await page.screenshot({path:join(output,'actual-candidate-phone.png'),fullPage:true});
 await command('closeForReview',{heatId:h.id});await page.getByRole('combobox',{name:'截图所属选手'}).waitFor({timeout:20000});
 const closed=await command('snapshot');assert.equal(closed.currentHeat.heat.state,'closed');assert.equal(closed.currentHeat.heat.closedWithPending,true);assert.ok(closed.currentHeat.heat.visionEvents.length>0);assert.equal(closed.currentHeat.runs[0].elapsedUs,null);
 pass('ending group retains unmatched visual event and tells phone to stop recording without deleting clip');
 await until(()=>page.locator('.frame-label').innerText().then(t=>t.startsWith('帧 1 /')),'original indexed after stop');
 const records=()=>page.evaluate(()=>new Promise((resolve,reject)=>{const open=indexedDB.open('obstacle-finish-v1',3);open.onsuccess=()=>{const db=open.result,tx=db.transaction(['vision','analysisJobs','clips']),result={};let remaining=3;for(const name of ['vision','analysisJobs','clips']){const r=tx.objectStore(name).getAll();r.onsuccess=()=>{result[name]=r.result.map(v=>name==='vision'?{id:v.id,payload:v.payload,complete:v.complete}:v);if(!--remaining){db.close();resolve(result);}};r.onerror=()=>reject(r.error);}};open.onerror=()=>reject(open.error);}));
 const cameraCalls=await page.evaluate(()=>navigator.mediaDevices.getUserMedia.toString());
 await page.getByText('重新检测原片（不打开相机）',{exact:true}).click();
 await page.getByRole('button',{name:'开始原片补检',exact:true}).click();
 await until(async()=>{const r=await records();return r.analysisJobs.some(j=>j.state==='running'&&j.processed>=3);},'offline analysis advances actual PTS',30000);
 await page.getByRole('button',{name:'暂停补检',exact:true}).click();
 const paused=(await records()).analysisJobs.at(-1);assert.equal(paused.state,'paused');assert.ok(paused.cursorUs>=0);
 await page.reload();await page.getByRole('button',{name:'继续补检',exact:true}).waitFor();
 assert.ok((await records()).analysisJobs.some(j=>j.id===paused.id&&j.cursorUs===paused.cursorUs));
 await page.getByRole('button',{name:'继续补检',exact:true}).click();
 await until(async()=>{const r=await records();return r.analysisJobs.some(j=>j.id===paused.id&&j.state==='complete');},'reloaded offline job resumes without camera',120000);
 const firstScan=(await records()).vision.filter(v=>v.payload.origin==='offline');assert.ok(firstScan.length>0);assert.ok(firstScan.every(v=>v.payload.clock===null&&v.payload.originalFramePtsUs>=0));
 await until(async()=>{const s=await command('snapshot');return s.heats.find(x=>x.id===h.id).visionEvents.some(v=>v.submission.origin==='offline');},'offline candidate reaches PC',25000);
 const afterScan=await command('snapshot');assert.ok(afterScan.heats.find(x=>x.id===h.id).visionEvents.filter(v=>v.submission.origin==='offline').every(v=>v.estimatedElapsedUs==null&&!v.eligible));assert.equal(afterScan.runs[0].elapsedUs,null);
 pass('original reanalysis uses real BodyPix per actual PTS, pause/checkpoint/reload resume; missing clock never creates time');
 await page.getByRole('button',{name:'恢复本机录像',exact:true}).click();await page.getByRole('button',{name:/网页测试组/}).click();
 await until(()=>page.locator('.frame-label').innerText().then(t=>t.startsWith('帧 1 /')),'reload original');
 await page.getByText('重新检测原片（不打开相机）',{exact:true}).click();await page.getByRole('button',{name:'开始原片补检',exact:true}).click();
 await until(async()=>{const r=await records();return r.analysisJobs.some(j=>j.state==='running');},'second scan starts');
 const next=await command('prepareHeat',{athleteIds:[a.id]});await command('armHeat',{heatId:next.id});await command('impulse',{raw:'0002 C0 12:01:00.00 00'});
 await page.getByRole('button',{name:'返回相机，准备下一组',exact:true}).click();await page.getByRole('button',{name:'开启相机并录像',exact:true}).click();
 await until(async()=>{const r=await records();return r.analysisJobs.some(j=>j.state==='paused'&&j.pauseReason==='live');},'live recording preempts offline analysis');
 await command('closeForReview',{heatId:next.id});
 await until(async()=>{const r=await records();return r.analysisJobs.every(j=>j.state==='complete');},'old clip resumes after new recording finishes',120000);
 assert.deepEqual((await records()).vision.filter(v=>v.payload.origin==='offline').map(v=>v.id).sort(),firstScan.map(v=>v.id).sort());
 pass('new group recording automatically pauses background reanalysis; rescan resumes idempotently without mixing recordings');
 await page.getByRole('button',{name:'恢复本机录像',exact:true}).click();const choices=page.getByRole('button',{name:/网页测试组/});await choices.last().click();
 await until(()=>page.locator('.frame-label').innerText().then(t=>t.startsWith('帧 1 /')),'manual original');await page.getByRole('button',{name:'下一帧',exact:true}).click();
 await until(()=>page.locator('.frame-label').innerText().then(t=>t.startsWith('帧 2 /')),'manual frame');
 for(let repeat=0;repeat<2;repeat++)await page.getByRole('button',{name:'将此帧保存为人工候选（待复核）',exact:true}).click();
 await until(async()=>{const r=await records();return r.vision.some(v=>v.payload.origin==='manual');},'manual candidate persists');
 const manual=(await records()).vision.filter(v=>v.payload.origin==='manual');assert.equal(manual.length,1);assert.equal(manual[0].payload.clock,null);
 assert.equal(await page.evaluate(()=>navigator.mediaDevices.getUserMedia.toString()),cameraCalls);
 pass('manual frame candidate is immutable and idempotent; original stays in IndexedDB after pairing/reload');
 assert.deepEqual(errors,[]);pass('no unhandled page or console errors in candidate generation flow');
 writeFileSync(join(output,'actual-candidate-results.json'),JSON.stringify({checks,simulation:true,physicalAcceptance:false,state:closed.currentHeat.heat,diagnostics:closed.visionDiagnostics,errors},null,2));
}catch(error){console.error(error);if(page){console.error(await page.locator('main').innerText());await page.screenshot({path:join(output,'actual-candidate-failure.png'),fullPage:true});}process.exitCode=1;}
finally{await context?.close();await browser?.close();try{await command('shutdown');}catch{}host.kill();}
