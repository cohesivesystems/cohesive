import contextlib
import csv
import io
from pathlib import Path
import runpy
import sys
import tempfile
import types
import unittest
from unittest.mock import patch

SCRIPT = Path(__file__).with_name("compare-ari.py")


class ComparisonProtocolTests(unittest.TestCase):
    def compare(self, iterations, legacy=False):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            harness = root / "ProtocolPreparation.dll"
            harness.touch()
            output = root / "counters.csv"
            commands = []

            def run(command, **kwargs):
                commands.append(command)
                count = 2 if legacy or len(command) == 3 else int(command[-1])
                phases = ["cold", "repeated"] + [f"warm-{i}" for i in range(3, count + 1)]
                text = "source,phase,allocated_bytes,elapsed_ms,cpu_ms\n" + "".join(
                    f"{command[2]},{phase},1234,2.5,3.5\n" for phase in phases)
                return types.SimpleNamespace(stdout=text)

            arguments = [str(SCRIPT), "--baseline", str(harness), "--candidate", str(harness),
                         "--output", str(output), "--runs", "1", "--iterations", str(iterations)]
            with patch.object(sys, "argv", arguments), patch("subprocess.run", side_effect=run), contextlib.redirect_stdout(io.StringIO()):
                runpy.run_path(str(SCRIPT), run_name="__main__")
            with output.open() as source:
                return list(csv.DictReader(source)), commands

    def test_default_remains_compatible_with_the_original_harness(self):
        rows, commands = self.compare(2, legacy=True)
        self.assertEqual(8, len(rows))
        self.assertTrue(all(len(command) == 3 for command in commands))
        self.assertTrue(all(row["cpu_ms"] == "3.5" for row in rows))

    def test_long_runs_preserve_every_phase_for_both_sources_and_variants(self):
        rows, commands = self.compare(8)
        self.assertEqual(32, len(rows))
        self.assertEqual({"cold", "repeated", *[f"warm-{i}" for i in range(3, 9)]}, {row["phase"] for row in rows})
        self.assertTrue(all(command[-2:] == ["--iterations", "8"] for command in commands))

    def test_stale_harness_cannot_silently_supply_a_shorter_warmup(self):
        with self.assertRaises(RuntimeError):
            self.compare(8, legacy=True)


if __name__ == "__main__":
    unittest.main()
