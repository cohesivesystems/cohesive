#!/usr/bin/env python3
"""Compare frozen original-X12 runtimes; never change packages or production assemblies."""
import argparse
import csv
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
from xml.sax.saxutils import escape

p = argparse.ArgumentParser(description=__doc__)
p.add_argument('--control', type=Path, required=True)
p.add_argument('--candidate', type=Path, required=True)
p.add_argument('--output', type=Path, required=True)
p.add_argument('--iterations', type=int, default=64)
p.add_argument('--runs', type=int, default=3)
p.add_argument('--sources', nargs='+', default=['x12-004010-204', 'x12-005030-204'])
p.add_argument('--mode', choices=['default', 'pgo-off', 'no-tiering'], default='default')
p.add_argument('--ari-program', type=Path, help='Original protocol-preparation/Program.cs; needed above 64 cases')
p.add_argument('--candidate-first', action='store_true')
p.add_argument('--changed-module', action='append', help='Expected differing DLL; repeat for multiple modules (default: Core and Relations)')
a = p.parse_args()
if not 2 <= a.iterations <= 4096 or a.runs < 1:
    p.error('Use 2..4096 iterations and at least one process pair')
env = os.environ.copy()
for prefix in ['DOTNET_', 'COMPlus_']:
    for name in ['TieredCompilation', 'TieredPGO', 'TC_QuickJit', 'TC_QuickJitForLoops']:
        if prefix + name in env:
            p.error('Remove ambient runtime tuning: ' + prefix + name)
if a.mode == 'pgo-off':
    env['DOTNET_TieredPGO'] = '0'
if a.mode == 'no-tiering':
    env['DOTNET_TieredCompilation'] = '0'
bases = {'control': a.control.resolve(), 'candidate': a.candidate.resolve()}
sha = lambda path: hashlib.sha256(path.read_bytes()).hexdigest()
modules = {v: {p.name: sha(p) for p in root.glob('*.dll')} for v, root in bases.items()}
assert modules['control'].keys() == modules['candidate'].keys()
assert {n for n in modules['control'] if modules['control'][n] != modules['candidate'][n]} == set(a.changed_module or ['Cohesive.dll', 'Cohesive.Relations.dll'])
a.output.mkdir(parents=True, exist_ok=False)
(a.output / 'modules.json').write_text(json.dumps(modules, indent=2) + '\n')
if a.iterations > 64:
    if a.ari_program is None:
        p.error('--ari-program is required above 64 iterations')
    # Keep the authoritative test, measurement loop and trace markers. Omit only unused CLI
    # modes so this isolated harness needs just the already-qualified test assembly reference.
    source = a.ari_program.read_text()
    loop = source[source.index('var source = args.Length'):source.index('// No returned models')]
    assert 'iterations is < 2 or > 64' in loop
    loop = loop.replace('> 64', '> 4096').replace('2 and 64', '2 and 4096').replace('2..64', '2..4096')
    source = ('using System.Diagnostics;\nusing System.Diagnostics.Tracing;\n'
              'using System.Globalization;\nusing Ari.Engine.Tests.Training;\n' + loop +
              source[source.index('// Opt-in trace markers'):])
    build = a.output / 'harness'
    build.mkdir()
    (build / 'Program.cs').write_text(source)
    reference = escape(str(bases['control'] / 'Ari.Engine.Tests.dll'))
    (build / 'Harness.csproj').write_text(
        '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType>'
        '<TargetFramework>net10.0</TargetFramework><AssemblyName>ProtocolPreparation</AssemblyName>'
        '<ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>'
        '<ItemGroup><Reference Include="Ari.Engine.Tests"><HintPath>' + reference +
        '</HintPath><Private>false</Private></Reference></ItemGroup></Project>')
    with (build / 'build.log').open('w') as log:
        subprocess.run(['dotnet', 'build', str(build / 'Harness.csproj'), '-c', 'Release', '-o', str(build / 'output')], check=True, stdout=log, stderr=subprocess.STDOUT)
    for v, base in list(bases.items()):
        dest = a.output / v
        dest.mkdir()
        for file in base.iterdir():
            if file.name in ['ProtocolPreparation.dll', 'ProtocolPreparation.pdb']:
                shutil.copy2(build / 'output' / file.name, dest / file.name)
            else:
                (dest / file.name).symlink_to(file, target_is_directory=file.is_dir())
        bases[v] = dest.resolve()
manifest = {'iterations': a.iterations, 'runs': a.runs, 'mode': a.mode, 'sources': a.sources,
            'candidateFirst': a.candidate_first, 'harnessSha256': {v: sha(d / 'ProtocolPreparation.dll') for v, d in bases.items()}}
assert len(set(manifest['harnessSha256'].values())) == 1
(a.output / 'run.json').write_text(json.dumps(manifest, indent=2) + '\n')
with (a.output / 'counters.csv').open('w', newline='') as f:
    writer = None
    for run in range(1, a.runs + 1):
        order = ['candidate', 'control'] if (run % 2 == 0) != a.candidate_first else ['control', 'candidate']
        for source in a.sources:
            for variant in order:
                result = subprocess.run(['dotnet', str(bases[variant] / 'ProtocolPreparation.dll'), source, '--iterations', str(a.iterations)], env=env, check=True, capture_output=True, text=True)
                rows = list(csv.DictReader(result.stdout.splitlines()))
                assert len(rows) == a.iterations
                if writer is None:
                    writer = csv.DictWriter(f, fieldnames=['variant', 'run'] + list(rows[0]))
                    writer.writeheader()
                writer.writerows(dict(variant=variant, run=run, **row) for row in rows)
                f.flush()
                print(source, variant, run, 'passed', flush=True)
