"""Compare frozen EdiDataPlane runtimes sequentially, alternating order and requiring identical output."""
import argparse
import csv
import json
from pathlib import Path
import subprocess


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--baseline", required=True, type=Path)
    parser.add_argument("--candidate", required=True, type=Path)
    parser.add_argument("--ari-root", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--runs", type=int, default=5)
    parser.add_argument("--iterations", type=int, default=8)
    parser.add_argument("--stops", type=int, nargs="+", default=[2, 16, 64])
    args = parser.parse_args()
    if args.runs < 1 or not 1 <= args.iterations <= 1000 or any(not 2 <= count <= 64 for count in args.stops):
        parser.error("runs must be positive, iterations 1..1000 and stops 2..64")
    for harness in (args.baseline, args.candidate):
        if not harness.is_file():
            parser.error(f"Missing harness: {harness}")
    expected = {}
    retention = []
    args.output.parent.mkdir(parents=True, exist_ok=True)
    with args.output.open("w", newline="") as output:
        writer = csv.writer(output, lineterminator="\n")
        writer.writerow(["variant", "run", "stage", "phase", "stops", "iterations", "allocated_bytes", "elapsed_ms", "input_bytes", "output_bytes", "sha256"])
        for run in range(1, args.runs + 1):
            variants = [("baseline", args.baseline), ("candidate", args.candidate)]
            if run % 2 == 0:
                variants.reverse()
            for stops in args.stops:
                for variant, harness in variants:
                    result = subprocess.run(["dotnet", str(harness.resolve()), str(args.ari_root.resolve()), str(stops), str(args.iterations)],
                                            check=True, capture_output=True, text=True)
                    rows = list(csv.DictReader(result.stdout.splitlines()))
                    if len(rows) != 9 or [row["phase"] for row in rows] != ["cold", "cold"] + ["warm"] * 7:
                        raise RuntimeError(f"Unexpected benchmark output: {result.stdout}")
                    for row in rows:
                        if int(row["stops"]) != stops:
                            raise RuntimeError("Wrong workload in benchmark output")
                        if row["stage"] == "pipeline":
                            identity = (row["input_bytes"], row["output_bytes"], row["sha256"])
                            if not row["sha256"] or expected.setdefault(stops, identity) != identity:
                                raise RuntimeError(f"Output differs for {stops} stops: {identity}")
                        writer.writerow([variant, run, *[row[key] for key in ["stage", "phase", "stops", "iterations", "allocated_bytes", "elapsed_ms", "input_bytes", "output_bytes", "sha256"]]])
                    output.flush()
                    retention.append(dict(variant=variant, run=run, stops=stops, stderr=result.stderr))
            print(f"Completed pair {run}/{args.runs}", flush=True)
    args.output.with_suffix(".retention.json").write_text(json.dumps(retention, indent=2) + "\n")


if __name__ == "__main__":
    main()
