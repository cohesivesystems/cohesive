from pathlib import Path
import subprocess,json,csv
r=Path('/private/tmp/sqlite-ingestion');rows=[]
for repeat in [1,2,3]:
 methods=['CanonicalIngestion','OrdinalIngestion']
 if repeat%2==0:methods.reverse()
 for method in methods:
  name=f'{repeat}-{method}';out=r/name
  with (r/(name+'.log')).open('w') as log:
   subprocess.run(['dotnet','/private/tmp/cohesive-automapper-gap/src/Cohesive.Relations.Benchmarks/bin/Release/net10.0/Cohesive.Relations.Benchmarks.dll','--filter',f'*SqliteOrdinalIngestionBenchmarks.{method}(RowCount: 1024)','--inProcess','--warmupCount','4','--iterationCount','6','--iterationTime','200','--artifacts',str(out)],stdout=log,stderr=subprocess.STDOUT,check=True)
  data=json.loads(next(out.glob('results/*full.json')).read_text())['Benchmarks'];assert len(data)==1 and data[0]['Statistics'];v=data[0]
  rows.append(dict(repeat=repeat,method=method,ns=v['Statistics']['Mean'],bytes=v['Memory']['BytesAllocatedPerOperation']))
  with (r/'timing.csv').open('w') as f:
   w=csv.DictWriter(f,fieldnames=rows[0]);w.writeheader();w.writerows(rows)
  print(name,rows[-1],flush=True)
