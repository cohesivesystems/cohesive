from pathlib import Path
import csv,statistics,sys
root=Path(sys.argv[1] if len(sys.argv)>1 else '/private/tmp/cohesive-nested-density-runs')
rows=list(csv.DictReader((root/'summary.csv').open()))
def group(w,n,s,p,m):return [r for r in rows if (int(r['width']),int(r['count']),r['scenario'],int(r['present_percent']),r['method'])==(w,n,s,p,m)]
def med(items,key):return statistics.median(float(r[key]) for r in items)
print('| Scenario | Width | Rows | Present % | Repeats | Existing ns | Fixed ns | Density ns | Density vs existing | Density vs fixed | B existing / fixed / density |')
print('|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|')
for w,n,s,p in dict.fromkeys((int(r['width']),int(r['count']),r['scenario'],int(r['present_percent'])) for r in rows):
 if s=='case-array':continue
 groups=[group(w,n,s,p,m) for m in ('ParseExisting','ParseSharedSeparated','ParseDensityAware')]
 if not all(groups) or len({len(g) for g in groups})!=1:continue
 a,b,c=[med(g,'mean_ns') for g in groups]
 print(f'| {s} | {w} | {n} | {p} | {len(groups[0])} | {a:.2f} | {b:.2f} | {c:.2f} | {(c/a-1)*100:+.1f}% | {(c/b-1)*100:+.1f}% | '+ ' / '.join(f'{med(g,"allocated_bytes"):g}' for g in groups)+' |')
print('\n| Fallback route | Repeats | ns | vs existing | B |\n|---|---:|---:|---:|---:|')
a=group(16,128,'case-array',100,'ParseExisting')
if a:
 for m in ('ParseExisting','ParseShared','ParseSharedSeparated','ParseDensityAware'):
  g=group(16,128,'case-array',100,m)
  if len(g)!=len(a):continue
  v=med(g,'mean_ns');print(f'| {m} | {len(g)} | {v:.2f} | {(v/med(a,"mean_ns")-1)*100:+.1f}% | {med(g,"allocated_bytes"):g} |')
print('\n| Sparse read route | Repeats | ns | B |\n|---|---:|---:|---:|')
for m in ('ReadExisting','ReadOrdinals','ReadDensityAware'):
 g=group(16,128,'array',25,m)
 if g:print(f'| {m} | {len(g)} | {med(g,"mean_ns"):.2f} | {med(g,"allocated_bytes"):g} |')
print('\n| Sparse combined route | Repeats | ns | B |\n|---|---:|---:|---:|')
for m in ('ParseExistingAndRead','ParseSharedSeparatedAndRead','ParseDensityAwareAndRead'):
 g=group(32,128,'array',25,m)
 if g:print(f'| {m} | {len(g)} | {med(g,"mean_ns"):.2f} | {med(g,"allocated_bytes"):g} |')
