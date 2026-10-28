export function estimateClock(sessionId,samples){
 const valid=samples.filter(s=>s.t4>=s.t1&&s.t3>=s.t2&&((s.t4-s.t1)-(s.t3-s.t2))>=0&&((s.t4-s.t1)-(s.t3-s.t2))<=2e6);
 if(valid.length<5)throw new Error('正在同步时钟，请等待数秒');
 const ranked=valid.map(s=>({...s,rtt:(s.t4-s.t1)-(s.t3-s.t2),offset:((s.t2-s.t1)+(s.t3-s.t4))/2})).sort((a,b)=>a.rtt-b.rtt).slice(0,5);
 const offsets=ranked.map(s=>s.offset).sort((a,b)=>a-b);
 return {sessionId,offsetUs:offsets[Math.floor(offsets.length/2)],rttUs:ranked[0].rtt,jitterUs:offsets.at(-1)-offsets[0],samples:valid.length,sampledClientUs:Math.max(...valid.map(s=>s.t4))};
}
