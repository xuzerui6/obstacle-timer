import * as tf from '@tensorflow/tfjs-core';
import {setWasmPaths} from '@tensorflow/tfjs-backend-wasm';
import * as bodyPix from '@tensorflow-models/body-pix';
import {TorsoTracker,torsoPixels} from './torso.mjs';
let model:bodyPix.BodyPix|undefined,tracker:TorsoTracker;
const MODEL='bodypix-2.2.1-m075-s8-q2-torso-v1';
let pending=Promise.resolve();
self.onmessage=({data})=>{pending=pending.then(()=>handle(data));};
async function handle(data:any){
 try{
  if(data.type==='load'){
   setWasmPaths('/phone/vision-assets/wasm/');await tf.setBackend('wasm');await tf.ready();
   installIntegerMatMul();
   model=await bodyPix.load({architecture:'MobileNetV1',outputStride:8,multiplier:.75,quantBytes:2,modelUrl:'/phone/vision-assets/bodypix/model-stride8.json'});
   self.postMessage({type:'ready',model:MODEL,backend:tf.getBackend()});return;
  }
  if(data.type==='reset'){tracker=new TorsoTracker(data.settings);return;}
  if(data.type==='frame'){
   if(!model||!tracker)throw new Error('模型尚未就绪');
   const {frame}=data;
   // Tensor input avoids browser DOM access inside Worker.
   const tensor=tf.tensor3d(new Int32Array(frame.width*frame.height*3).map((_,i)=>frame.pixels[Math.floor(i/3)*4+i%3]),[frame.height,frame.width,3],'int32');
   let parts;try{parts=await model.segmentMultiPersonParts(tensor,{internalResolution:'medium',segmentationThreshold:.7,maxDetections:12,scoreThreshold:.5,nmsRadius:20,refineSteps:10});}finally{tensor.dispose();}
   const people=parts.map(p=>({...torsoPixels(p.data,p.width,p.height),confidence:p.pose.score}));
   const events=tracker.update(people,frame);self.postMessage({type:'analyzed',events,people:people.length,torsoPixels:people.reduce((sum,p)=>sum+p.points.length,0)});
  }
 }catch(error){self.postMessage({type:'error',message:(error as Error).message});}
};
function installIntegerMatMul(){
 // BodyPix's part decoder multiplies an int32 one-hot matrix by part IDs 0..23.
 // TFJS WASM 4.22 only implements float32 matmul. Cast these exact small integers
 // around that kernel; keep model weights, part labels and segmentation unchanged.
 const multiply=tf.getKernel('BatchMatMul','wasm')!,cast=tf.getKernel('Cast','wasm')!;
 tf.unregisterKernel('BatchMatMul','wasm');
 tf.registerKernel({...multiply,kernelFunc:(args:any)=>{
  const {a,b}=args.inputs;if(a.dtype==='float32'&&b.dtype==='float32')return multiply.kernelFunc(args);
  if(a.dtype!=='int32'||b.dtype!=='int32')throw new Error('Unexpected part decoder matmul types');
  const fa=cast.kernelFunc({inputs:{x:a},backend:args.backend,attrs:{dtype:'float32'}}) as tf.TensorInfo;
  const fb=cast.kernelFunc({inputs:{x:b},backend:args.backend,attrs:{dtype:'float32'}}) as tf.TensorInfo;
  let result:tf.TensorInfo|undefined;
  try{result=multiply.kernelFunc({...args,inputs:{a:fa,b:fb}}) as tf.TensorInfo;return cast.kernelFunc({inputs:{x:result},backend:args.backend,attrs:{dtype:a.dtype}});}
  finally{args.backend.disposeData(fa.dataId);args.backend.disposeData(fb.dataId);if(result)args.backend.disposeData(result.dataId);}
 }});
}
