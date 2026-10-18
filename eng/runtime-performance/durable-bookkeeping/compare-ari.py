"""Compare already-built, isolated Ari ProtocolPreparation harnesses in alternating fresh processes."""
import argparse
import csv
from pathlib import Path
import subprocess

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--baseline", type=Path, required=True)
parser.add_argument("--candidate", type=Path, required=True)
parser.add_argument("--output", type=Path, required=True)
parser.add_argument("--runs", type=int, default=5)
parser.add_argument("--iterations", type=int, default=2)
args = parser.parse_args()
if args.runs < 1:
    parser.error("--runs must be positive")
if not 2 <= args.iterations <= 64:
    parser.error("--iterations must be between 2 and 64")
for harness in [args.baseline, args.candidate]:
    if not harness.is_file():
        parser.error(f"Missing harness: {harness}")
args.output.parent.mkdir(parents=True, exist_ok=True)
with args.output.open("w", newline="") as output:
    writer = csv.writer(output, lineterminator="\n")
    writer.writerow(["variant", "run", "source", "phase", "allocated_bytes", "elapsed_ms", "cpu_ms"])
    for run in range(1, args.runs + 1):
        for source in ["x12-004010-204", "x12-005030-204"]:
            variants = [("baseline", args.baseline), ("candidate", args.candidate)]
            if run % 2 == 0:
                variants.reverse()
            for variant, harness in variants:
                command = ["dotnet", str(harness.resolve()), source]
                if args.iterations != 2:
                    command += ["--iterations", str(args.iterations)]
                result = subprocess.run(command, check=True, capture_output=True, text=True)
                rows = list(csv.DictReader(result.stdout.splitlines()))
                expected = {"cold", "repeated"} | {f"warm-{i}" for i in range(3, args.iterations + 1)}
                if len(rows) != args.iterations or {row["phase"] for row in rows} != expected:
                    raise RuntimeError(f"Unexpected harness output: {result.stdout}")
                for row in rows:
                    if row["source"] != source:
                        raise RuntimeError("Wrong workload in harness result")
                    writer.writerow([variant, run, source, row["phase"], int(row["allocated_bytes"]), row["elapsed_ms"], row.get("cpu_ms", "")])
                output.flush()
        print(f"Completed pair {run}/{args.runs}", flush=True)
