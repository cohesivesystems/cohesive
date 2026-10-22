"""Matched dependency-closure qualification; optional root argument selects frozen folders."""
from pathlib import Path
import subprocess, json, hashlib, sys, csv
root = Path(sys.argv[1] if len(sys.argv) > 1 else '/private/tmp/cohesive-main-final-runs')
root.mkdir(exist_ok=True)
folders = {side: Path('/private/tmp/cohesive-main-final-' + side) for side in ('control', 'current')}
manifest = {side: {p.name: hashlib.sha256(p.read_bytes()).hexdigest() for p in sorted(folder.iterdir()) if p.suffix == '.dll' or p.name.endswith(('.deps.json', '.runtimeconfig.json'))} for side, folder in folders.items()}
assert manifest['control'].keys() == manifest['current'].keys()
assert [name for name, digest in manifest['control'].items() if manifest['current'][name] != digest] == ['Cohesive.Relations.dll', 'Cohesive.Storage.dll', 'Cohesive.dll']
(root / 'manifest.json').write_text(json.dumps(manifest, indent=2) + '\n')
cases = []
for method, workloads in [('ParseDirect', ('flat', 'nested', 'collection', 'large')), ('ParseMapWrite', ('flat', 'large'))]:
 for workload in workloads:
  cases.append((method + '-' + workload, f'*ObservationValuePipelineBenchmarks.{method}(*{workload}*)'))
for selector in ('False', 'True'):
 cases.append(('aggregate-' + selector, f'*ObservationAggregateBenchmarks.Evaluate(Function: "sum", Count: 4096, Selector: {selector})'))
for workload in ('flat', 'nested', 'collection', 'large'):
 cases.append(('portable-' + workload, f'*ObservationBoundaryConversionBenchmarks.DecodePortable(*{workload}*)'))
for method in ('Encode', 'DecodeAndAdmit'):
 for workload in ('flat', 'large'):
  cases.append((method + '-' + workload, f'*ObservationCheckpointBenchmarks.{method}(*{workload}*)'))
for workload in ('unique', 'escaped'):
 cases.append(('strings-' + workload, f'*ObservationJsonStringReuseBenchmarks.ParseDirect(*{workload}*)'))
def run(repeat, case, side, pattern):
 stem = root / f'{repeat}-{case}-{side}'
 if not list(stem.glob('results/*full.json')):
  with stem.with_suffix('.log').open('w') as output:
   subprocess.run(['dotnet', str(folders[side] / 'Cohesive.Relations.Benchmarks.dll'), '--filter', pattern, '--inProcess', '--warmupCount', '4', '--iterationCount', '6', '--iterationTime', '200', '--artifacts', str(stem)], stdout=output, stderr=subprocess.STDOUT, check=True)
 results = list(stem.glob('results/*full.json'))
 assert len(results) == 1, (stem, results)
 benchmarks = json.loads(results[0].read_text())['Benchmarks']
 assert len(benchmarks) == 1 and benchmarks[0]['Statistics'], stem
 b = benchmarks[0]
 row = dict(repeat=repeat, case=case, side=side, full_name=b['FullName'], mean_ns=b['Statistics']['Mean'], allocated_bytes=b['Memory']['BytesAllocatedPerOperation'])
 with (root / 'summary.csv').open('a', newline='') as output:
  writer = csv.DictWriter(output, fieldnames=row.keys(), lineterminator='\n')
  if output.tell() == 0: writer.writeheader()
  writer.writerow(row)
 print(stem.name, flush=True)
for repeat in (1, 2, 3):
 for case, pattern in cases:
  for side in (('control', 'current') if repeat % 2 else ('current', 'control')):
   run(repeat, case, side, pattern)
 for count in (1, 4096):
  routes = [('control', 'compatibility'), ('current', 'compatibility'), ('current', 'prepared')]
  if repeat % 2 == 0: routes.reverse()
  for side, route in routes:
   run(repeat, f'response-{count}-{route}', side, f'*ObservationMainResponseQualificationBenchmarks.Read(Count: {count}, Route: "{route}")')
 for method in (('IndexedNamePlan', 'StrictOrdinalPlan') if repeat % 2 else ('StrictOrdinalPlan', 'IndexedNamePlan')):
  run(repeat, 'strict-flat-' + method, 'current', f'*ObservationJsonMaterializationBenchmarks.{method}(*flat*)')
print('Complete', flush=True)
