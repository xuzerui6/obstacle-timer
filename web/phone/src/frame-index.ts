import {webmFrameTimes} from './webm.mjs';
export async function frameTimes(blob:Blob):Promise<number[]>{
 const buffer=await blob.arrayBuffer();
 if(blob.type.includes('webm'))return webmFrameTimes(buffer);
 const {createFile}=await import('mp4box');
 return new Promise((resolve,reject)=>{
  const file=createFile();const times:number[]=[];let trackId=0;
  file.onError=(error:any)=>reject(new Error(`无法读取 MP4 帧索引：${error}`));
  file.onReady=(info:any)=>{
   const video=info.videoTracks?.[0];if(!video){reject(new Error('录像缺少视频轨道'));return;}trackId=video.id;
   file.setExtractionOptions(trackId,null,{nbSamples:100});file.start();
  };
  file.onSamples=(_track:number,_user:any,samples:any[])=>{
   for(const sample of samples)times.push(sample.cts/sample.timescale);
   if(samples.length)file.releaseUsedSamples(trackId,samples.at(-1).number);
  };
  try{
   const input=buffer as ArrayBuffer&{fileStart:number};input.fileStart=0;file.appendBuffer(input);file.flush();
   if(!times.length){reject(new Error('录像尚未封装完成，原片已保留'));return;}
   const ordered=[...new Set(times)].sort((a,b)=>a-b),origin=ordered[0];resolve(ordered.map(v=>v-origin));
  }catch(error){reject(error);}
 });
}
export async function presentedFrame(video:HTMLVideoElement,target:number):Promise<{mediaUs:number;image:Blob}>{
 if(!video.requestVideoFrameCallback)throw new Error('当前浏览器缺少帧回调，请使用较新版本 Safari 或 Chrome');
 return new Promise((resolve,reject)=>{
  let handle=0,captured=false;const timer=setTimeout(()=>{video.cancelVideoFrameCallback(handle);reject(new Error('所选帧未能呈现，请重试或更换浏览器'));},4000);
  const capture=(_now:number,meta:VideoFrameCallbackMetadata)=>{
   captured=true;clearTimeout(timer);video.pause();const canvas=document.createElement('canvas');canvas.width=video.videoWidth;canvas.height=video.videoHeight;
   if(!canvas.width||!canvas.height){reject(new Error('视频帧尺寸无效'));return;}
   canvas.getContext('2d')!.drawImage(video,0,0);canvas.toBlob(blob=>blob?resolve({mediaUs:Math.round(meta.mediaTime*1e6),image:blob}):reject(new Error('截图失败')),'image/jpeg',.95);
  };
  handle=video.requestVideoFrameCallback(capture);
  video.currentTime=Math.max(0,target+0.000001);
  video.playbackRate=.25;
  void video.play().catch(error=>{if(captured)return;clearTimeout(timer);video.cancelVideoFrameCallback(handle);reject(error);});
 });
}
