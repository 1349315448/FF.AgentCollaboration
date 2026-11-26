"""Verify direct inbox delivery without a terminal keyboard or an xterm viewer."""
from concurrent.futures import ThreadPoolExecutor
from contextlib import closing
import hashlib
import json
import os
from pathlib import Path
import queue
import sqlite3
import subprocess
import sys
import tempfile
import threading
import time
from unittest.mock import patch

from check_workbench import host, http, mcp, new_id


def verify(exe, case):
    root, project = case / "state", case / "project"
    project.mkdir()
    with host(exe, root, project) as url, closing(sqlite3.connect(root / "workbench.sqlite")) as db:
        topic = http(url, "/api/topics", {"name": "收件", "project": str(project)})[1]["id"]
        sender, receiver = new_id(), new_id()
        for ident in (sender, receiver):
            db.execute("INSERT INTO windows(id,name,engine,project,grant_hash,online,topic_id,auto_receive,receive_mode) VALUES(?,?,?,?,?,1,?,1,'immediate')",
                       (ident, ident, "codebuddy", str(project), hashlib.sha256(ident.encode()).hexdigest().upper(), topic))
        db.commit()
        def receive(grant, **args):
            return mcp(exe, root, grant, "receive_message", **args)

        empty = receive(receiver)
        assert not empty.get("isError"), empty
        assert json.loads(empty["content"][0]["text"]) is None
        sent = mcp(exe, root, sender, "send_to_agent", target=receiver, message="第一段\n第二段")
        mid = json.loads(sent["content"][0]["text"])["id"]
        # Simulate the host releasing the envelope; there is deliberately no PTY.
        db.execute("UPDATE inbox SET native_offer='immediate' WHERE id=?", (mid,))
        db.commit()
        assert json.loads(receive(sender, messageId=mid)["content"][0]["text"]) is None
        db.execute("UPDATE windows SET auto_receive=0 WHERE id=?", (receiver,))
        db.commit()
        assert json.loads(receive(receiver)["content"][0]["text"]) is None
        db.execute("UPDATE windows SET auto_receive=1 WHERE id=?", (receiver,))
        db.commit()
        with ThreadPoolExecutor(2) as pool:
            replies = list(pool.map(lambda _: json.loads(receive(receiver)["content"][0]["text"]), range(2)))
        assert replies.count(None) == 1, replies
        body = next(reply for reply in replies if reply)
        assert body["id"] == mid and "第一段" in body["message"] and "第二段" in body["message"], body
        assert http(url, "/api/messages/" + mid)[1]["state"] == "delivered"
        print("PASS native MCP receipt: grant isolation, disabled automatic receipt, concurrent single delivery without PTY")

        # Simulate a process exiting after claiming the body and before flushing its response.
        db.execute("UPDATE inbox SET state='writing', native_offer='claimed', native_offered_at=? WHERE id=?", (int(time.time()) - 31, mid))
        db.commit()
        deadline = time.monotonic() + 10
        while time.monotonic() < deadline and http(url, "/api/messages/" + mid)[1]["state"] == "writing":
            time.sleep(0.2)
        assert http(url, "/api/messages/" + mid)[1]["state"] == "delivery_uncertain"
        assert json.loads(receive(receiver, messageId=mid)["content"][0]["text"]) is None
        print("PASS interrupted native receipt is reconciled without delivering its body again")

def verify_channel(exe, case):
    project, root = case / "channel-project", case / "channel-state"
    project.mkdir()
    (project / "channel.fixture").touch()
    (project / "native-prompt.fixture").write_text("codebuddy")
    fixture = case / "channel-cli.exe"
    compiler = Path(os.environ["WINDIR"]) / "Microsoft.NET/Framework/v4.0.30319/csc.exe"
    subprocess.run([compiler, "/nologo", "/main:TopicTerminalFixture", "/out:" + str(fixture),
                    Path(__file__).with_name("TopicTerminalFixture.cs"), Path(__file__).with_name("WorkbenchEngineFixture.cs")], check=True)
    with patch.dict(os.environ, FF_CODEBUDDY_EXE=str(fixture), CODEBUDDY_CONFIG_DIR=str(case / "profile")), host(exe,root,project) as url, closing(sqlite3.connect(root / "workbench.sqlite")) as db:
        topic = http(url,"/api/topics",{"name":"频道", "project":str(project)})[1]["id"]
        status, agent = http(url,"/api/windows",{"name":"Buddy","engine":"codebuddy","topic":topic,"newSession":True})
        assert status == 200, agent
        route = "/api/windows/" + agent["id"]
        assert http(url,route+"/input",{"text":"我正在写的草稿"})[0] == 204
        sender, grant = new_id(), new_id()
        digest = lambda key: hashlib.sha256(key.encode()).hexdigest().upper()
        db.execute("INSERT INTO windows(id,name,engine,project,grant_hash,online,topic_id) VALUES(?,?,?,?,?,1,?)",
                   (sender,sender,"codex",str(project),digest(sender),topic))
        db.execute("UPDATE windows SET grant_hash=? WHERE id=?", (digest(grant),agent["id"]))
        db.commit()
        with subprocess.Popen([exe,"workbench-mcp",root],stdin=subprocess.PIPE,stdout=subprocess.PIPE,
                              text=True,encoding="utf8",env=dict(os.environ,LX_P0_WINDOW_GRANT=grant)) as child:
            packets = queue.Queue()
            def reader():
                for line in child.stdout:
                    packets.put(json.loads(line))
            threading.Thread(target=reader, daemon=True).start()
            def write(method, params, seq=None):
                payload = {"jsonrpc":"2.0","method":method,"params":params}
                if seq is not None:
                    payload["id"] = seq
                child.stdin.write(json.dumps(payload)+"\n")
                child.stdin.flush()
            try:
                write("initialize",{"protocolVersion":"2024-11-05"},1)
                assert "claude/channel" in packets.get(timeout=8)["result"]["capabilities"]["experimental"]
                write("notifications/initialized",{})
                for seq,mode in enumerate(("immediate","queued"),2):
                    assert http(url,route+"/auto-receive",{"mode":mode,"enabled":True},"PUT")[0] == 204
                    result = mcp(exe,root,sender,"send_to_agent",target=agent["id"],message="只投递到会话 "+mode,supersedePrevious=False)
                    mid = json.loads(result["content"][0]["text"])["id"]
                    note = packets.get(timeout=12)
                    assert note["method"] == "notifications/claude/channel" and note["params"]["meta"]["message_id"] == mid
                    token = note["params"]["content"].split("channelReceipt=")[1]
                    write("tools/call",{"name":"receive_message","arguments":{"messageId":mid,"channelReceipt":token}},seq)
                    result = packets.get(timeout=8)["result"]
                    assert mode in json.loads(result["content"][0]["text"])["message"]
                    assert (project/"draft-observed.txt").read_text(encoding="utf-8-sig") == "我正在写的草稿"
                    assert not (project/"enters.txt").exists()
                assert db.execute("SELECT channel_confirmed FROM windows WHERE id=?",(agent["id"],)).fetchone() == (1,)
            finally:
                child.stdin.close()
                child.wait(timeout=10)
        print("PASS real ConPTY and MCP channel: immediate and queued envelopes received with the original Chinese draft intact and zero Enter")


if __name__ == "__main__":
    with tempfile.TemporaryDirectory(prefix="ff-native-inbox-") as case:
        verify(Path(sys.argv[1]).resolve(), Path(case))
        verify_channel(Path(sys.argv[1]).resolve(), Path(case))
