"""Exercise a native Claude fork using only a disposable profile and a local model stub."""
import json
import hashlib
import sqlite3
from contextlib import closing
import os
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
from threading import Thread
from uuid import uuid4
from unittest.mock import patch

from check_workbench import host, http


class LocalMessages(BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass

    def do_POST(self):
        request = json.loads(self.rfile.read(int(self.headers.get("Content-Length", 0))) or b"{}")
        if self.path.split("?")[0] != "/v1/messages":
            self.send_response(200)
            self.end_headers()
            self.wfile.write(b'{"input_tokens":12}')
            return
        names = {tool["name"] for tool in request.get("tools", [])}
        self.server.tool_names.update(names)
        # 新版 CLI 可在 MCP 连接就绪前发起第一轮请求。模拟原生提供的等待工具调用。
        wait = (self.server.checking_mcp and not self.server.requested_wait and "WaitForMcpServers" in names
                and "mcp__lx_collab__send_to_agent" not in names)
        block = {"type":"tool_use","id":"toolu_wait","name":"WaitForMcpServers","input":{}} if wait else {"type":"text","text":""}
        if wait:
            self.server.requested_wait = True
        reason = "tool_use" if wait else "end_turn"
        msg = {"id":"msg_test","type":"message","role":"assistant","model":request["model"],"content":[],
               "stop_reason":None,"stop_sequence":None,"usage":{"input_tokens":12,"output_tokens":1}}
        self.send_response(200)
        if request.get("stream"):
            self.send_header("Content-Type", "text/event-stream")
            self.end_headers()
            events = [("message_start",dict(message=msg)),("content_block_start",dict(index=0,content_block=block)),
                      ("content_block_stop",dict(index=0)),("message_delta",dict(delta={"stop_reason":reason,"stop_sequence":None},
                      usage={"output_tokens":2})),("message_stop",{})]
            if not wait:
                events.insert(2, ("content_block_delta",dict(index=0,delta={"type":"text_delta","text":"local reply"})))
            for kind, data in events:
                self.wfile.write(("event: "+kind+"\ndata: "+json.dumps(dict(type=kind, **data))+"\n\n").encode())
        else:
            self.send_header("Content-Type", "application/json")
            self.end_headers()
            msg.update(content=[block] if wait else [{"type":"text","text":"local reply"}],stop_reason=reason)
            self.wfile.write(json.dumps(msg).encode())


def verify(exe, root):
    cli = os.environ.get("FF_CLAUDE_EXE") or shutil.which("claude.exe") or shutil.which("claude.cmd")
    assert cli, "Install Claude Code to run this native check"
    settings = root / "profile"
    settings.mkdir()
    project = root / "project"
    project.mkdir()
    server = ThreadingHTTPServer(("127.0.0.1",0),LocalMessages)
    server.tool_names = set()
    server.checking_mcp = server.requested_wait = False
    Thread(target=server.serve_forever,daemon=True).start()
    env = dict(CLAUDE_CONFIG_DIR=str(settings), FF_CLAUDE_EXE=cli, ANTHROPIC_BASE_URL=f"http://127.0.0.1:{server.server_port}",
               ANTHROPIC_API_KEY="local-test-only", ANTHROPIC_AUTH_TOKEN="", CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC="1",
               DISABLE_AUTOUPDATER="1", ENABLE_TOOL_SEARCH="false")
    def run(*args):
        result = subprocess.run([cli, "-p", "--output-format","json","--model","sonnet",*args], cwd=project,
                                capture_output=True, encoding="utf8",timeout=65)
        assert result.returncode == 0, (result.stderr or result.stdout)[-1000:]
        return json.loads(result.stdout)
    try:
        with patch.dict(os.environ,env):
            source = str(uuid4())
            marker = "source-marker-"+uuid4().hex
            assert run("--session-id",source,marker)["session_id"] == source
            transcript, = (settings/"projects").rglob(source+".jsonl")
            original = transcript.read_bytes()
            with host(exe, root/"state", project) as base:
                topic = http(base,"/api/topics",{"name":"调研","project":str(project)})[1]["id"]
                code, window = http(base,"/api/windows/fork",{"name":"分析","engine":"claude","topic":topic,"session":source,"forkSession":True})
                assert code == 200 and window["session"] != source, (code, transcript.parent.name, str(project),http(base,"/api/state")[1]["project"],
                    {row.get("cwd") for row in map(json.loads,original.decode().splitlines())},
                    http(base,"/api/sessions/claude?includeBound=true")[1])
                branch = window["session"]
                assert http(base,"/api/windows/"+window["id"],method="DELETE")[0] == 204
                # 用固定测试授权连接同一份工作台 MCP，检查原生 Claude 交给本地模型的工具清单。
                test_grant = uuid4().hex
                with closing(sqlite3.connect(root/"state"/"workbench.sqlite")) as db:
                    db.execute("UPDATE windows SET closed=0,online=1,grant_hash=? WHERE id=?",
                               (hashlib.sha256(test_grant.encode()).hexdigest().upper(),window["id"]))
                    db.commit()
                config = {"mcpServers":{"lx_collab":{"type":"stdio","command":str(exe),
                          "args":["workbench-mcp",str(root/"state")],"env":{"LX_P0_WINDOW_GRANT":"${LX_P0_WINDOW_GRANT}"}}}}
                server.checking_mcp = True
                with patch.dict(os.environ,LX_P0_WINDOW_GRANT=test_grant):
                    run("--strict-mcp-config","--mcp-config",json.dumps(config),"--allowedTools","mcp__lx_collab__*","--","MCP local test")
                assert "mcp__lx_collab__send_to_agent" in server.tool_names, sorted(server.tool_names)
                print("PASS native Claude exposes workbench MCP send_to_agent to the local model")
            # Use the same native fork flags as the interactive launcher, plus a local-only first turn.
            second = "fork-only-"+uuid4().hex
            existing = list((settings/"projects").rglob(branch+".jsonl"))
            args = ["--resume", branch] if existing else ["--resume",source,"--fork-session","--session-id",branch,"--name","调研-分析"]
            assert run(*args, second)["session_id"] == branch
            record, = (settings/"projects").rglob(branch+".jsonl")
            contents = record.read_text("utf8")
            assert marker in contents and second in contents
            assert transcript.read_bytes() == original
            print("PASS native Claude fork: new ID, inherited source context, branch-only turn, original transcript byte-for-byte preserved")
    finally:
        server.shutdown()
        server.server_close()


if __name__ == "__main__":
    temp = Path(tempfile.gettempdir()) / "codex-task"
    temp.mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(dir=temp, prefix="ff-claude-fork-") as root:
        verify(Path(sys.argv[1]).resolve(),Path(root))
