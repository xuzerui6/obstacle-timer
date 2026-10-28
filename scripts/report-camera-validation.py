"""Summarize operator-supplied end-to-end measurements; never generates measurement data."""
from pathlib import Path
import argparse, json, math, statistics, hashlib
p=argparse.ArgumentParser(description=__doc__)
p.add_argument('profile',type=Path);p.add_argument('--output',type=Path,required=True)
a=p.parse_args();raw=a.profile.read_bytes();profile=json.loads(raw)
rows=profile.get('measurements',[])
errors=[r['estimatedUs']-r['referenceUs'] for r in rows]
absolute=sorted(abs(e) for e in errors)
required={'normal','congested','long','frame-gap'}
conditions={r['condition'] for r in rows}
failures=[]
if len(rows)<100:failures.append('至少需要100次端到端比较')
if len({r['id'] for r in rows})!=len(rows):failures.append('测量标识重复')
if not absolute or max(absolute)>10000:failures.append('最大绝对误差超过10ms或无测量')
if not required<=conditions:failures.append('缺少正常/拥堵/长时间/掉帧条件')
if any(not r.get('evidence') or not 0<=r.get('referenceUncertaintyUs',100000)<=1000 for r in rows):failures.append('缺少独立证据或参考不确定度超过1ms')
if not profile.get('realMeasurements') or not profile.get('reviewedBy') or not profile.get('referenceDescription'):failures.append('未由操作员声明真实测量、参考方法和审核人')
report={'measurementCount':len(rows),'biasUs':statistics.mean(errors) if errors else None,
 'p95AbsoluteUs':absolute[math.ceil(.95*len(absolute))-1] if absolute else None,'maxAbsoluteUs':max(absolute) if absolute else None,
 'conditions':sorted(conditions),'numericThresholdPassed':not failures,'failures':failures,
 'profileSha256':hashlib.sha256(raw).hexdigest(),'note':'仅审核所提供数据的数值条件；实测真实性、参考躯干事件与配置一致性须人工核对。此报告不导入配置，不修改成绩。'}
a.output.parent.mkdir(parents=True,exist_ok=True);a.output.write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps(report,ensure_ascii=False,indent=2))
raise SystemExit(1 if failures else 0)
