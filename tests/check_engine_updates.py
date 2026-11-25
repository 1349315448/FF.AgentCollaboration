"""Exercise the stable release feed and update button's API with a disposable CLI."""
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
from unittest.mock import patch

from check_workbench import host, http


def verify(exe, case):
    cli = case / "codebuddy.exe"
    compiler = Path(os.environ["WINDIR"]) / "Microsoft.NET/Framework/v4.0.30319/csc.exe"
    subprocess.run([compiler, "/nologo", "/out:" + str(cli), Path(__file__).with_name("WorkbenchEngineFixture.cs")], check=True)
    version = case / "version"
    version.write_text("CodeBuddy Code 0.0.1")
    prefix = case / "npm"
    package = prefix / "node_modules/@qwen-code/qwen-code"
    package.mkdir(parents=True)
    shim = prefix / "qwen.cmd"
    shim.write_text("@exit /b 1\n")
    (package / "package.json").write_text(json.dumps({"name":"@qwen-code/qwen-code","version":"0.14.2"}))
    node = shutil.which("node")
    tools = case / "tools"
    tools.mkdir()
    for binary in ("node.exe", "npm.exe", "winget.exe"):
        shutil.copyfile(cli, tools / binary)
    machine_cli = case / "Program Files/WinGet/Packages/Anthropic.ClaudeCode_Microsoft.Winget.Source_8wekyb3d8bbwe/claude.exe"
    machine_cli.parent.mkdir(parents=True)
    shutil.copyfile(cli, machine_cli)
    route = "/api/engines/codebuddy"
    with patch.dict(os.environ, FF_CODEBUDDY_EXE=str(cli), FF_QWEN_EXE=str(shim), FF_CLAUDE_EXE=str(machine_cli),
                    LOCALAPPDATA=str(case / "local"), ProgramFiles=str(case / "Program Files"), ProgramW6432=str(case / "Program Files"),
                    FF_TEST_ENGINE_VERSION_FILE=str(version), CODEBUDDY_API_KEY="local-update-test",
                    PATH=str(tools) + os.pathsep + os.environ["PATH"]), host(exe, case / "state") as url:
        assert http(url, "/api/engines/unknown/release")[0] == 400
        qwen = http(url, "/api/engines/qwen/release")[1]
        assert qwen["installed"]["version"] == "0.14.2" and qwen["canUpdate"]
        (case / "version.node").write_text("v20.18.1")
        assert http(url, "/api/engines/qwen/update", method="POST")[0] == 400
        assert not (case / "version.npm-call").exists()
        (case / "version.node").write_text("v22.0.0")
        (case / "version.package").write_text(json.dumps({"name":"@qwen-code/qwen-code","version":qwen["latestVersion"]}))
        assert http(url, "/api/engines/qwen/update", method="POST")[0] == 204
        assert (case / "version.npm-call").read_text() == "@qwen-code/qwen-code@" + qwen["latestVersion"]
        claude = http(url, "/api/engines/claude/release")[1]
        (case / "version.next").write_text(claude["latestVersion"])
        response = http(url, "/api/engines/claude/update", method="POST")
        assert response[0] == 204, (claude, response)
        assert (case / "version.winget").read_text().splitlines()[:8] == ["upgrade","--id","Anthropic.ClaudeCode","--exact","--source","winget","--scope","machine"]
        version.write_text("0.0.1")
        topic = http(url, "/api/topics", {"name":"fixture", "project":str(case)})[1]
        agent = http(url, "/api/windows", {"topic":topic["id"],"engine":"codebuddy","name":"fixture","newSession":True})[1]
        assert http(url, route + "/update", method="POST")[0] == 409
        assert http(url, "/api/windows/" + agent["id"] + "/stop", method="POST")[0] == 204
        release = http(url, route + "/release")[1]
        assert release["installed"]["path"] == str(cli)
        assert release["status"] == "available" and release["canUpdate"], release
        latest = release["latestVersion"]
        next_version = case / "version.next"
        next_version.write_text("0.0.1")
        assert http(url, route + "/update", method="POST")[0] == 503
        next_version.write_text(latest)
        version.write_text(latest + "-rc.1")
        assert http(url, route + "/release")[1]["status"] == "available"
        assert http(url, route + "/update", method="POST")[0] == 204
        assert http(url, route + "/release")[1]["status"] == "current"
        assert http(url, route + "/update", method="POST")[0] == 204
        version.write_text("999.0.0")
        assert http(url, route + "/release")[1]["status"] == "ahead"
        assert http(url, route + "/update", method="POST")[0] == 204
        assert (case / "version.calls").read_text().splitlines() == ["update", "update"]
        if "--ui" in sys.argv:
            version.write_text("0.0.1")
            subprocess.run([node, Path(__file__).with_suffix(".cjs"), url, latest], check=True, timeout=100)
            assert version.read_text() == latest
    print("PASS release detection, active window, prerelease, verified update, retry and no downgrade")


if __name__ == "__main__":
    root = Path(tempfile.gettempdir()) / "codex-task"
    root.mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(dir=root, prefix="ff-engine-update-") as case:
        verify(Path(sys.argv[1]).resolve(), Path(case).resolve())
