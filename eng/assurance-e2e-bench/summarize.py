"""Print reproducible median service metrics without discarding raw trials."""
import json
import statistics
import sys
from collections import defaultdict

with open(sys.argv[1]) as stream:
    document = json.load(stream)
groups = defaultdict(list)
for run in document["Results"]:
    case = run["Scenario"]
    groups[tuple(case[key] for key in ("Mode", "Shape", "Rows", "Batch", "Padding"))].append(run)
print("| Mode / shape | Rows | Batch | Padding | Service ms median [min–max] | Allocated MiB | Producer p95 µs | Consumer p95 ms | Logical writes MiB | Restore ms |")
print("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|")
for (mode, shape, rows, batch, padding), runs in groups.items():
    def median(key):
        return statistics.median(run[key] for run in runs)
    times = [run["ServiceMilliseconds"] for run in runs]
    print(f"| {mode} / {shape} | {rows} | {batch} | {padding} | "
          f"{median('ServiceMilliseconds'):.2f} [{min(times):.2f}–{max(times):.2f}] | "
          f"{median('AllocatedBytes') / 2**20:.2f} | {median('ProducerP95Ms') * 1000:.2f} | "
          f"{median('ConsumerP95Ms'):.2f} | {median('JournalBytesWritten') / 2**20:.2f} | "
          f"{median('RestoreMilliseconds'):.2f} |")
