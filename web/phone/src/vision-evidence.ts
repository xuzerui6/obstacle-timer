import {id,type Clip} from './types';
import {getVision,saveVision} from './storage';
import {link} from './link';
import {MODEL} from './protocol';
export async function digestText(text:string){return digest(new TextEncoder().encode(text));}
export async function digest(bytes:Uint8Array){return Array.from(new Uint8Array(await crypto.subtle.digest('SHA-256',new Uint8Array(bytes).buffer)),n=>n.toString(16).padStart(2,'0')).join('');}
export async function saveCandidate(clip:Clip,event:any,origin:'live'|'offline'|'manual',analysisRunId='',issues:string[]=[]){
 const {before,frame,after}=event;
 const eventId=origin==='live'?id():await digestText(`${clip.id}|${analysisRunId}|${frame.mediaUs}|${event.personIndex??0}|${await digest(new Uint8Array(event.mask))}`);
 if(await getVision(eventId))return false;
 const blob=async(f:any,mask=false)=>{
  const c=document.createElement('canvas');c.width=f.width;c.height=f.height;const ctx=c.getContext('2d')!;
  if(mask){if(event.mask.length!==f.width*f.height)throw new Error('躯干掩膜与画面尺寸不符');const p=new Uint8ClampedArray(f.width*f.height*4);for(let i=0;i<event.mask.length;i++){p[i*4]=60;p[i*4+1]=255;p[i*4+2]=130;p[i*4+3]=event.mask[i];}ctx.putImageData(new ImageData(p,f.width,f.height),0,0);}
  else ctx.putImageData(new ImageData(new Uint8ClampedArray(f.pixels),f.width,f.height),0,0);
  return new Promise<Blob>((resolve,reject)=>c.toBlob(b=>b?resolve(b):reject(new Error('证据编码失败')),mask?'image/png':'image/jpeg',.95));
 };
 const payload={requestId:eventId,eventId,heatId:clip.heatId,recordingId:clip.id,deviceId:link.deviceId,trackId:origin==='live'?`${clip.id}-${analysisRunId}-${event.trackId}`:eventId,
  modelVersion:MODEL,captureFingerprint:clip.captureFingerprint??'',settings:clip.vision,
  clock:origin==='live'?{...frame.clock,source:frame.source,mediaUs:frame.mediaUs,clientUs:frame.clientUs,callbackClientUs:frame.callbackClientUs}:null,
  origin,originalFramePtsUs:origin==='live'?null:frame.mediaUs,analysisRunId,
  beforeMediaUs:before.mediaUs,afterMediaUs:after.mediaUs,maxFrameGapUs:event.maxFrameGapUs,confidence:event.confidence,ambiguous:event.ambiguous,
  issues:[...event.issues,...issues],frameHashes:[before.hash,frame.hash,after.hash],geometry:{width:frame.width,height:frame.height,hashVersion:'dhash256-v1'}};
 const [a,b,c,mask]=await Promise.all([blob(before),blob(frame),blob(after),blob(frame,true)]);
 await saveVision({id:eventId,clipId:clip.id,payload,files:{before:a,image:b,after:c,mask,metadata:new Blob([JSON.stringify({...payload,track:[before,frame,after].map(f=>({sequence:f.sequence,mediaUs:f.mediaUs,clientUs:f.clientUs,hash:f.hash}))})],{type:'application/json'})},created:Date.now(),complete:false,verificationState:origin==='live'?'queued':'unmapped'});
 void link.retry();return true;
}
