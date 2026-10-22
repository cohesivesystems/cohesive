from pathlib import Path
import json,csv,statistics
root=Path('/private/tmp/cohesive-adoption-runs');rows=[]
for path in sorted(root.glob('*/results/*full.json')):
 phase,repeat,workload,method=path.parent.parent.name.split('-')
 b=json.loads(path.read_text())['Benchmarks'];assert len(b)==1;b=b[0]
 rows.append(dict(phase=phase,repeat=int(repeat),workload=workload,method=method,mean_ns=b['Statistics']['Mean'],allocated_bytes=b['Memory']['BytesAllocatedPerOperation']))
if rows:
 with (root/'summary.csv').open('w') as output:
  writer=csv.DictWriter(output,fieldnames=list(rows[0]));writer.writeheader();writer.writerows(rows)
print('| Workload | Pairs | Indexed names ns | Indexed ordinals ns | MaterializeJson ns | Change vs names | B: names / ordinals / new |')
print('|---|---:|---:|---:|---:|---:|---|')
for workload in ('flat','nested','collection','large'):
 groups=[[r for r in rows if r['phase']=='adoption' and r['workload']==workload and r['method']==method] for method in ('IndexedNamePlan','IndexedOrdinalPlan','StrictOrdinalPlan')]
 if not all(groups) or len({len(group) for group in groups})!=1:continue
 a,b,c=[statistics.median(r['mean_ns'] for r in group) for group in groups]
 alloc=[statistics.median(r['allocated_bytes'] for r in group) for group in groups]
 print(f'| {workload} | {len(groups[0])} | {a:.2f} | {b:.2f} | {c:.2f} | {(c/a-1)*100:+.1f}% | {alloc[0]:g} / {alloc[1]:g} / {alloc[2]:g} |')
print('\n| Guard width | Pairs | Before ns | After ns | Change | B before / after |\n|---:|---:|---:|---:|---:|---|')
for width in ('4','16','32'):
 groups=[[r for r in rows if r['phase']=='guard' and r['workload']==width and r['method']==side] for side in ('control','current')]
 if not all(groups) or len(groups[0])!=len(groups[1]):continue
 a,b=[statistics.median(r['mean_ns'] for r in group) for group in groups]
 alloc=[statistics.median(r['allocated_bytes'] for r in group) for group in groups]
 print(f'| {width} | {len(groups[0])} | {a:.2f} | {b:.2f} | {(b/a-1)*100:+.1f}% | {alloc[0]:g} / {alloc[1]:g} |')
