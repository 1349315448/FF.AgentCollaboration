"""Exercise the opt-in HTTP workbench without touching a running instance."""
from contextlib import closing
from pathlib import Path
import os
import socket
import sqlite3
import subprocess
import sys
import tempfile
from unittest.mock import patch

from check_workbench import host, http, new_id


def verify(exe, case):
    root = case / "state"
    with host(exe, root, case) as local:
        assert http(local, "/api/lan-access")[1]["address"] is None
        assert http(local, "/api/lan-access/pairing", {}, "POST")[0] == 409
        topic = http(local, "/api/topics", {"name": "LAN 验证"})[1]["id"]
    agent = new_id()
    with closing(sqlite3.connect(root / "workbench.sqlite")) as db:
        db.execute("INSERT INTO windows(id,name,engine,project,topic_id) VALUES(?,'LAN Reviewer','codex',?,?)",
                   (agent, str(case), topic))
        db.commit()
    with socket.socket() as available:
        available.bind(("", 0))
        port = available.getsockname()[1]
    fixture = case / "terminal.exe"
    compiler = Path(os.environ["WINDIR"]) / "Microsoft.NET/Framework/v4.0.30319/csc.exe"
    subprocess.run([compiler, "/nologo", "/main:TopicTerminalFixture", "/out:" + str(fixture),
                    Path(__file__).with_name("TopicTerminalFixture.cs"), Path(__file__).with_name("WorkbenchEngineFixture.cs")], check=True)
    with patch.dict(os.environ,LX_CODEX_EXE=str(fixture),OPENAI_API_KEY="test"), host(exe, root, case, lan_http_port=port) as local:
        address = http(local, "/api/lan-access")[1]["address"]
        assert address.endswith(":" + str(port))
        subprocess.run(["node", Path(__file__).with_name("check_lan_workbench.cjs"), local, address, topic, agent],
                       check=True, timeout=100)


if __name__ == "__main__":
    temp = Path(tempfile.gettempdir()) / "codex-task"
    temp.mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(dir=temp, prefix="ff-lan-ui-") as folder:
        verify(sys.argv[1], Path(folder))
