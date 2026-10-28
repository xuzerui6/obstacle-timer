import type {Clip,Outbox,VisionItem} from './types';
let connection:Promise<IDBDatabase>|undefined;
function db(){return connection??=new Promise<IDBDatabase>((resolve,reject)=>{
 const request=indexedDB.open('obstacle-finish-v1',2);
 request.onupgradeneeded=()=>{const d=request.result;if(!d.objectStoreNames.contains('clips')){d.createObjectStore('clips',{keyPath:'id'});d.createObjectStore('chunks',{keyPath:['clipId','index']});d.createObjectStore('outbox',{keyPath:'id'});}d.createObjectStore('vision',{keyPath:'id'});};
 request.onsuccess=()=>resolve(request.result);request.onerror=()=>reject(request.error??new Error('无法打开本机录像存储'));
});}
async function write(store:string,value:unknown){const d=await db();await new Promise<void>((resolve,reject)=>{
 const t=d.transaction(store,'readwrite');t.objectStore(store).put(value);t.oncomplete=()=>resolve();t.onerror=()=>reject(t.error??new Error('本机存储写入失败'));t.onabort=()=>reject(t.error??new Error('本机存储空间不足'));
});}
async function read<T>(store:string,key:IDBValidKey):Promise<T|undefined>{const d=await db();return new Promise((resolve,reject)=>{const r=d.transaction(store).objectStore(store).get(key);r.onsuccess=()=>resolve(r.result);r.onerror=()=>reject(r.error);});}
async function all<T>(store:string):Promise<T[]>{const d=await db();return new Promise((resolve,reject)=>{const r=d.transaction(store).objectStore(store).getAll();r.onsuccess=()=>resolve(r.result);r.onerror=()=>reject(r.error);});}
export const saveClip=(clip:Clip)=>write('clips',structuredClone(clip));
export const getClip=(key:string)=>read<Clip>('clips',key);
export const listClips=async()=>(await all<Clip>('clips')).sort((a,b)=>b.created-a.created);
export const saveChunk=(clipId:string,index:number,blob:Blob)=>write('chunks',{clipId,index,blob});
export const saveOutbox=(item:Outbox)=>write('outbox',item);
export const listOutbox=async()=>(await all<Outbox>('outbox')).sort((a,b)=>a.created-b.created);
export const saveVision=(item:VisionItem)=>write('vision',item);
export const listVision=()=>all<VisionItem>('vision');
export async function videoBlob(clip:Clip){
 const d=await db();const chunks=await new Promise<{index:number;blob:Blob}[]>((resolve,reject)=>{
  const range=IDBKeyRange.bound([clip.id,0],[clip.id,Number.MAX_SAFE_INTEGER]);const r=d.transaction('chunks').objectStore('chunks').getAll(range);
  r.onsuccess=()=>resolve(r.result);r.onerror=()=>reject(r.error);
 });
 if(!chunks.length)throw new Error('本机尚无可恢复的录像分片');
 return new Blob(chunks.sort((a,b)=>a.index-b.index).map(c=>c.blob),{type:clip.mime});
}
