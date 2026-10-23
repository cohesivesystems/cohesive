from pathlib import Path
import json,csv,statistics
root=Path('/private/tmp/cohesive-layout-runs');rows=[]
for path in sorted(root.glob('*/results/*full.json')):
 repeat,width,reads,method=path.parent.parent.name.split('-')
 benchmarks=json.loads(path.read_text())['Benchmarks'];assert len(benchmarks)==1
 b=benchmarks[0]
 rows.append(dict(repeat=int(repeat),width=int(width),reads=int(reads),method=method,mean_ns=b['Statistics']['Mean'],allocated_bytes=b['Memory']['BytesAllocatedPerOperation']))
if rows:
 with (root/'summary.csv').open('w') as output:
  writer=csv.DictWriter(output,fieldnames=list(rows[0]));writer.writeheader();writer.writerows(rows)
print('| Fields | Reads | Pairs | Generic ns | Shared+ordinal ns | Change | Generic B | Shared B |')
print('|---:|---:|---:|---:|---:|---:|---:|---:|')
for width in (4,16,32):
 for reads in (0,1,16,64):
  groups=[[r for r in rows if r['width']==width and r['reads']==reads and r['method']==method] for method in ('ParseGenericAndRead','ParseSharedAndRead')]
  if not all(groups) or len(groups[0])!=len(groups[1]):continue
  a,b=[statistics.median(r['mean_ns'] for r in group) for group in groups]
  alloc=[statistics.median(r['allocated_bytes'] for r in group) for group in groups]
  print(f'| {width} | {reads} | {len(groups[0])} | {a:.2f} | {b:.2f} | {(b/a-1)*100:+.1f}% | {alloc[0]:g} | {alloc[1]:g} |')
print('\nLookup ns/query (16-query batch):')
for width in (4,16,32):
 groups=[[r for r in rows if r['width']==width and r['method']==method] for method in ('ReadGeneric','ReadSharedNames','ReadOrdinal')]
 if not all(groups):continue
 print(width,[(len(group),round(statistics.median(r['mean_ns'] for r in group)/16,3)) for group in groups])
