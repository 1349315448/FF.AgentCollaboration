"""Run the two real CLIs, lose a callback acknowledgement, then start two fresh coordinators."""
import argparse
from contextlib import closing
import hashlib
import json
from pathlib import Path
import sqlite3
import subprocess


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("executable", type=Path)
    parser.add_argument("run_dir", type=Path)
    parser.add_argument("--copilot-model")
    args = parser.parse_args()
    root = args.run_dir.resolve()
    executable = args.executable.resolve()
    options = ["--copilot-model", args.copilot_model] if args.copilot_model else []

    def invoke(verb, *extra):
        return subprocess.run([executable, verb, root, *extra], check=False).returncode

    def artifacts():
        return {p.name: hashlib.sha256(p.read_bytes()).hexdigest() for p in (root / "handoffs").glob("*.md")}

    code = invoke("run", "--disconnect-on-callback", *options)
    assert code == 75, f"expected actual native disconnect exit 75, got {code}"
    fault = json.loads((root / "disconnect.json").read_text(encoding="utf-8"))
    with closing(sqlite3.connect((root / "state.sqlite").as_uri() + "?mode=ro", uri=True)) as db:
        row = db.execute("SELECT state,attempts FROM events WHERE id=?", (fault["EventId"],)).fetchone()
    assert row == ("in_flight", 1), row
    before = artifacts()
    assert len(before) == 6, before
    for verb in ("recover", "resume"):
        assert invoke(verb) == 0, verb
        report = json.loads((root / "acceptance.json").read_text(encoding="utf-8"))
        assert report["success"] and all(e["Attempts"] == 1 for e in report["events"]), report
        event = next(e for e in report["events"] if e["Id"] == fault["EventId"])
        assert (event["Target"], event["Phase"], event["State"]) == ("codex", "callback", "recovered"), event
        proof = next(p for p in report["protocol"] if p["EventId"] == fault["EventId"])
        assert proof["HistoryMatches"] == 1 and proof["NativeTurn"], proof
        assert artifacts() == before, "document content changed during recovery"
    print("PASS: native exit 75, in_flight before recovery, recover=0, resume=0, one native input, six unchanged documents")


if __name__ == "__main__":
    main()
