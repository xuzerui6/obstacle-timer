import {useEffect,useRef,useState,useSyncExternalStore,type PointerEvent as ReactPointerEvent} from 'react';
import {vision,defaultVision} from './vision';
import {link} from './link';import {CameraCapture} from './capture';import {frameTimes,presentedFrame} from './frame-index';
import {listClips,listOutbox,saveClip,saveOutbox,videoBlob} from './storage';import {id,allCaptured,formatTime,type Clip,type Submission,type HeatView} from './types';
function CameraIcon(){return <svg aria-hidden="true" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.6"><path d="M4 7h4l2-3h4l2 3h4v13H4z"/><circle cx="12" cy="13" r="4"/></svg>;}
export default function App(){
 const connection=useSyncExternalStore(link.subscribe,link.snapshot);const modelStatus=useSyncExternalStore(vision.subscribe,vision.snapshot);const [detection,setDetection]=useState(defaultVision);const [phase,setPhase]=useState('idle');const [,render]=useState(0);
 const [clip,setClip]=useState<Clip>();const clipRef=useRef<Clip|undefined>(undefined);const [movieUrl,setMovieUrl]=useState('');const urlRef=useRef('');
 const live=useRef<HTMLVideoElement>(null),movie=useRef<HTMLVideoElement>(null),stage=useRef<HTMLDivElement>(null);
 const [dimensions,setDimensions]=useState({width:350,height:262.5}),[frames,setFrames]=useState<number[]>([]),[index,setIndex]=useState(0);
 const [selected,setSelected]=useState<{mediaUs:number;image:Blob}>(),[busy,setBusy]=useState(false);const generation=useRef(0),initializedMovie=useRef('');
 const [line,setLine]=useState(.5),[zoom,setZoom]=useState(1),[pan,setPan]=useState({x:0,y:0});const pointer=useRef<{x:number;y:number}|undefined>(undefined);
 const [modal,setModal]=useState<'settings'|'recovery'|null>(null),[savedClips,setSavedClips]=useState<Clip[]>([]),[secret,setSecret]=useState(''),[runnerId,setRunnerId]=useState('');const dialog=useRef<HTMLDialogElement>(null);
 const camera=useRef<CameraCapture|null>(null);
 if(!camera.current)camera.current=new CameraCapture(next=>{setPhase(next);if(next==='review'&&camera.current?.clip)void loadClip(camera.current.clip).catch(report);},()=>render(v=>v+1));
 const active=connection.snapshot?.currentHeat,recording=phase==='recording',reviewing=phase==='review';
 const group=reviewing?clip?.view:active;const settings=camera.current.settings();
 useEffect(()=>{if(connection.connected)void vision.load().catch(report);},[connection.connected]);
 function report(error:unknown){link.notify(error instanceof Error?error.message:String(error));setBusy(false);}
 useEffect(()=>{void link.initialize().catch(report);const hidden=()=>{if(document.hidden)camera.current?.stop(true);};const leaving=(e:BeforeUnloadEvent)=>{if(camera.current?.recorder?.state==='recording'){e.preventDefault();e.returnValue='';}};
  document.addEventListener('visibilitychange',hidden);window.addEventListener('beforeunload',leaving);return()=>{document.removeEventListener('visibilitychange',hidden);window.removeEventListener('beforeunload',leaving);};
 },[]);
 useEffect(()=>{if(!active)return;const current=camera.current?.clip;if(current?.heatId===active.heat.id&&camera.current?.recorder?.state==='recording'){current.view=structuredClone(active);if(active.heat.recordingId===current.id)current.registrationPending=false;void saveClip(current).catch(report);if(active.heat.state==='closed')camera.current?.stop(true);}
  if(clipRef.current?.heatId===active.heat.id){const next={...clipRef.current,view:structuredClone(active)};clipRef.current=next;setClip(next);}
 },[active]);
 useEffect(()=>{if(!stage.current)return;const observer=new ResizeObserver(entries=>{const r=entries[0].contentRect;setDimensions({width:r.width,height:r.height});});observer.observe(stage.current);return()=>observer.disconnect();},[]);
 useEffect(()=>{if(modal)dialog.current?.showModal();else dialog.current?.close();},[modal]);
 useEffect(()=>()=>{if(urlRef.current)URL.revokeObjectURL(urlRef.current);},[]);
 async function loadClip(next:Clip){
  setBusy(true);setSelected(undefined);setFrames([]);setZoom(1);setPan({x:0,y:0});
  if(next.heatId&&connection.connected){try{next.view=await link.api(`/api/heats/${next.heatId}`) as HeatView;}catch{/* Offline snapshot remains available. */}}
  clipRef.current=next;setClip(next);setRunnerId(next.view?.runs[0]?.id??'');setPhase('review');
  const blob=await videoBlob(next);if(urlRef.current)URL.revokeObjectURL(urlRef.current);const url=URL.createObjectURL(blob);urlRef.current=url;setMovieUrl(url);
  const times=await frameTimes(blob);setFrames(times);setIndex(0);setBusy(false);link.notify(`已建立 ${times.length} 个视频时间点；选帧更正需重新核验帧时间映射`);
 }
 async function seek(frame:number){
  if(!movie.current||!frames.length)return;const target=Math.max(0,Math.min(frame,frames.length-1));setSelected(undefined);setBusy(true);setIndex(target);const ticket=++generation.current;
  try{const actual=await presentedFrame(movie.current,frames[target]);if(ticket!==generation.current)return;const t=actual.mediaUs/1e6;const nearest=frames.reduce((best,value,i)=>Math.abs(value-t)<Math.abs(frames[best]-t)?i:best,0);
   if(Math.abs(frames[nearest]-t)>.1)throw new Error('呈现帧与索引不一致，请重试');setIndex(nearest);setSelected(actual);
  }catch(error){if(ticket===generation.current)report(error);}finally{if(ticket===generation.current)setBusy(false);}
 }
 function initializeMovie(){if(movieUrl&&frames.length&&movie.current&&movie.current.readyState>=2&&initializedMovie.current!==movieUrl){initializedMovie.current=movieUrl;void seek(0);}}
 useEffect(()=>{initializeMovie();},[movieUrl,frames.length]);
 async function startManual(){
  if(busy||recording)return;
  try{
   if(!active)throw new Error('请先在电脑创建组次');
   if(!connection.connected)throw new Error('请先恢复电脑连接，再手动开启本组录像');
   if(active.heat.state==='closed'||active.heat.recordingStopped)throw new Error('本组已结束录像，请恢复原片或新建组次');
   if((await listClips()).some(c=>c.heatId===active.heat.id))throw new Error('本组已有原片，请使用恢复本机录像，不会覆盖');
   setBusy(true);if(!camera.current!.stream)await camera.current!.open(live.current!);await camera.current!.start(active,live.current!,{...detection,lineX:line});
  }catch(error){camera.current!.closePreview();if(camera.current!.recorder?.state!=='recording')setPhase('idle');report(error);}finally{setBusy(false);}
 }
 function stop(){const view=camera.current?.clip?.view;const early=!view||!allCaptured(view);if(early&&!window.confirm('仍有选手或终点事件待处理。现在结束将标记录像可能不完整，Timy3 仍继续计时。确认结束录像？'))return;camera.current?.stop(early);}
 async function confirm(){
  try{
   const current=clipRef.current;if(!current?.heatId||!current.view||!selected)throw new Error('请选择本组录像和过线画面；旧版录像仅供导出');
   const run=current.view.runs.find(r=>r.id===runnerId);if(!run)throw new Error('请选择截图所属选手');setBusy(true);
   const image=await createImageBitmap(selected.image),canvas=document.createElement('canvas');canvas.width=image.width;canvas.height=image.height;
   const ctx=canvas.getContext('2d')!;ctx.drawImage(image,0,0);image.close();ctx.strokeStyle='#ff4545';ctx.lineWidth=2;ctx.beginPath();ctx.moveTo(canvas.width*line,0);ctx.lineTo(canvas.width*line,canvas.height);ctx.stroke();
   const screenshot=await new Promise<Blob>((resolve,reject)=>canvas.toBlob(blob=>blob?resolve(blob):reject(new Error('截图失败')),'image/jpeg',.95));
   const pending=(await listOutbox()).filter(item=>item.payload.heatId&&item.payload.runId===runnerId);
   const revision=Math.max(run.reviews?.length??0,...pending.map(item=>item.payload.expectedReviewRevision+1));
   const payload:Submission={requestId:id(),heatId:current.heatId,runId:runnerId,recordingId:current.id,deviceId:link.deviceId,expectedReviewRevision:revision,
    framePtsUs:selected.mediaUs,finishLine:line,evidenceId:id(),visionEventId:run.visionEventId??'',note:`裁判逐帧更正 #${index+1}；修改视频判定帧后需重新核验时间映射`};
   const metadata=new Blob([JSON.stringify({heatId:current.heatId,recordingId:current.id,runId:runnerId,framePtsUs:selected.mediaUs,finishLine:line,videoMime:current.mime,officialTiming:'camera-primary',captureTimestampVerified:false})],{type:'application/json'});
   await saveOutbox({id:payload.requestId,clipId:current.id,payload,image:screenshot,metadata,complete:false,created:Date.now()});
   link.notify(`号码 ${run.athlete.bib} 的截图已保存在本机；判读已提交，电脑核验主成绩条件`);void link.retry();
  }catch(error){report(error);}finally{setBusy(false);}
 }
 async function recovery(){try{setSavedClips(await listClips());setModal('recovery');}catch(error){report(error);}}
 function returnCamera(){camera.current!.closePreview();camera.current!.clip=undefined;clipRef.current=undefined;setClip(undefined);setSelected(undefined);setFrames([]);setMovieUrl('');setPhase('idle');link.notify('相机已关闭；选手接近终点时再手动开启相机并录像');}
 const ratio=(reviewing?clip!.width:settings?.width??4)/(reviewing?clip!.height:settings?.height??3),mediaWidth=Math.min(dimensions.width,dimensions.height*ratio),mediaHeight=mediaWidth/ratio;
 const move=(e:ReactPointerEvent)=>{if(zoom>1&&pointer.current){setPan(p=>({x:p.x+e.clientX-pointer.current!.x,y:p.y+e.clientY-pointer.current!.y}));pointer.current={x:e.clientX,y:e.clientY};}};
 const canCapture=connection.connected&&!!active&&!active.heat.recordingStopped&&active.heat.state!=='closed'&&
  (active.heat.state!=='pending'||active.heat.acceptingFinishes)&&!busy;
 return <div className="app"><header><span className="brand">终点相机 · v0.4.1</span><span className={`connection ${connection.connected?'online':''}`}><i/>{connection.connected?'电脑已连接':'电脑未连接'}</span></header><main>
  <section className="intro"><h1>{group?`${group.heat.name} · ${group.runs.length} 人`:'等待电脑创建组次'}</h1><p>{reviewing?'逐人复核躯干过线帧，更正追加版本':recording?'请持续拍摄至最后一人过线':active?.heat.start?'Timy3 已起跑，接近终点时手动开启相机': '先在电脑发令，选手接近终点时再开启相机'}</p></section>
  <div className="stage" ref={stage}><div className="media-content" style={{width:mediaWidth,height:mediaHeight,transform:`translate(${pan.x}px,${pan.y}px) scale(${zoom})`}} onPointerDown={e=>{if(zoom>1){pointer.current={x:e.clientX,y:e.clientY};e.currentTarget.setPointerCapture(e.pointerId);}}} onPointerMove={move} onPointerUp={()=>{pointer.current=undefined;}}>
   <video ref={live} autoPlay muted playsInline hidden={reviewing}/><video ref={movie} src={movieUrl||undefined} muted playsInline preload="auto" hidden={!reviewing} onLoadedData={initializeMovie}/>{(recording||reviewing||settings)&&<span className="finish-line" style={{left:`${line*100}%`}}/>}{!reviewing&&settings&&<span className="detection-region" style={{left:`${detection.left*100}%`,right:`${(1-detection.right)*100}%`,top:`${detection.top*100}%`,bottom:`${(1-detection.bottom)*100}%`}}/>}</div>
   {phase==='idle'&&<div className="empty-camera"><CameraIcon/><span>相机尚未开启</span></div>}{recording&&<span className="recording-indicator"><b/>整组录像中</span>}
  </div>
  <div className="diagnostics"><span>实际帧率 <strong>{settings?.frameRate??clip?.fps?`${Math.round(settings?.frameRate??clip!.fps)} fps`:'—'}</strong></span><span>网络 RTT <strong>{connection.clock?`${(connection.clock.rttUs/1000).toFixed(1)} ms`:'—'}</strong></span></div>
  <p className="notice" role="note">{modelStatus}</p>
  {!recording&&!reviewing&&<details className="vision-settings" open><summary>终点躯干检测设置（开始录像后锁定）</summary><label>终点参考线<input type="range" aria-label="检测终点线" min="0.06" max="0.94" step="0.001" value={line} onChange={e=>setLine(Number(e.target.value))}/></label><label>通过方向<select value={detection.direction} onChange={e=>setDetection(v=>({...v,direction:Number(e.target.value)}))}><option value="1">从左向右</option><option value="-1">从右向左</option></select></label><label>检测区域顶部<input type="range" min="0" max="0.5" step="0.01" value={detection.top} onChange={e=>setDetection(v=>({...v,top:Number(e.target.value)}))}/></label><label>检测区域底部<input type="range" min="0.51" max="1" step="0.01" value={detection.bottom} onChange={e=>setDetection(v=>({...v,bottom:Number(e.target.value)}))}/></label><label><input type="checkbox" checked={detection.fixedSide} onChange={e=>setDetection(v=>({...v,fixedSide:e.target.checked}))}/>手机已固定在终点线延长线上侧拍</label></details>}
  {reviewing?<section className="review-controls">
   {clip?.earlyStop&&<p className="precision">录像提前结束，证据可能不完整；设备计时仍保留</p>}
   <label className="review-athlete">截图所属选手<select aria-label="截图所属选手" value={runnerId} onChange={e=>setRunnerId(e.target.value)}>{clip?.view?.runs.map(r=><option key={r.id} value={r.id}>{r.athlete.bib} · {r.athlete.name} · {formatTime(r,clip.view!.heat)}{r.elapsedUs==null?'':' s'}</option>)}</select></label>
   <div className="frame-label">{busy?'正在读取画面…':selected?`帧 ${index+1} / ${frames.length} · ${(selected.mediaUs/1e6).toFixed(4)} s`:`已索引 ${frames.length} 帧，选择画面`}</div>
   <input aria-label="视频帧" type="range" min="0" max={Math.max(0,frames.length-1)} value={index} disabled={busy||!frames.length} onChange={e=>{setIndex(Number(e.target.value));setSelected(undefined);}} onPointerUp={()=>void seek(index)} onKeyUp={()=>void seek(index)}/>
   <div className="frame-actions"><button onClick={()=>void seek(index-1)} disabled={busy||index===0}>上一帧</button><button onClick={()=>void seek(index+1)} disabled={busy||index>=frames.length-1}>下一帧</button><button onClick={()=>setZoom(z=>z>=4?1:z+.5)}>{zoom>=4?'还原':'放大'}</button></div>
   <label className="line-control">终点参考线<input aria-label="终点参考线" type="range" min="0" max="1" step="0.001" value={line} onChange={e=>setLine(Number(e.target.value))}/></label>
   <button className="primary" onClick={()=>void confirm()} disabled={!selected||busy||!runnerId}>保存此人的过线证据</button>
   <a className="download" href={movieUrl} download={`${clip?.view?.heat.name??'旧版录像'}.${clip?.mime.includes('webm')?'webm':'mp4'}`}>导出整组原片</a><button className="secondary" onClick={returnCamera}>返回相机，准备下一组</button>
  </section>:<div className="capture-actions"><button className="primary" onClick={recording?stop:()=>void startManual()} disabled={recording?busy:!canCapture}>{recording?'结束整组录像':'开启相机并录像'}</button>{!recording&&<button className="secondary" disabled={busy} onClick={()=>{if(phase==='preview'){camera.current!.closePreview();setPhase('idle');}else{setBusy(true);void camera.current!.open(live.current!).catch(report).finally(()=>setBusy(false));}}}>{phase==='preview'?'关闭预览相机':'手动预览并校准终点线'}</button>}<p className="camera-policy">相机默认关闭；可手动预览校准，开始录像后锁定设置。Timy3 C1 独立保存备份，裁判确认后才能采用。</p></div>}
  {group?.heat.evidenceIncomplete&&<p className="notice" role="note">{group.heat.recordingNote||'本组录像证据可能不完整，请核查是否漏拍。'}</p>}
  {group?.heat.visionEvents&&<section className="group-list" aria-label="视频过线候选"><p>视频躯干过线候选 · 电脑匹配号码</p>{group.heat.visionEvents.map(v=><div key={v.id}><span>{v.runId?'已绑定':'待匹配'} · {v.eligible?'已核验':v.reasons.join('；')}</span><strong>{v.estimatedElapsedUs==null?'待映射':`${(v.estimatedElapsedUs/1e6).toFixed(4)} s`}</strong></div>)}</section>}
  {group&&<section className="group-list" aria-label="本组设备成绩"><p>当前采用成绩 · 视频候选与光电门备份分别保存</p>{group.runs.map(r=><div key={r.id}><span>{r.athlete.bib} · {r.athlete.name}</span><strong>{formatTime(r,group.heat)}{r.elapsedUs==null?'':' s'}</strong></div>)}</section>}
  <div className="utilities"><button onClick={()=>void recovery()} disabled={recording}>恢复本机录像</button><button onClick={()=>setModal('settings')} disabled={recording}>连接设置</button></div>
  {connection.status&&<p className="notice" role="status">{connection.status}</p>}<p className="precision">视频主计时 · 无实测达标配置时自动确认关闭 · 光电门仅经裁判确认作为备份</p>
 </main><dialog ref={dialog} onCancel={()=>setModal(null)} onClick={e=>{if(e.target===dialog.current)setModal(null);}}><div className="dialog-body"><div className="dialog-title"><h2>{modal==='recovery'?'本机整组录像':'连接设置'}</h2><button onClick={()=>setModal(null)} aria-label="关闭">×</button></div>
  {modal==='recovery'?<div className="saved-list">{savedClips.length?savedClips.map(c=><button key={c.id} onClick={()=>{setModal(null);void loadClip(c).catch(report);}}><strong>{c.view?.heat.name??`旧版 · ${c.run?.athlete.name??'录像'}`}</strong><small>{new Date(c.created).toLocaleString()} · {c.complete?'已封装':'分片待恢复'}</small></button>):<p>本机还没有录像</p>}</div>:<form onSubmit={e=>{e.preventDefault();void link.pair(secret).then(()=>setModal(null)).catch(report);}}><p>扫描电脑二维码配对，首次需完成本机 HTTPS 证书信任。请保持页面前台，整组结束后再停录像。</p><label>配对码<input value={secret} onChange={e=>setSecret(e.target.value)} autoComplete="off"/></label><button className="primary" type="submit">连接电脑</button><button className="secondary" type="button" onClick={()=>{void link.retry();setModal(null);}}>重试证据与整组原片补传</button><a className="download" href="/setup">打开证书安装指引</a></form>}
 </div></dialog></div>;
}
