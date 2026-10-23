"""Summarize the median of fresh-process means, retaining all process observations."""
from pathlib import Path
import csv, statistics, sys
root = Path(sys.argv[1] if len(sys.argv) > 1 else '/private/tmp/cohesive-main-final-runs')
rows = list(csv.DictReader((root / 'summary.csv').open()))
def group(case, side):
 selected = [row for row in rows if row['case'] == case and row['side'] == side]
 assert len({row['repeat'] for row in selected}) == len(selected), (case, side)
 return selected
def median(selected, key): return statistics.median(float(row[key]) for row in selected)
print('| Case | Repeats | Main ns | Branch ns | Time change | B main / branch |')
print('|---|---:|---:|---:|---:|---:|')
for case in dict.fromkeys(row['case'] for row in rows):
 a, b = group(case, 'control'), group(case, 'current')
 if not a or len(a) != len(b): continue
 before, after = median(a, 'mean_ns'), median(b, 'mean_ns')
 print(f'| {case} | {len(a)} | {before:.2f} | {after:.2f} | {(after / before - 1) * 100:+.1f}% | {median(a, "allocated_bytes"):g} / {median(b, "allocated_bytes"):g} |')
for count in (1, 4096):
 a, b = group(f'response-{count}-compatibility', 'control'), group(f'response-{count}-prepared', 'current')
 if a and len(a) == len(b):
  before, after = median(a, 'mean_ns'), median(b, 'mean_ns')
  print(f'Prepared response {count}: {before:.2f} -> {after:.2f} ns ({(after / before - 1) * 100:+.1f}%), {median(a, "allocated_bytes"):g} -> {median(b, "allocated_bytes"):g} B')
a, b = group('strict-flat-IndexedNamePlan', 'current'), group('strict-flat-StrictOrdinalPlan', 'current')
if a and len(a) == len(b):
 before, after = median(a, 'mean_ns'), median(b, 'mean_ns')
 print(f'Strict flat vs indexed names: {before:.2f} -> {after:.2f} ns ({(after / before - 1) * 100:+.1f}%), {median(a, "allocated_bytes"):g} -> {median(b, "allocated_bytes"):g} B')
