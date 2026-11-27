"""Verify the Windows Cursor adapter, launch-scoped plugin and session binding without a model."""

import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import time
from unittest.mock import patch
from urllib.parse import urlencode

from check_workbench import host, http


def verify(executable, case):
    project = case / "project files"
    local = case / "local-app"
    state = case / "state"
    project.mkdir()
    local.mkdir()
    misleading = case / "other-tools"
    misleading.mkdir()
    compiler = Path(os.environ["WINDIR"]) / "Microsoft.NET/Framework/v4.0.30319/csc.exe"
    fixture = misleading / "agent.exe"
    subprocess.run([compiler, "/nologo", "/out:" + str(fixture),
                    Path(__file__).with_name("CursorEngineFixture.cs")], check=True)
    environment = dict(LOCALAPPDATA=str(local), FF_CURSOR_EXE="", LX_CURSOR_EXE="",
                       PATH=str(misleading) + os.pathsep + os.environ["PATH"], CURSOR_API_KEY="", CURSOR_AUTH_TOKEN="",
                       FF_TEST_CURSOR_HOST=str(executable), FF_TEST_CURSOR_ROOT=str(state))
    native = project / ".cursor" / "mcp.json"
    native.parent.mkdir()
    native.write_text('{"mcpServers":{"original":{"command":"test-original"}}}', encoding="utf-8")
    with patch.dict(os.environ, environment), host(executable, state) as base:
        initial = next(item for item in http(base, "/api/engines?detail=true", timeout=25)[1] if item["engine"] == "cursor")
        assert not initial["installed"] or Path(initial["path"]) != fixture, initial
        if not initial["installed"]:
            status, response = http(base, "/api/engines/cursor/install", method="POST")
            assert status == 400 and "官方安装指南" in response["error"], response
        # Prefer the official installation folder regardless of PATH contents or stale shell state.
        candidate = local / "cursor-agent" / "agent.exe"
        candidate.parent.mkdir()
        shutil.copyfile(fixture, candidate)
        details = next(item for item in http(base, "/api/engines?detail=true", timeout=25)[1] if item["engine"] == "cursor")
        assert Path(details["path"]) == candidate and details["version"] == "2026.11.01-fixture", details
        catalog = http(base, "/api/engines/capabilities")[1]["cursor"]
        assert {mode["id"] for mode in catalog["modes"]} == {"ask", "plan"} and not catalog.get("startupApproval"), catalog
        assert http(base, "/api/quota/cursor")[1]["supported"] is False
        query = "?" + urlencode({"project": str(project)})
        topic = http(base, "/api/topics", {"name": "Cursor test", "project": str(project)})[1]
        create = {"topic": topic["id"], "name": "Cursor", "engine": "cursor", "project": str(project), "newSession": True,
                  "mode": "ask", "model": "sonnet"}
        account = "/api/engines/cursor/account" + query
        signed_out = project / "signed-out.fixture"
        signed_out.touch()
        assert http(base, account)[1]["status"] == "signed-out"
        assert http(base, "/api/windows", create)[0] == 401
        signed_out.unlink()
        assert http(base, account)[1]["status"] == "authenticated"
        models = http(base, "/api/models/cursor" + query)[1]
        assert [(model["id"], model["isDefault"]) for model in models] == [("auto", True), ("sonnet", False)], models
        assert http(base, "/api/sessions/cursor" + query)[1] == []
        old = project / "old-version.fixture"
        old.touch()
        status, response = http(base, "/api/windows", create)
        assert status == 503 and "协作插件" in response["error"] and not (project / "created.txt").exists(), response
        old.unlink()

        import_response = http(base, "/api/managed-mcps/import", {"engines": ["cursor"], "mcpServers": {
            "a-remote": {"type": "http", "url": "https://example.invalid/mcp", "headers": {"Authorization": "fixture-remote-header"}},
            "z-local": {"type": "stdio", "command": "example-cli", "env": {"KEY": "fixture-local-env"}}
        }})
        assert import_response[0] == 200, import_response
        inventory = http(base, "/api/managed-mcps" + query)[1]
        assert any(row["engine"] == "cursor" and row["name"] == "original" for row in inventory["native"]), inventory
        status, window = http(base, "/api/windows", create)
        assert status == 200 and window["online"], (status, window)
        route = "/api/windows/" + window["id"]
        marker = project / "cursor-launch.txt"
        failure = project / "cursor-launch-error.txt"
        for attempt in range(2):
            for _ in range(100):
                assert not failure.exists(), failure.read_text(encoding="utf-8")
                if marker.exists() and marker.stat().st_size:
                    break
                time.sleep(.05)
            args = marker.read_text(encoding="utf-8-sig").splitlines()
            assert args[:5] == ["--resume=" + window["session"], "--model", "sonnet", "--mode", "ask"], args
            plugin = Path(args[args.index("--plugin-dir") + 1])
            assert plugin.parent.resolve() == (state / "engine-profiles" / window["id"]).resolve(), plugin
            assert json.loads((plugin / ".cursor-plugin/plugin.json").read_text(encoding="utf-8"))["name"] == "ff-agent-collaboration"
            config = (plugin / "mcp.json").read_text(encoding="utf-8")
            servers = json.loads(config)["mcpServers"]
            assert servers["lx_collab"]["env"] == {"LX_P0_WINDOW_GRANT": "${env:LX_P0_WINDOW_GRANT}"}
            assert servers["a-remote"]["headers"] == {"Authorization": "${env:FF_CURSOR_MCP_0}"}
            assert servers["z-local"]["env"]["KEY"] == "${env:FF_CURSOR_MCP_1}"
            assert not any(secret in config for secret in ("fixture-remote-header", "fixture-local-env"))
            assert http(base, route + "/stop", method="POST")[0] == 204
            marker.unlink()
            if attempt == 0:
                assert http(base, route + "/start", method="POST")[0] == 204
        assert (project / "created.txt").read_text(encoding="utf-8").splitlines() == ["created"]
        assert json.loads(native.read_text(encoding="utf-8")) == {"mcpServers": {"original": {"command": "test-original"}}}
        assert not (case / "cli-user/.cursor/mcp.json").exists()
        print("PASS Windows Cursor login gate, model catalog, ConPTY resume, MCP authorization and secret isolation")


if __name__ == "__main__":
    temporary = Path(tempfile.gettempdir()) / "codex-task"
    temporary.mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="ff-cursor-", dir=temporary) as folder:
        verify(Path(sys.argv[1]).resolve(), Path(folder))
