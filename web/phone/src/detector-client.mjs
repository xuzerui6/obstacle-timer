export class StaleAnalysis extends Error {constructor(){super('分析会话已更换');this.name='StaleAnalysis';}}
export class DetectorClient {
 constructor(createWorker,{loadTimeout=30000,frameTimeout=10000}={}){this.createWorker=createWorker;this.loadTimeout=loadTimeout;this.frameTimeout=frameTimeout;this.pending=new Map();this.epoch=0;this.sequence=0;this.session='';}
 async load(){
  if(this.loading)return this.loading;
  const epoch=++this.epoch,worker=this.createWorker();this.worker=worker;
  worker.onmessage=({data})=>{if(this.epoch!==epoch)return;const p=this.pending.get(data.requestId);if(!p)return;
   this.pending.delete(data.requestId);clearTimeout(p.timer);
   if(data.type==='error')p.reject(new Error(data.message));else if(p.session&&data.sessionId!==p.session)p.reject(new StaleAnalysis());else p.resolve(data);
  };
  worker.onerror=error=>{if(this.epoch===epoch)this.invalidate(new Error(error.message||'模型进程失败'));};
  this.loading=this.request('load',{},'',this.loadTimeout).catch(error=>{if(this.epoch===epoch)this.invalidate(error);throw error;});return this.loading;
 }
 request(type,data,session,timeout=this.frameTimeout){
  const requestId=String(++this.sequence);return new Promise((resolve,reject)=>{
   const timer=setTimeout(()=>{if(!this.pending.has(requestId))return;this.pending.delete(requestId);reject(new Error(type==='load'?'模型加载超过30秒':'单帧分析超过10秒'));this.invalidate(new Error('模型请求超时'));},timeout);
   this.pending.set(requestId,{resolve,reject,timer,session,type});
   try{this.worker.postMessage({type,...data,requestId,sessionId:session});}catch(error){clearTimeout(timer);this.pending.delete(requestId);reject(error);}
  });
 }
 async begin(settings){
  if([...this.pending.values()].some(p=>p.session))this.invalidate(new StaleAnalysis());
  const session=crypto.randomUUID();this.session=session;await this.load();
  if(this.session!==session)throw new StaleAnalysis();await this.request('reset',{settings},session);return session;
 }
 async analyze(frame,session){if(!session||session!==this.session)throw new StaleAnalysis();return this.request('frame',{frame},session);}
 invalidate(error=new StaleAnalysis()){
  this.epoch++;this.worker?.terminate();this.worker=undefined;this.loading=undefined;
  for(const p of this.pending.values()){clearTimeout(p.timer);p.reject(error);}this.pending.clear();
 }
 dispose(){this.session='';this.invalidate();}
}
