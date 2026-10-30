// BodyPix semantic labels, not whole-person boxes. Conservative association deliberately
// sacrifices automatic detections when identity cannot be established.
export class TorsoTracker {
 constructor(settings){this.settings=settings;this.tracks=[];this.next=0;}
 update(people,frame){
  // Emitted identities are latched only while the same trajectory remains visible.
  // Retire lost tracks, including their pixel buffers, before associating a new entrant.
  this.tracks=this.tracks.filter(t=>frame.mediaUs>=t.last.mediaUs&&frame.mediaUs-t.last.mediaUs<=750000);
  const s=this.settings,events=[],used=new Set();
  const detections=people.map((p,personIndex)=>({...p,personIndex})).filter(p=>p.points.length>=24).map(p=>{
   // Mobile JS engines have small argument limits. A close-up portrait torso can
   // contain >100k pixels; Math.min(...pixels) would terminate every detection.
   let count=0,left=1,right=0,top=1,bottom=0;
   for(const [x,y] of p.points){if(x<s.left||x>s.right||y<s.top||y>s.bottom)continue;
    count++;left=Math.min(left,x);right=Math.max(right,x);top=Math.min(top,y);bottom=Math.max(bottom,y);}
   if(count<24)return null;
   return {x:(left+right)/2,y:(top+bottom)/2,
    edge:s.direction===1?right:left,confidence:p.confidence,mask:p.mask,personIndex:p.personIndex};
  }).filter(Boolean);
  for(const d of detections){
   const nearby=this.tracks.filter(t=>Math.hypot(t.x-d.x,t.y-d.y)<.22);
   // A completed runner cannot absorb a new approach from behind the plane.
   // A large reverse jump can also be a returning runner: retain a candidate,
   // but keep that identity ambiguity for the judge.
   let candidates=nearby.filter(t=>!t.emitted||!((d.edge-s.lineX)*s.direction<-.04&&(d.edge-t.edge)*s.direction<-.08));
   const approaching=candidates.filter(t=>!t.emitted);if(approaching.length)candidates=approaching;
   const conflicted=nearby.length>1||nearby.length>candidates.length||detections.some(other=>other!==d&&Math.hypot(other.x-d.x,other.y-d.y)<.18);
   let t=candidates.length===1?candidates[0]:undefined;
   if(t&&used.has(t.id)){t.ambiguous=true;continue;}
   if(!t){t={id:String(++this.next),x:d.x,y:d.y,last:frame,history:[],ambiguous:conflicted,emitted:false};this.tracks.push(t);}
   used.add(t.id);t.ambiguous||=conflicted;
   if(Math.hypot(d.x-t.x,d.y-t.y)>.15)t.ambiguous=true;
   const side=(d.edge-s.lineX)*s.direction,previous=t.history.at(-1);
   if(!t.emitted&&previous&&previous.side<0&&side>=0){
    t.cross={before:previous.frame,frame,mask:d.mask,personIndex:d.personIndex,confidence:Math.min(d.confidence,previous.confidence),gap:frame.mediaUs-previous.frame.mediaUs};
   }
   if(t.cross&&frame.mediaUs>t.cross.frame.mediaUs){
    // One more observed frame must remain across the plane. Reverse/lingering is pending.
    const cross=t.cross;t.emitted=true;
    const issues=[];if(side<0)issues.push('过线后回退，需逐帧复核');
    if(cross.gap>100000||frame.mediaUs-cross.frame.mediaUs>100000)issues.push('关键分析帧间隔过大');
    if(t.ambiguous)issues.push('多人接近或轨迹关联不唯一');
    events.push({trackId:t.id,personIndex:cross.personIndex,before:cross.before,frame:cross.frame,after:frame,mask:cross.mask,
     confidence:cross.confidence,ambiguous:t.ambiguous,issues,maxFrameGapUs:Math.max(cross.gap,frame.mediaUs-cross.frame.mediaUs)});
   }
   if(t.emitted){t.history=[];t.cross=undefined;t.last={mediaUs:frame.mediaUs};}else{t.history.push({side,frame,confidence:d.confidence});t.history=t.history.slice(-3);t.last=frame;}t.x=d.x;t.y=d.y;t.edge=d.edge;
  }
  return events;
 }
}

// 256-bit difference hash used only for original-frame correspondence, never timing.
export function pixelHash(data,width,height){
 const values=[];for(let y=0;y<16;y++)for(let x=0;x<17;x++){
  const p=(Math.min(height-1,Math.floor((y+.5)*height/16))*width+Math.min(width-1,Math.floor((x+.5)*width/17)))*4;
  values.push(data[p]*.299+data[p+1]*.587+data[p+2]*.114);
 }
 let hex='';for(let i=0;i<256;i+=4){let nibble=0;for(let b=0;b<4;b++){const n=i+b,y=Math.floor(n/16),x=n%16;nibble=(nibble<<1)|Number(values[y*17+x]>values[y*17+x+1]);}hex+=nibble.toString(16);}return hex;
}
export function hashDistance(a,b){let n=0;for(let i=0;i<a.length;i++){let v=parseInt(a[i],16)^parseInt(b[i],16);while(v){n++;v&=v-1;}}return n;}

export function torsoPixels(data,width,height){
 const visited=new Uint8Array(data.length);let largest=[];
 for(let i=0;i<data.length;i++){
  if(visited[i]||(data[i]!==12&&data[i]!==13))continue;
  const component=[i];visited[i]=1;
  for(let n=0;n<component.length;n++){
   const p=component[n],x=p%width,y=Math.floor(p/width);
   for(const q of [x>0?p-1:-1,x+1<width?p+1:-1,y>0?p-width:-1,y+1<height?p+width:-1]){
    if(q>=0&&!visited[q]&&(data[q]===12||data[q]===13)){visited[q]=1;component.push(q);}
   }
  }
  if(component.length>largest.length)largest=component;
 }
 const mask=new Uint8Array(data.length),points=[];
 for(const i of largest){mask[i]=255;points.push([(i%width)/width,Math.floor(i/width)/height]);}
 return {points,mask};
}
