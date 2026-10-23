from pathlib import Path
import json, statistics, csv
root = Path('/private/tmp/cohesive-width-runs')
rows=[]
for repeat in range(1,4):
 for warmup in (4,32):
  for width in (1,4,8,9,16,32):
   for side in ('control','candidate'):
    paths=list((root/f'{repeat}-{warmup}-{width}-{side}'/'results').glob('*full.json'))
    if not paths: continue
    benchmark=json.loads(paths[0].read_text())['Benchmarks'][0]
    rows.append(dict(repeat=repeat,warmup=warmup,width=width,side=side,mean_ns=benchmark['Statistics']['Mean'],allocated_bytes=benchmark['Memory']['BytesAllocatedPerOperation']))
with (root/'summary.csv').open('w') as output:
 writer=csv.DictWriter(output,fieldnames=list(rows[0]));writer.writeheader();writer.writerows(rows)
print('| Warmup | Fields | Control ns | Split ns | Change | Bytes/op |')
print('|---:|---:|---:|---:|---:|---:|')
for warmup in (4,32):
 for width in (1,4,8,9,16,32):
  groups=[[r for r in rows if r['warmup']==warmup and r['width']==width and r['side']==side] for side in ('control','candidate')]
  if any(len(group)!=3 for group in groups):continue
  a,b=[statistics.median(r['mean_ns'] for r in group) for group in groups]
  assert len({r['allocated_bytes'] for group in groups for r in group})==1
  print(f'| {warmup} | {width} | {a:.2f} | {b:.2f} | {(b/a-1)*100:+.1f}% | {groups[0][0]["allocated_bytes"]} |')
