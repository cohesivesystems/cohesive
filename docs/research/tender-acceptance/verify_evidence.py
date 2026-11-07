"""Check source drift without copying application code. Usage: python verify_evidence.py SOURCE_ROOT."""
import hashlib
import json
from pathlib import Path
import sys


def verify(root):
    evidence = json.loads(Path(__file__).with_name("evidence.json").read_text())
    failures = []
    for entry in evidence["entries"]:
        path = root / entry["path"]
        if not path.is_file():
            failures.append(f'{entry["id"]}: missing {entry["path"]}')
            continue
        raw = path.read_bytes()
        excerpt = b"".join(raw.splitlines(keepends=True)[entry["startLine"] - 1:entry["endLine"]])
        for label, value in (("file", raw), ("excerpt", excerpt)):
            if hashlib.sha256(value).hexdigest() != entry[label + "Sha256"]:
                failures.append(f'{entry["id"]}: {label} changed in {entry["path"]}')
    return failures


if __name__ == "__main__":
    if len(sys.argv) != 2:
        raise SystemExit("Usage: python verify_evidence.py SOURCE_ROOT (directory containing Features)")
    errors = verify(Path(sys.argv[1]))
    if errors:
        raise SystemExit("\n".join(errors))
    print("All evidence anchors match the inspected source bytes.")
