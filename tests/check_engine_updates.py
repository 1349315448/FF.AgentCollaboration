"""Exercise the stable release feed and update button's API with a disposable CLI."""
import json
import base64
import os
from pathlib import Path
import shutil
import socket
import subprocess
import sys
import tempfile
import time
from unittest.mock import patch
from urllib.parse import urlencode, urlsplit

from check_workbench import host, http


def verify_codebuddy_node(exe, root):
    project = root / "project"
    prefix = root / "old-node"
    project.mkdir(parents=True)
    entry = prefix / "node_modules/@tencent-ai/codebuddy-code/bin/codebuddy"
    entry.parent.mkdir(parents=True)
    entry.write_text("""
const fs = require('node:fs');
const [action] = process.argv.slice(2);
fs.appendFileSync('runtimes.jsonl', JSON.stringify([action, process.versions.node, process.execPath])+'\\n');
if (action==='--acp') {
  require('node:readline').createInterface({input:process.stdin}).on('line',line=>{
    const req=JSON.parse(line);
    console.log(JSON.stringify({jsonrpc:'2.0',id:req.id,result:req.method==='initialize'?{protocolVersion:1}:{userInfo:fs.existsSync('signed-out.fixture')?{}:{userId:'fixture'}}}));
  });
} else if(action==='--version') console.log('fixture 1.0');
else setTimeout(()=>{},60000);
""", encoding="utf8")
    shim = prefix / "codebuddy.cmd"
    shim.write_text('@echo off\n"%~dp0node.exe" "%~dp0node_modules/@tencent-ai/codebuddy-code/bin/codebuddy" %*\n')
    source = root / "old.cs"
    source.write_text('using System.Reflection; [assembly: AssemblyFileVersion("18.2.0.0")] class Old { static int Main(){ return 7; } }')
    compiler = Path(os.environ["WINDIR"]) / "Microsoft.NET/Framework/v4.0.30319/csc.exe"
    subprocess.run([compiler,"/nologo","/out:"+str(prefix/"node.exe"),source],check=True)
    env = dict(FF_CODEBUDDY_EXE=str(shim), CODEBUDDY_CONFIG_DIR=str(root/"profile"),
               CODEBUDDY_API_KEY="", CODEBUDDY_AUTH_TOKEN="", PATH=str(prefix)+os.pathsep+os.environ["PATH"])
    with patch.dict(os.environ,env), host(exe,root/"state",project) as base:
        query = "?" + urlencode({"project":str(project)})
        assert http(base,"/api/engines/codebuddy/account"+query)[1]["status"]=="authenticated"
        url=urlsplit(base)
        key=base64.b64encode(os.urandom(16)).decode()
        with socket.create_connection((url.hostname,url.port),timeout=10) as ws:
            ws.sendall((f"GET /api/engines/codebuddy/login{query} HTTP/1.1\r\nHost: {url.netloc}\r\nOrigin: {base}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\n\r\n").encode())
            assert b"101" in ws.recv(4096).split(b"\r\n")[0]
            topic=http(base,"/api/topics",{"name":"Node","project":str(project)})[1]
            status,agent=http(base,"/api/windows",{"name":"Buddy","topic":topic["id"],"engine":"codebuddy","newSession":True})
            assert status==200,agent
            for _ in range(100):
                rows=[json.loads(line) for line in (project/"runtimes.jsonl").read_text().splitlines()]
                if {"--acp","--no-session-persistence","--session-id"} <= {r[0] for r in rows}: break
                time.sleep(.05)
            else: raise AssertionError(rows)
            assert len({r[2] for r in rows})==1 and all(tuple(map(int,r[1].split("."))) >= (18,20,8) for r in rows),rows
            route = "/api/windows/" + agent["id"]
            assert http(base,route+"/stop",method="POST")[0]==204
            (project/"signed-out.fixture").touch()
            assert http(base,"/api/engines/codebuddy/account"+query)[1]["status"]=="signed-out"
            assert http(base,route+"/start",method="POST")[0]==204, "existing CodeBuddy pane must start the native terminal after sign-out"
            assert http(base,route+"/session",{"newSession":True})[0]==401
    print("PASS CodeBuddy status, login ConPTY and Agent all select a compatible Node with 18.2 first on PATH")


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
        exe, folder = Path(sys.argv[1]).resolve(), Path(case).resolve()
        verify_codebuddy_node(exe, folder / "runtime")
        verify(exe, folder)
