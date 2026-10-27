#!/usr/bin/env python3
"""Example-local TLC runner. Successful checks concern only the exported finite model."""
import hashlib
import json
import pathlib
import subprocess
import signal
import sys
import uuid

root = pathlib.Path(sys.argv[1]).resolve()
jar = pathlib.Path(sys.argv[2]).resolve()
expected_hash = '936a262061c914694dfd669a543be24573c45d5aa0ff20a8b96b23d01e050e88'
sha = lambda path: hashlib.sha256(path.read_bytes()).hexdigest()
if sha(jar) != expected_hash:
    raise SystemExit('TLC 1.7.4 jar checksum mismatch')
# Keep the exact manually reviewed correspondence sources alongside the model.
repo = pathlib.Path(__file__).resolve().parents[4]
source_paths = (
    'src/adapters/Cohesive.Adapters.Postgres/PostgresTransitionReceipts.cs',
    'src/Cohesive.Storage/EntityTransitionCommitProtocol.cs',
    'src/Cohesive.Assurance/EffectUniquenessEvaluator.cs',
    'eng/examples/replay-assurance/ReservationReplayDemo.cs',
    'eng/examples/replay-assurance/ReservationClaim.cs',
    'eng/examples/replay-assurance/ReservationFormalExport.cs',
    'eng/examples/replay-assurance/formal/check.py',
    'eng/examples/replay-assurance/run-formal.sh',
)
for relative in source_paths:
    destination = root / 'correspondence-sources' / relative
    destination.parent.mkdir(parents=True, exist_ok=True)
    with destination.open('xb') as output:
        output.write((repo / relative).read_bytes())

def interrupted(signum, frame):
    raise KeyboardInterrupt(f'Interrupted by signal {signum}')

signal.signal(signal.SIGTERM, interrupted)
manifest = json.loads((root / 'manifest.json').read_text())
inputs = {str(p.relative_to(root)): sha(p) for p in sorted(root.rglob('*')) if p.is_file()}
results = []
for mode in ('AtomicReceipt', 'UnprotectedRetry', 'SplitReceipt', 'LostAckReplayWitness', 'ClaimConformance'):
    name = 'cohesive-tlc-' + uuid.uuid4().hex
    module = 'ClaimConformance' if mode == 'ClaimConformance' else 'ReservationProtocol'
    command = ['docker', 'run', '--rm', '--name', name, '--network', 'none',
               '--memory', '512m', '--cpus', '2',
               '--volume', str(root) + ':/work', '--volume', str(jar) + ':/tools/tlc.jar:ro',
               '--workdir', '/work', 'eclipse-temurin:21-jre',
               'java', '-XX:+UseParallelGC', '-Xmx384m', '-jar', '/tools/tlc.jar',
               '-workers', '1', '-seed', '1', '-fp', '0', '-tool',
               '-metadir', '/work/' + mode + '.states', '-config', mode + '.cfg', module]
    log = root / (mode + '.log')
    code = None
    failure = None
    try:
        with log.open('x') as output:
            completed = subprocess.run(command, stdout=output, stderr=subprocess.STDOUT, timeout=120)
        code = completed.returncode
    except subprocess.TimeoutExpired:
        failure = 'Checker timeout; no satisfaction inferred'
    finally:
        subprocess.run(['docker', 'rm', '-f', name], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    text = log.read_text()
    invariant = ('Conformance' if mode == 'ClaimConformance' else
                 'NoLostAckReplay' if mode == 'LostAckReplayWitness' else 'Claim')
    violation = code == 12 and f'Invariant {invariant} is violated.' in text
    complete = code == 0 and 'Model checking completed. No error has been found.' in text
    expected = complete if mode in ('AtomicReceipt', 'ClaimConformance') else violation
    result = dict(Mode=mode, ExitCode=code, ExpectedOutcomeObserved=expected,
                  Status=('WitnessFound' if mode == 'LostAckReplayWitness' and violation else
                          'Violated' if violation else 'Satisfied' if complete else 'Inconclusive'),
                  Scope='All reachable states of the exported finite model' if complete else 'See retained trace/log',
                  Invariant=invariant, Log=log.name, LogSha256=sha(log), Failure=failure)
    results.append(result)
    print(f'{mode}: {result["Status"]} (expected outcome: {expected})', flush=True)
report = dict(Manifest=manifest, InputSha256=inputs, Tool=dict(Name='TLC', Version='1.7.4',
              JarSha256=sha(jar), JavaImage='eclipse-temurin:21-jre', Workers=1,
              Seed=1, FingerprintPolynomial=0, TimeoutSeconds=120), Results=results,
              Limit='Bounded model evidence, not implementation verification or an unbounded proof')
with (root / 'bounded-verification.json').open('x') as output:
    json.dump(report, output, indent=2)
if not all(r['ExpectedOutcomeObserved'] for r in results):
    raise SystemExit('Unexpected checker outcome; inspect retained logs')
