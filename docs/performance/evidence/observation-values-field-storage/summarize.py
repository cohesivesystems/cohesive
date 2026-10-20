from pathlib import Path
import json,csv,statistics
root=Path('/private/tmp/cohesive-vector-runs');rows=[]
for path in sorted(root.glob('*/results/*full.json')):
 repeat,warmup,width,method,side=path.parent.parent.name.split('-')
 benchmarks=json.loads(path.read_text())['Benchmarks']
 assert len(benchmarks)==1,(path,len(benchmarks))
 benchmark=benchmarks[0]
 rows.append(dict(repeat=int(repeat),warmup=int(warmup),width=int(width),method=method,side=side,mean_ns=benchmark['Statistics']['Mean'],allocated_bytes=benchmark['Memory']['BytesAllocatedPerOperation']))
if rows:
 with (root/'summary.csv').open('w') as output:
  writer=csv.DictWriter(output,fieldnames=list(rows[0]));writer.writeheader();writer.writerows(rows)
print('| Warmup | Fields | Method | Pairs | Dictionary ns | Vector ns | Change | Dictionary B | Vector B |')
print('|---:|---:|---|---:|---:|---:|---:|---:|---:|')
for warmup,width,method in sorted({(r['warmup'],r['width'],r['method']) for r in rows}):
 groups=[[r for r in rows if r['warmup']==warmup and r['width']==width and r['method']==method and r['side']==side] for side in ('control','candidate')]
 if not all(groups) or len(groups[0])!=len(groups[1]): continue
 a,b=[statistics.median(r['mean_ns'] for r in group) for group in groups]
 alloc=[statistics.median(r['allocated_bytes'] for r in group) for group in groups]
 print(f'| {warmup} | {width} | {method} | {len(groups[0])} | {a:.2f} | {b:.2f} | {(b/a-1)*100:+.1f}% | {alloc[0]:g} | {alloc[1]:g} |')
