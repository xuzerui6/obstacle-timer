"""Analyze measured timing pairs without inventing accuracy data."""
import argparse
import csv
import json
import math
from collections import defaultdict
from datetime import datetime, timezone
from pathlib import Path

parser = argparse.ArgumentParser()
parser.add_argument('--input', type=Path, required=True)
parser.add_argument('--output', type=Path, required=True)
args = parser.parse_args()
groups = defaultdict(list)
seen = set()
with args.input.open(encoding='utf-8-sig', newline='') as stream:
    for line, row in enumerate(csv.DictReader(stream), 2):
        required = ('device', 'mode', 'condition', 'run_id', 'estimated_us', 'reference_us', 'reference_uncertainty_us')
        if any(not row.get(key, '').strip() for key in required):
            raise SystemExit(f'Row {line}: missing required field')
        key = (row['device'], row['mode'], row['run_id'])
        if key in seen:
            raise SystemExit(f'Row {line}: duplicate measurement {key}')
        seen.add(key)
        estimated, reference, uncertainty = (float(row[key]) for key in required[-3:])
        if not all(math.isfinite(x) for x in (estimated, reference, uncertainty)) or min(estimated, reference, uncertainty) < 0:
            raise SystemExit(f'Row {line}: invalid measurement')
        groups[(row['device'], row['mode'])].append((estimated-reference, uncertainty, row['condition']))
reports = []
for (device, mode), rows in sorted(groups.items()):
    errors = sorted(abs(r[0]) for r in rows)
    conditions = sorted({r[2] for r in rows})
    qualified = len(rows) >= 100 and max(errors) <= 10_000 and max(r[1] for r in rows) <= 1_000 and all(c in conditions for c in ('normal', 'congested', 'long', 'frame-gap'))
    reports.append(dict(device=device, mode=mode, samples=len(rows), bias_us=sum(r[0] for r in rows)/len(rows),
        absolute_p95_us=errors[math.ceil(.95*len(errors))-1], absolute_max_us=max(errors), conditions=conditions,
        passes_10ms_target_for_measured_batch=qualified, competition_certified=False))
report = dict(created_utc=datetime.now(timezone.utc).isoformat(), source=str(args.input), measured_groups=reports,
              note='Only measured configurations and conditions are covered. Statistical results are not competition certification.')
args.output.parent.mkdir(parents=True, exist_ok=True)
args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
print(f'{len(reports)} measured groups analyzed; report: {args.output}')
