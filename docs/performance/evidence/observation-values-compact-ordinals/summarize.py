from pathlib import Path
import csv,statistics
root=Path('/private/tmp/cohesive-packed-runs');rows=list(csv.DictReader((root/'summary.csv').open()))
for key in dict.fromkeys((r['scenario'],r['width'],r['count'],r['present_percent']) for r in rows):
 print('\n',key)
 for method in dict.fromkeys(r['method'] for r in rows):
  g=[r for r in rows if (r['scenario'],r['width'],r['count'],r['present_percent'])==key and r['method']==method]
  if g:
   v=[float(r['mean_ns']) for r in g];print(method,len(g),round(statistics.median(v)/1000,3),'us',round(min(v)/1000,3),round(max(v)/1000,3),'range',statistics.median(float(r['allocated_bytes']) for r in g),'B')
