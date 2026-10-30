"""Fetch the upstream BodyPix documentation sample for reproducible software tests."""
from pathlib import Path
import os,urllib.request,hashlib
root=Path(__file__).resolve().parent.parent
target=root/'.tools/e2e-fixtures/person.jpg';target.parent.mkdir(parents=True,exist_ok=True)
url='https://raw.githubusercontent.com/tensorflow/tfjs-models/master/body-pix/images/three_people_faceblur.jpg'
expected='d01d3a5542967ec5e691425bff3a191ea65fd075643dc5a775c2f4b495421294'
if not target.exists():
 with urllib.request.urlopen(url,timeout=60) as response:target.write_bytes(response.read())
if hashlib.sha256(target.read_bytes()).hexdigest()!=expected:raise SystemExit('E2E fixture hash mismatch')
print(target)
