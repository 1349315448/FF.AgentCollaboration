"""Exercise the Windows AGY engine contract without requiring a Google account."""

import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import time
from unittest.mock import patch
from urllib.parse import urlencode

from check_workbench import host, http


def verify(executable, case):
    project, state = case / "project files", case / "state"
    project.mkdir()
    candidate = case / "local-app/agy/bin/agy.exe"
    candidate.parent.mkdir(parents=True)
    compiler = Path(os.environ["WINDIR"]) / "Microsoft.NET/Framework/v4.0.30319/csc.exe"
    subprocess.run([compiler, "/nologo", "/out:" + str(candidate),
                    Path(__file__).with_name("AntigravityEngineFixture.cs")], check=True)
    original = project / ".agents/mcp_config.json"
    original.parent.mkdir()
    configured = '{"mcpServers":{"original":{"command":"test-original"}}}'
    original.write_text(configured, encoding="utf-8")
    with patch.dict(os.environ, LOCALAPPDATA=str(case / "local-app"), FF_ANTIGRAVITY_EXE="", LX_ANTIGRAVITY_EXE=""), host(executable, state) as base:
        engines = http(base, "/api/engines?detail=true", timeout=30)[1]
        installed = next(item for item in engines if item["engine"] == "antigravity")
        assert installed["installed"] and Path(installed["path"]) == candidate and installed["version"] == "1.5.18", installed
        capabilities = http(base, "/api/engines/capabilities")[1]["antigravity"]
        assert capabilities["sessionActions"] == ["new"] and {mode["id"] for mode in capabilities["modes"]} == {"accept-edits", "plan"}
        assert http(base, "/api/quota/antigravity")[1]["supported"] is False
        query = "?" + urlencode({"project": str(project)})
        account = "/api/engines/antigravity/account" + query
        topic = http(base, "/api/topics", {"name": "AGY test", "project": str(project)})[1]
        create = {"topic": topic["id"], "name": "Antigravity", "engine": "antigravity", "newSession": True,
                  "model": "gemini-3.8-flash", "mode": "plan"}
        flag = project / "signed-out.fixture"
        flag.touch()
        assert http(base, account)[1]["status"] == "signed-out"
        assert http(base, "/api/windows", create)[0] == 401
        flag.unlink()
        assert http(base, account)[1]["status"] == "authenticated"
        models = http(base, "/api/models/antigravity" + query)[1]
        assert [(m["id"], m["name"]) for m in models] == [
            ("gemini-3.8-flash", "Gemini 3.8 Flash"), ("example-model", "example-model")], models
        inventory = http(base, "/api/managed-mcps" + query)[1]
        assert any(row["engine"] == "antigravity" and row["name"] == "original" for row in inventory["native"]), inventory
        assert inventory["compatibility"]["antigravity"] == []

        original.write_text(configured.replace('"original"', '"lx_collab"'), encoding="utf-8")
        status, response = http(base, "/api/windows", create)
        assert status == 400 and "lx_collab" in response["error"], (status, response)
        original.write_text(configured, encoding="utf-8")
        status, window = http(base, "/api/windows", create)
        assert status == 200 and window["online"] and not window.get("session"), (status, window)
        marker = project / "agy-launch.txt"
        for _ in range(100):
            if marker.exists() and marker.stat().st_size:
                break
            time.sleep(.05)
        args = marker.read_text(encoding="utf-8").splitlines()
        assert args[:5] == ["--model", "gemini-3.8-flash", "--mode", "plan", "--add-dir"], args
        overlay = Path(args[5])
        assert len(args) == 6 and overlay.parent.resolve() == (state / "engine-profiles" / window["id"]).resolve(), args
        bridge = json.loads((overlay / ".agents/mcp_config.json").read_text(encoding="utf-8"))["mcpServers"]["lx_collab"]
        assert set(bridge) == {"command", "args"} and "workbench-mcp" in bridge["args"], bridge
        assert original.read_text(encoding="utf-8") == configured
        assert http(base, "/api/windows/" + window["id"] + "/stop", method="POST")[0] == 204
        print("PASS Antigravity install discovery, sign-in, model catalog, native launch and scoped MCP configuration")


if __name__ == "__main__":
    with tempfile.TemporaryDirectory(prefix="ff-antigravity-") as folder:
        verify(sys.argv[1], Path(folder))
