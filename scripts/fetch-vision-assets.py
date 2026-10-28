"""Fetch official BodyPix assets and bundled TFJS WASM, honoring the pinned SHA256 manifest."""
from pathlib import Path
import argparse, hashlib, json, shutil, urllib.request
root=Path(__file__).resolve().parent.parent
parser=argparse.ArgumentParser(description=__doc__)
parser.add_argument('--proxy',default='')
args=parser.parse_args()
opener=urllib.request.build_opener(urllib.request.ProxyHandler({'https':args.proxy,'http':args.proxy}) if args.proxy else urllib.request.ProxyHandler())
target=root/'web/phone/public/vision-assets';target.mkdir(parents=True,exist_ok=True)
manifest_path=root/'web/phone/vision-assets.json'
expected=json.loads(manifest_path.read_text()) if manifest_path.exists() else {}
actual={}
def fetch(name,url):
 path=target/name;path.parent.mkdir(parents=True,exist_ok=True)
 if not path.exists():
  print('Fetch',url,flush=True)
  with opener.open(url,timeout=120) as response:path.write_bytes(response.read())
 digest=hashlib.sha256(path.read_bytes()).hexdigest()
 if name in expected and expected[name]['sha256']!=digest:raise SystemExit('Asset SHA256 mismatch: '+name)
 actual[name]={'url':url,'sha256':digest,'bytes':path.stat().st_size}
 return path
base='https://storage.googleapis.com/tfjs-models/savedmodel/bodypix/mobilenet/quant2/075/'
model=fetch('bodypix/model-stride8.json',base+'model-stride8.json')
for group in json.loads(model.read_text())['weightsManifest']:
 for shard in group['paths']:fetch('bodypix/'+shard,base+shard)
for name in ['tfjs-backend-wasm.wasm','tfjs-backend-wasm-simd.wasm','tfjs-backend-wasm-threaded-simd.wasm']:
 origin=root/'web/phone/node_modules/@tensorflow/tfjs-backend-wasm/dist'/name
 dest=target/'wasm'/name;dest.parent.mkdir(exist_ok=True);shutil.copyfile(origin,dest)
 digest=hashlib.sha256(dest.read_bytes()).hexdigest()
 if 'wasm/'+name in expected and expected['wasm/'+name]['sha256']!=digest:raise SystemExit('WASM SHA256 mismatch')
 actual['wasm/'+name]={'url':'npm:@tensorflow/tfjs-backend-wasm@4.22.0/'+name,'sha256':digest,'bytes':dest.stat().st_size}
license_path=root/'web/phone/node_modules/@tensorflow-models/body-pix/README.md'
(target/'NOTICE.txt').write_text('BodyPix 2.2.1 / TensorFlow.js 4.22.0. Copyright Google LLC. Apache-2.0.\nhttps://www.apache.org/licenses/LICENSE-2.0\nModel source: '+base+'\n',encoding='utf-8')
manifest_path.write_text(json.dumps(actual,indent=2),encoding='utf-8')
print('Verified',len(actual),'local assets; no runtime Internet required')
