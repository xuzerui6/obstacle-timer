import {DetectorClient} from './detector-client.mjs';
export const detector=new DetectorClient(()=>new Worker(new URL('./vision.worker.ts',import.meta.url),{type:'module'}));
