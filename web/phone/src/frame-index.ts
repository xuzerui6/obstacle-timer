import {webmBlobTimes} from './webm-index.mjs';
import {mp4PresentationTimes} from './mp4-index.mjs';
export async function frameTimes(blob:Blob):Promise<number[]>{
 if(blob.type.includes('webm'))return webmBlobTimes(blob);
 const {createFile}=await import('mp4box');
 const file=createFile(false);let trackId=0,failure='';
  file.onError=(error:any)=>{failure=`无法读取 MP4 帧索引：${error}`;};
  file.onReady=(info:any)=>{
   const video=info.videoTracks?.[0];if(!video){failure='录像缺少视频轨道';return;}trackId=video.id;
  };
  for(let offset=0;offset<blob.size;){const end=Math.min(blob.size,offset+1024*1024),input=await blob.slice(offset,end).arrayBuffer() as ArrayBuffer&{fileStart:number};input.fileStart=offset;const next=file.appendBuffer(input);if(failure)throw new Error(failure);offset=Math.min(blob.size,Math.max(end,next??end));}
  file.flush();if(failure)throw new Error(failure);if(!trackId)throw new Error('录像尚未封装完成，原片分片已保留');
  const times=mp4PresentationTimes(file,trackId);if(!times.length)throw new Error('录像尚未封装完成，原片已保留');return times;
}
export async function presentedFrame(video:HTMLVideoElement,target:number):Promise<{mediaUs:number;image:Blob}>{
 if(!video.requestVideoFrameCallback)throw new Error('当前浏览器缺少帧回调，请使用较新版本 Safari 或 Chrome');
 return new Promise((resolve,reject)=>{
  let handle=0,captured=false,last=-1;video.pause();const timer=setTimeout(()=>{video.pause();video.cancelVideoFrameCallback(handle);reject(new Error(`所选帧未能呈现（目标 ${target.toFixed(6)}，收到 ${last.toFixed(6)}），请重试或更换浏览器`));},4000);
  const capture=(_now:number,meta:VideoFrameCallbackMetadata)=>{
   last=meta.mediaTime;if(Math.abs(meta.mediaTime-target)>.00001){handle=video.requestVideoFrameCallback(capture);return;}
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
