// Video-only MediaRecorder WebM timestamp index. No nominal-FPS arithmetic.
export function webmFrameTimes(buffer){
 const bytes=new Uint8Array(buffer);let scale=1_000_000;const frames=[];
 function vint(pos,keep){if(pos>=bytes.length)throw new Error('WebM 文件被截断');let length=1,mask=128;while(length<=8&&!(bytes[pos]&mask)){length++;mask>>=1;}if(length>8||pos+length>bytes.length)throw new Error('WebM 整数无效');let value=keep?bytes[pos]:bytes[pos]&(mask-1);let unknown=!keep&&value===mask-1;for(let i=1;i<length;i++){value=value*256+bytes[pos+i];unknown=unknown&&bytes[pos+i]===255;}return{length,value,unknown};}
 function uint(start,end){let value=0;for(let i=start;i<end;i++)value=value*256+bytes[i];return value;}
 function walk(start,end,cluster=0){let pos=start;while(pos<end){const tag=vint(pos,true);pos+=tag.length;const size=vint(pos,false);pos+=size.length;const stop=size.unknown?end:Math.min(end,pos+size.value);if(stop<pos)throw new Error('WebM 长度无效');
  if(tag.value===0x2ad7b1)scale=uint(pos,stop);
  else if(tag.value===0xe7)cluster=uint(pos,stop);
  else if(tag.value===0xa3||tag.value===0xa1){const track=vint(pos,false);const p=pos+track.length;if(p+3>stop)throw new Error('WebM 帧不完整');let relative=(bytes[p]<<8)|bytes[p+1];if(relative&0x8000)relative-=65536;if(bytes[p+2]&6)throw new Error('该 WebM 视频使用暂不支持的帧交织');frames.push((cluster+relative)*scale/1_000_000_000);}
  else if([0x18538067,0x1549a966,0x1f43b675,0xa0].includes(tag.value))walk(pos,stop,tag.value===0x1f43b675?0:cluster);
  if(stop===pos&&size.unknown)break;pos=stop;
 }}
 walk(0,bytes.length);if(!frames.length)throw new Error('没有找到可判读 WebM 帧');const sorted=[...new Set(frames)].sort((a,b)=>a-b),origin=sorted[0];return sorted.map(v=>v-origin);
}
