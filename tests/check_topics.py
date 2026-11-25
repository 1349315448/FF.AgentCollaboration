"""Isolated Chromium / native ConPTY topic and delivery regression. Requires Node Playwright."""
import os
from pathlib import Path
import subprocess
import sys
import tempfile
from unittest.mock import patch

from check_workbench import host


def verify(exe, case):
    fixture = case / "terminal.exe"
    compiler = Path(os.environ["WINDIR"]) / "Microsoft.NET/Framework/v4.0.30319/csc.exe"
    subprocess.run([compiler, "/nologo", "/main:TopicTerminalFixture", "/out:" + str(fixture),
                    Path(__file__).with_name("TopicTerminalFixture.cs"), Path(__file__).with_name("WorkbenchEngineFixture.cs")], check=True)
    state = case / "state"
    for project in ("one", "two", "third"):
        (case / project).mkdir()
    knowledge = case / "knowledge"
    existing = knowledge / "development-tasks" / "existing"
    existing.mkdir(parents=True)
    (existing / "PLAN.md").write_bytes(b"original plan\r\n")
    with patch.dict(os.environ, LX_CODEX_EXE=str(fixture), LX_COPILOT_EXE=str(fixture), FF_CLAUDE_EXE=str(fixture), CLAUDE_CONFIG_DIR=str(case / "claude")):
        create_test = "--create" in sys.argv
        with host(exe, state, case / "one" if create_test else None, knowledge=knowledge, initialize=create_test) as url:
            script = Path(__file__).with_name("check_create.cjs") if "--create" in sys.argv else Path(__file__).with_suffix(".cjs")
            subprocess.run(["node", script, url, exe, case], check=True, timeout=200 if "--create" in sys.argv else 300)


if __name__ == "__main__":
    temp = Path(tempfile.gettempdir()) / "codex-task"
    temp.mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(dir=temp, prefix="lx-topic-ui-") as root:
        verify(Path(sys.argv[1]).resolve(), Path(root).resolve())
