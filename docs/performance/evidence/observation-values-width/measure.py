from pathlib import Path
import subprocess, json, hashlib
root = Path('/private/tmp/cohesive-width-runs')
root.mkdir(exist_ok=True)
folders = {side: Path('/private/tmp/cohesive-width-' + side) for side in ('control', 'candidate')}
manifest = {side: {p.name: hashlib.sha256(p.read_bytes()).hexdigest() for p in sorted(folder.iterdir()) if p.suffix == '.dll' or p.name.endswith(('.deps.json', '.runtimeconfig.json'))} for side, folder in folders.items()}
assert [name for name, digest in manifest['control'].items() if manifest['candidate'].get(name) != digest] == ['Cohesive.dll']
(root / 'manifest.json').write_text(json.dumps(manifest, indent=2))
for repeat in range(1, 4):
 for warmup in (4, 32):
  for width in (1, 4, 8, 9, 16, 32):
   for side in (('control', 'candidate') if repeat % 2 else ('candidate', 'control')):
    stem = root / f'{repeat}-{warmup}-{width}-{side}'
    with stem.with_suffix('.log').open('w') as output:
     subprocess.run(['dotnet', str(folders[side] / 'Cohesive.Relations.Benchmarks.dll'), '--filter', f'*WidthBenchmarks*Width: {width}, WarmupWidth: {warmup}*', '--inProcess', '--warmupCount', '4', '--iterationCount', '6', '--iterationTime', '200', '--artifacts', str(stem)], stdout=output, stderr=subprocess.STDOUT, check=True)
    print(stem.name, flush=True)
print('Complete', flush=True)
