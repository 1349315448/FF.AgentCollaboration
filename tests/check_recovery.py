"""Black-box regression for a lost acknowledgement, using the same CLI and SQLite file as a live run."""
import pathlib
from contextlib import closing
import sqlite3
import subprocess
import sys
import tempfile


def run(*args):
    return subprocess.run(["dotnet", sys.argv[1], *map(str, args)], capture_output=True, text=True)


with tempfile.TemporaryDirectory(prefix="lx-p0-recovery-") as temp:
    root = pathlib.Path(temp) / "case"
    initialized = run("init", root)
    assert initialized.returncode == 0, initialized.stdout + initialized.stderr
    with closing(sqlite3.connect(root / "state.sqlite")) as db:
        db.execute("UPDATE events SET state='in_flight', attempts=1 WHERE phase='open' AND target='codex'")
        db.commit()
    for command in ("recover", "resume", "recover"):
        result = run(command, root)
        assert result.returncode == 2, result.stdout + result.stderr
    with closing(sqlite3.connect(root / "state.sqlite")) as db:
        rows = db.execute("SELECT target, state, attempts FROM events ORDER BY target").fetchall()
    assert rows == [("codex", "delivery_uncertain", 1), ("copilot", "pending", 0)], rows
    print("PASS: recover and resume preserved an uncertain send and its pending peer")
