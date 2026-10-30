// MP4Box's cumulative sample DTS can bridge fragment boundaries using an estimated
// final duration. The next fragment's tfdt is authoritative, including real gaps.
export function mp4PresentationTimes(file,trackId){
 const samples=file.getTrackSamplesInfo(trackId),adjustments=new Map();
 for(const sample of samples){
  const number=sample.moof_number;
  if(number&&!adjustments.has(number)){
   const fragment=file.moofs?.[number-1]?.trafs?.find(t=>t.tfhd?.track_id===trackId);
   const base=fragment?.tfdt?.baseMediaDecodeTime;
   adjustments.set(number,Number.isFinite(base)?base-sample.dts:0);
  }
 }
 const times=samples.map(s=>(s.cts+(adjustments.get(s.moof_number)??0))/s.timescale);
 if(times.some(t=>!Number.isFinite(t)))throw new Error('MP4 帧时间戳无效');
 const ordered=[...new Set(times)].sort((a,b)=>a-b),origin=ordered[0];
 return ordered.map(t=>t-origin);
}
