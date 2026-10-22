from pathlib import Path
import csv,statistics,sys
root=Path(sys.argv[1] if len(sys.argv)>1 else '/private/tmp/cohesive-nested-runs')
rows=list(csv.DictReader((root/'summary.csv').open()))
def group(scenario,width,count,method):return [r for r in rows if r['scenario']==scenario and int(r['width'])==width and int(r['count'])==count and r['method']==method]
def median(items,key):return statistics.median(float(item[key]) for item in items)
print('| Scenario | Width | Rows | Repeats | Existing ns | Shared ns | Time change | B existing / shared |')
print('|---|---:|---:|---:|---:|---:|---:|---:|')
for scenario,width,count in dict.fromkeys((r['scenario'],int(r['width']),int(r['count'])) for r in rows):
 a,b=group(scenario,width,count,'ParseExisting'),group(scenario,width,count,'ParseShared')
 if not a or len(a)!=len(b):continue
 before,after=median(a,'mean_ns'),median(b,'mean_ns')
 print(f'| {scenario} | {width} | {count} | {len(a)} | {before:.2f} | {after:.2f} | {(after/before-1)*100:+.1f}% | {median(a,"allocated_bytes"):g} / {median(b,"allocated_bytes"):g} |')
print('\n| Width | Rows | Repeats | Existing names ns | Shared names ns | Bound ordinals ns | Bound change | B all routes |\n|---:|---:|---:|---:|---:|---:|---:|---:|')
for width in (4,16,32):
 groups=[group('array',width,128,method) for method in ('ReadExisting','ReadSharedNames','ReadOrdinals')]
 if not all(groups) or len({len(g) for g in groups})!=1:continue
 a,b,c=[median(g,'mean_ns') for g in groups]
 print(f'| {width} | 128 | {len(groups[0])} | {a:.2f} | {b:.2f} | {c:.2f} | {(c/a-1)*100:+.1f}% | '+ ' / '.join(f'{median(g,"allocated_bytes"):g}' for g in groups)+' |')
print('\n| Width | Rows | Repeats | Existing parse+reads ns | Shared parse+bound reads ns | Change | B existing / shared |\n|---:|---:|---:|---:|---:|---:|---:|')
for count in (128,4096):
 a,b=group('array',16,count,'ParseExistingAndRead'),group('array',16,count,'ParseSharedAndRead')
 if not a or len(a)!=len(b):continue
 before,after=median(a,'mean_ns'),median(b,'mean_ns')
 print(f'| 16 | {count} | {len(a)} | {before:.2f} | {after:.2f} | {(after/before-1)*100:+.1f}% | {median(a,"allocated_bytes"):g} / {median(b,"allocated_bytes"):g} |')
