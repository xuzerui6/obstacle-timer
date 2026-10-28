import {estimateClock} from './clock.mjs';
import {id,nowUs,type ClockMap,type Snapshot,type Outbox} from './types';
import {getClip,listClips,listOutbox,saveClip,saveOutbox,videoBlob,listVision,saveVision} from './storage';
type Sample={id:string;proof:string;t1:number;t2:number;t3:number;t4:number};
type View={connected:boolean;status:string;snapshot:Snapshot|null;clock:ClockMap|null};
class PhoneLink {
 uploadError='';
 readonly deviceId=localStorage.getItem('obstacle-device')??id();
 private token=localStorage.getItem('obstacle-token')??'';
 private ws?:WebSocket; private listeners=new Set<()=>void>();private samples:Sample[]=[];private requests=new Map<string,number>();
 private view:View={connected:false,status:'',snapshot:null,clock:null};private initialized=false;private uploading=false;
 private lastReceivedUs=0;
 constructor(){localStorage.setItem('obstacle-device',this.deviceId);}
 subscribe=(fn:()=>void)=>{this.listeners.add(fn);return()=>this.listeners.delete(fn);};
 snapshot=()=>this.view;
 private publish(next:Partial<View>){this.view={...this.view,...next};this.listeners.forEach(fn=>fn());}
 notify=(status:string)=>this.publish({status});
 async initialize(){
  if(this.initialized)return;this.initialized=true;
  const secret=new URLSearchParams(location.hash.slice(1)).get('secret');
  if(secret)await this.pair(secret);else if(this.token)this.connect();
  window.addEventListener('hashchange',()=>{const value=new URLSearchParams(location.hash.slice(1)).get('secret');if(value)void this.pair(value).catch(error=>this.notify((error as Error).message));});
  window.addEventListener('offline',()=>this.disconnect('网络离线，本机继续保存录像与截图，恢复后补传'));
  window.addEventListener('online',()=>{if(this.token)this.connect();});
  setInterval(()=>{if(this.view.connected&&nowUs()-this.lastReceivedUs>15e6)this.disconnect('电脑连接超时，录像与判读保留在本机');
   if(this.view.connected){this.sync();void this.retry();}else if(this.token)this.connect();},5000);
 }
 async pair(secret:string){
  if(!secret.trim())throw new Error('请扫描电脑上的二维码，或输入配对码');
  const response=await fetch('/api/pair',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({secret:secret.trim(),deviceId:this.deviceId})});
  const data=await this.decode(response);if(data.protocol!==3)throw new Error('电脑协议版本不匹配，请更新电脑安装包');this.token=data.token;localStorage.setItem('obstacle-token',this.token);
  history.replaceState(null,'',location.pathname);this.connect();
 }
 private connect(){
  if(!navigator.onLine)return;
  if(this.ws?.readyState===WebSocket.OPEN||this.ws?.readyState===WebSocket.CONNECTING)return;
  if(location.protocol!=='https:'){this.notify('请从电脑的安全 HTTPS 地址打开相机页面');return;}
  const ws=new WebSocket(`${location.origin.replace('https:','wss:')}/api/ws?access_token=${encodeURIComponent(this.token)}`);this.ws=ws;
  ws.onopen=()=>{this.lastReceivedUs=nowUs();this.publish({connected:true,status:'电脑已连接，正在同步'});for(let i=0;i<30;i++)setTimeout(()=>this.sync(),i*50);};
  ws.onmessage=(message)=>{
   if(this.ws!==ws)return;this.lastReceivedUs=nowUs();
   const t4=nowUs();try{
    const {type,data}=JSON.parse(message.data);
    if(type==='state'){
     if(data.protocol!==3){ws.close();this.notify('电脑需要升级到视觉计时 v3');return;}
     if(this.view.snapshot?.sessionId!==data.sessionId){this.samples=[];this.requests.clear();for(let i=0;i<30;i++)setTimeout(()=>this.sync(),i*50);}
     this.publish({snapshot:data});
    }else if(type==='sync'){
     const t1=this.requests.get(data.id);this.requests.delete(data.id);
     if(t1===data.t1&&data.sessionId===this.view.snapshot?.sessionId){
      this.samples.push({id:data.id,proof:data.proof,t1:t1!,t2:data.t2,t3:data.t3,t4});this.samples=this.samples.slice(-120);
      if(this.samples.length>=5)this.publish({clock:estimateClock(data.sessionId,this.samples)});
     }
    }else if(type==='error')this.notify(data.message);
    else if(type==='recordingAccepted')this.notify('电脑已确认本组录像；拍到最后一人后再结束录像');
   }catch(error){this.notify(`消息处理失败：${(error as Error).message}`);}
  };
  ws.onclose=()=>{if(this.ws===ws)this.publish({connected:false,status:'连接已断开，正在保留本机录像；恢复后补传'});};
  ws.onerror=()=>this.notify('连接失败，请检查本机证书、网络或重新扫码配对');
 }
 private disconnect(status:string){const old=this.ws;this.ws=undefined;old?.close();this.publish({connected:false,status});}
 private sync(){const request=id(),t1=nowUs();this.requests.set(request,t1);for(const [key,time]of this.requests)if(t1-time>1e7)this.requests.delete(key);this.send('sync',{id:request,t1});}
 clock():ClockMap{
  const map=this.view.clock;if(!map||map.samples<5||nowUs()-map.sampledClientUs>6e7)throw new Error('时钟尚未同步，请等待数秒');return structuredClone(map);
 }
 clockEvidence(pageClockId:string){return {sessionId:this.view.snapshot?.sessionId??'',pageClockId,mapId:id(),samples:structuredClone(this.samples)};}
 send(type:string,data:unknown){if(this.ws?.readyState===WebSocket.OPEN)this.ws.send(JSON.stringify({type,data}));}
 async api(path:string,init?:RequestInit){
  return this.decode(await fetch(path,{...init,headers:{Authorization:`Bearer ${this.token}`,...init?.headers}}));
 }
 private async decode(response:Response){const text=await response.text();let data;try{data=JSON.parse(text);}catch{throw new Error(`服务器响应异常 ${response.status}`);}if(!response.ok)throw new Error(data.error??`请求被拒绝 ${response.status}`);return data;}
 async retry(){
  if(this.uploading||!this.view.connected)return;this.uploading=true;
  try{
   const failures:string[]=[];
   for(const clip of await listClips()){
    if(!clip.heatId||!clip.registrationPending)continue;
    try{
     await this.api('/api/recording-ready',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({heatId:clip.heatId,recordingId:clip.id,deviceId:this.deviceId,videoMime:clip.mime,fps:clip.fps,width:clip.width,height:clip.height,savedChunks:clip.chunkCount,model:clip.model??'',captureFingerprint:clip.captureFingerprint??'',vision:clip.vision})});
     clip.registrationPending=false;await saveClip(clip);
    }catch(error){failures.push((error as Error).message);}
   }
   for(const clip of await listClips()){
    if(!clip.heatId||!clip.statusPending)continue;
    try{
     await this.api('/api/recording-status',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({heatId:clip.heatId,recordingId:clip.id,recording:false,complete:clip.complete&&!clip.earlyStop,note:clip.earlyStop?'提前停止/离开前台，证据可能不完整':'整组原片已封装',savedChunks:clip.chunkCount})});
     clip.statusPending=false;await saveClip(clip);
    }catch(error){failures.push((error as Error).message);}
   }
   const candidates=(await listVision()).filter(item=>!item.complete);
   for(const item of candidates){try{
    const registered=await getClip(item.clipId);
    // A fast detector may finish before the first saved chunk is acknowledged.
    // Keep its outbox entry locally until the recording ownership is registered.
    if(!registered||registered.chunkCount<1||registered.registrationPending!==false)continue;
    await this.api('/api/vision-events',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(item.payload)});item.submitted=true;await saveVision(item);
    const owner={payload:{recordingId:item.clipId,evidenceId:item.id,heatId:item.payload.heatId}} as Outbox;
    for(const kind of ['image','before','after','mask','metadata'])await this.upload(owner,kind,item.files[kind]);
    item.evidenceUploaded=true;await saveVision(item);
   }catch(error){failures.push((error as Error).message);}}
   const items=(await listOutbox()).filter(item=>!item.complete&&item.payload.heatId);
   for(const item of items){
    try{
     const ack=await this.api('/api/reviews',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(item.payload)});
     item.acceptedRevision=ack.review.revision;await saveOutbox(item);
     await this.upload(item,'image',item.image);await this.upload(item,'metadata',item.metadata);
     item.reviewUploaded=true;await saveOutbox(item);
    }catch(error){failures.push((error as Error).message);}
   }
   const recordings=new Set(items.filter(item=>item.reviewUploaded).map(item=>item.clipId));
   for(const recording of recordings){
    try{
     const clip=await getClip(recording);if(!clip)throw new Error('找不到整组原片，文件未删除');
     const owner=items.find(item=>item.clipId===recording&&item.reviewUploaded)!;
     await this.upload(owner,'video',await videoBlob(clip));clip.serverAcknowledged=true;await saveClip(clip);
     for(const item of items.filter(item=>item.clipId===recording&&item.reviewUploaded)){item.complete=true;await saveOutbox(item);}
    }catch(error){failures.push((error as Error).message);}
   }
   for(const item of candidates.filter(v=>v.evidenceUploaded)){try{
    const clip=await getClip(item.clipId);if(!clip?.complete)continue;
    const blob=await videoBlob(clip),owner={payload:{recordingId:item.clipId,evidenceId:item.id,heatId:item.payload.heatId}} as Outbox;
    await this.upload(owner,'video',blob);clip.serverAcknowledged=true;await saveClip(clip);
    if(!item.correspondence){const {matchOriginal}=await import('./vision-match');const matched=await matchOriginal(clip,blob,item);item.correspondence=matched.proof;item.correspondenceReason=matched.reason;await saveVision(item);}
    if(item.correspondence)await this.api(`/api/vision-events/${item.id}/correspondence`,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(item.correspondence)});
    // A non-unique match remains pending on the computer; all files have been retained.
    item.complete=true;await saveVision(item);
   }catch(error){failures.push((error as Error).message);}}
   this.uploadError=failures[0]??'';
   if(failures.length)this.notify(`本机文件保留，待补传/处理：${failures[0]}`);
   else if(items.length)this.notify('多人截图与整组原片已获电脑确认；原片只传一份，本机仍保留');
  }catch(error){this.uploadError=(error as Error).message;this.notify(`已保存在本机，待补传：${(error as Error).message}`);}finally{this.uploading=false;}
 }
 private async upload(item:Outbox,kind:string,blob:Blob){
  const fileId=kind==='video'?item.payload.recordingId:item.payload.evidenceId;
  const base=`/api/heat-evidence/${fileId}/${kind}?heatId=${item.payload.heatId}`;
  const checkpoint=await this.api(base);if(checkpoint.complete)return;let offset:number=checkpoint.offset;
  const {sha256}=await import('@noble/hashes/sha2.js');const digest=sha256.create();
  for(let offset=0;offset<blob.size;offset+=4*1024*1024)digest.update(new Uint8Array(await blob.slice(offset,offset+4*1024*1024).arrayBuffer()));
  const hash=Array.from(digest.digest(),v=>v.toString(16).padStart(2,'0')).join('');
  while(offset<blob.size){const block=blob.slice(offset,offset+4*1024*1024);const ack=await this.api(`${base}&offset=${offset}&length=${blob.size}&sha256=${hash}`,{method:'POST',body:block});
   if(ack.offset<=offset||ack.offset>blob.size)throw new Error('服务器上传断点无效');offset=ack.offset;
   if(offset===blob.size&&!ack.complete)throw new Error('电脑尚未确认完整文件');
  }
 }
}
export const link=new PhoneLink();
