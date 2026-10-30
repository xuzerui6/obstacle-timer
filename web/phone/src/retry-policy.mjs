export class HttpFailure extends Error {constructor(status,message){super(message);this.status=status;}}
export class UploadYield extends Error {}
export function retryable(record,now=Date.now()){return !record.blocked&&(record.nextAttemptAt??0)<=now;}
export function failed(record,error,now=Date.now(),random=Math.random){
 const attempts=(record.attempts??0)+1,status=error.status;
 return {...record,attempts,error:error.message,blocked:status===401||status===403?'authentication':status===409?'conflict':undefined,
  nextAttemptAt:now+Math.round(Math.min(30000,1000*2**Math.min(attempts-1,5))*(.8+random()*.4))};
}
export const acknowledged=record=>({...record,attempts:0,error:undefined,blocked:undefined,nextAttemptAt:0});
