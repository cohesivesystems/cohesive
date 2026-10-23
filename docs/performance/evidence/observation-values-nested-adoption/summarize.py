from pathlib import Path
import csv,statistics,sys
root=Path(sys.argv[1] if len(sys.argv)>1 else '/private/tmp/cohesive-nested-adoption-final-runs');rows=list(csv.DictReader((root/'summary.csv').open()))
for workload in dict.fromkeys(r['workload'] for r in rows):
 print(workload)
 for route in ('before','after'):
  g=[r for r in rows if r['workload']==workload and r['route']==route]
  if not g:continue
  v=[float(r['mean_ns'])/1000 for r in g];b=[float(r['allocated_bytes']) for r in g]
  print(route,len(g),round(statistics.median(v),3),'us',round(min(v),3),round(max(v),3),'range',statistics.median(b),'B')
