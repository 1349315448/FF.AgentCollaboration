"""Run the Unix release through the workbench's native CLI and WebSocket entry points."""
import base64
import json
import os
from pathlib import Path
import socket
import struct
import sys
import tempfile
import time
from urllib.parse import urlencode, urlsplit
from unittest.mock import patch

from check_workbench import host, http


CLI = r"""#!/usr/bin/env python3
import fcntl, json, os, struct, subprocess, sys, termios, tty
args = sys.argv[1:]
if args == ["--version"]:
    print("fixture 1.0")
elif args == ["auth", "status"]:
    print('{"loggedIn":true}')
elif args == ["auth", "login"]:
    sys.exit(7)
else:
    assert os.isatty(0) and os.isatty(1) and os.isatty(2)
    assert os.environ["TERM"] == "xterm-256color"
    conf = json.loads(args[args.index("--mcp-config")+1])["mcpServers"]["lx_collab"]
    query = json.dumps({"jsonrpc":"2.0","id":1,"method":"tools/list"})+"\n"
    result = subprocess.run([conf["command"], *conf["args"]], input=query, text=True, capture_output=True, timeout=10, check=True)
    assert "send_to_agent" in result.stdout, result.stdout
    tty.setraw(0)
    os.write(1, b"PTY ready and MCP authorized\r\n")
    while True:
        key = os.read(0, 1)
        if key == b"s":
            rows, cols, _, _ = struct.unpack("HHHH", fcntl.ioctl(0, termios.TIOCGWINSZ, bytes(8)))
            os.write(1, f"size {cols}x{rows}\r\n".encode())
        elif key == b"x":
            os.write(1, b"PTY exiting\r\n")
            sys.exit(7)
        elif not key:
            break
"""


def send(ws, opcode, payload):
    assert len(payload) < 126
    mask = os.urandom(4)
    ws.sendall(bytes((128 | opcode, 128 | len(payload))) + mask +
               bytes(byte ^ mask[i % 4] for i, byte in enumerate(payload)))


def read_frame(reader):
    header = reader.read(2)
    if not header:
        return 8, b""
    opcode, size = header[0] & 15, header[1] & 127
    if size in (126, 127):
        size = int.from_bytes(reader.read(2 if size == 126 else 8), "big")
    return opcode, reader.read(size)


def expect(reader, text):
    recent = b""
    while text not in recent:
        opcode, data = read_frame(reader)
        assert opcode != 8, (text, recent)
        recent = (recent + data)[-16384:]


def verify(executable, root):
    cli = root / "bin" / "claude"
    cli.parent.mkdir()
    cli.write_text(CLI)
    cli.chmod(0o700)
    project = root / "project with spaces"
    project.mkdir()
    profile = root / "claude-profile"
    (profile / "projects").mkdir(parents=True)
    with patch.dict(os.environ, PATH=str(cli.parent) + os.pathsep + os.environ["PATH"],
                    CLAUDE_CONFIG_DIR=str(profile)), host(executable, root / "data", project) as base:
        query = "?" + urlencode({"project": str(project)})
        installed = http(base, "/api/engines/claude/installed")
        assert installed == (200,{"ready": True}), installed
        assert http(base, "/api/engines/claude/account" + query)[1]["status"] == "authenticated"
        assert http(base, "/api/sessions/claude" + query)[0] == 200
        topic = http(base, "/api/topics", {"name": "Unix PTY", "project": str(project)})[1]
        status, agent = http(base, "/api/windows", {
            "name": "Unix Agent", "topic": topic["id"], "engine": "claude", "newSession": True
        })
        assert status == 200, agent
        route = "/api/windows/" + agent["id"]
        url = urlsplit(base)
        key = base64.b64encode(os.urandom(16)).decode()
        with socket.create_connection((url.hostname, url.port), timeout=15) as ws, ws.makefile("rb") as reader:
            ws.sendall((f"GET {route}/terminal HTTP/1.1\r\nHost: {url.netloc}\r\nOrigin: {base}\r\n"
                        f"Upgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: {key}\r\n"
                        "Sec-WebSocket-Version: 13\r\n\r\n").encode())
            assert b"101" in reader.readline()
            for line in reader:
                if line == b"\r\n":
                    break
            else:
                raise AssertionError("WebSocket closed before completing headers")
            expect(reader, b"PTY ready and MCP authorized")
            send(ws, 1, json.dumps({"type": "resize", "cols": 121, "rows": 41}).encode())
            send(ws, 2, b"s")
            expect(reader, b"size 121x41")
            send(ws, 2, b"x")
            expect(reader, b"PTY exiting")
        for _ in range(100):
            view = next(window for window in http(base,"/api/state")[1]["windows"] if window["agent"]["id"] == agent["id"])
            if not view["agent"]["online"] and view["exitCode"] == 7:
                break
            time.sleep(.05)
        else:
            raise AssertionError(view)
        assert http(base, route + "/start", method="POST")[0] == 204
        assert http(base, route + "/stop", method="POST")[0] == 204
    print("PASS Unix CLI discovery, account, history, MCP over stdio, controlling tty, resize, exit and restart")


if __name__ == "__main__":
    with tempfile.TemporaryDirectory(prefix="ff-unix-pty-") as directory:
        verify(Path(sys.argv[1]).resolve(), Path(directory).resolve())
