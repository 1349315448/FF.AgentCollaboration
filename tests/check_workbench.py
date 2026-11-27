"""Verify the terminal inbox, file boundary and restart state without invoking models."""
from contextlib import closing, contextmanager
from concurrent.futures import ThreadPoolExecutor
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
from urllib.error import HTTPError
from urllib.parse import urlencode, urlsplit
from urllib.request import Request, urlopen
from uuid import uuid4


def new_id():
    return uuid4().hex


@contextmanager
def host(executable, root, project=None, handoffs=None, knowledge=None, fixed=True, graceful=False, initialize=True, use_defaults=False, lan_http_port=None, federation=False):
    command = [executable, "workbench", "--port", "0"] if use_defaults else [
        executable, "workbench", root, "--port", "0", "--handoffs", handoffs or Path(root) / "handoffs"]
    if fixed:
        command.append("--fixed-data-dir")
    if lan_http_port:
        command += ["--lan-http-port", str(lan_http_port)]
    elif not federation:
        command.append("--loopback-only")
    if knowledge is not None:
        command += ["--knowledge", knowledge]
    if project:
        command += ["--project", project]
    # Keep user-level skills and engine configuration inside this fixture.
    profile = Path(root).parent / "cli-user"
    env = dict(os.environ, USERPROFILE=str(profile))
    for key, folder in (("CODEX_HOME", ".codex"), ("CLAUDE_CONFIG_DIR", ".claude")):
        if not env.get(key) or not Path(env[key]).resolve().is_relative_to(profile.parent.resolve()):
            env[key] = str(profile / folder)
    launch = {"env": env}
    if graceful:
        startup = subprocess.STARTUPINFO()
        startup.dwFlags |= subprocess.STARTF_USESHOWWINDOW
        startup.wShowWindow = subprocess.SW_HIDE
        launch.update(creationflags=subprocess.CREATE_NEW_CONSOLE, startupinfo=startup)
    with subprocess.Popen(command, **launch, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, encoding="utf-8") as process:
        lines = queue.Queue()

        def read_output():
            for line in process.stdout:
                lines.put(line.rstrip())
            lines.put(None)

        threading.Thread(target=read_output, daemon=True).start()
        try:
            line = lines.get(timeout=15)
            assert line and line.startswith("LX_WORKBENCH "), line
            url = line.removeprefix("LX_WORKBENCH ")
            if initialize:
                _, setup = http(url, "/api/initialize")
                if not setup["initialized"]:
                    assert http(url, "/api/initialize", dict(setup, nodeName="Test node"), "POST")[0] == 204
            yield url
        finally:
            try:
                if graceful and process.poll() is None:
                    # Only attach the sender to this fixture's private console.
                    signal = ("import ctypes,sys,time; k=ctypes.windll.kernel32; k.FreeConsole(); "
                              "assert k.AttachConsole(int(sys.argv[1])); cb=ctypes.WINFUNCTYPE(ctypes.c_int,ctypes.c_uint)(lambda _:1); assert k.SetConsoleCtrlHandler(cb,1); "
                              "assert k.GenerateConsoleCtrlEvent(1,0); time.sleep(.3)")
                    subprocess.run([sys.executable,"-c",signal,str(process.pid)], check=True, timeout=5)
                    assert process.wait(timeout=15) == 0
            finally:
                if process.poll() is None:
                    process.terminate()
                    process.wait(timeout=10)


def http(base, route, body=None, method=None, *, timeout=10, **headers):
    if body is not None:
        body = json.dumps(body).encode("utf-8")
        headers["Content-Type"] = "application/json"
    try:
        with urlopen(Request(base + route, data=body, headers=headers, method=method), timeout=timeout) as response:
            return response.status, json.loads(response.read() or b"null")
    except HTTPError as error:
        detail = error.read()
        return error.code, json.loads(detail) if detail else None


def mcp(executable, root, grant, name, *, notify_port=None, **arguments):
    request = {"jsonrpc": "2.0", "id": 1, "method": "tools/call", "params": {"name": name, "arguments": arguments}}
    env = dict(os.environ, LX_P0_WINDOW_GRANT=grant)
    command = [executable, "workbench-mcp", root]
    if notify_port is not None:
        command += ["--notify-port", str(notify_port)]
    call = subprocess.run(command, input=json.dumps(request) + "\n",
                          capture_output=True, text=True, encoding="utf-8", env=env, timeout=15, check=True)
    return json.loads(call.stdout)["result"]


def verify(executable, directory):
    project = directory / "Project"
    project.mkdir()
    other = directory / "Other"
    other.mkdir()
    root = directory / "state"
    root.mkdir()
    db_path = root / "workbench.sqlite"
    legacy_window = new_id()
    legacy_session = str(uuid4())
    sender, recipient, outsider = (new_id() for _ in range(3))
    old_id = new_id()
    old_doc = root / "handoffs" / (old_id + ".md")
    old_doc.parent.mkdir()
    old_doc.write_text("旧版交接", encoding="utf8")
    with closing(sqlite3.connect(db_path)) as db:
        db.execute("CREATE TABLE windows(id TEXT PRIMARY KEY,name TEXT,engine TEXT,project TEXT,model TEXT,session TEXT NOT NULL,grant_hash TEXT)")
        db.execute("INSERT INTO windows VALUES(?,?,?,?,?,?,?)", (legacy_window, "Legacy", "codex", str(project), "legacy-model", legacy_session, None))
        db.execute("CREATE TABLE tasks(id TEXT)")
        db.execute("""CREATE TABLE inbox(id TEXT PRIMARY KEY,source TEXT NOT NULL,target TEXT NOT NULL,sender TEXT NOT NULL,
                      text TEXT NOT NULL,files TEXT NOT NULL,document INTEGER NOT NULL,return_hint TEXT,state TEXT NOT NULL)""")
        db.execute("INSERT INTO inbox VALUES(?,?,?,'Sender','旧摘要','[]',1,NULL,'delivered')", (old_id, sender, legacy_window))
        db.execute("INSERT INTO tasks VALUES('prior task')")
        db.commit()
    attachment = project / "interface notes.md"
    attachment.write_text("fixture", encoding="utf8")
    (project / ".git").write_text("gitdir fixture", encoding="utf8")
    (project / "obj").mkdir()
    (project / "obj" / "exclude.txt").touch()
    (other / "private.txt").touch()

    with host(executable, root, project, knowledge=root) as base:
        state = http(base, "/api/state")[1]
        assert state["project"] == str(project)
        assert state["projects"] == [str(project)]
        assert state["windows"][0]["activity"] == "offline" and state["topics"][0]["activity"] == "idle"
        default, = state["topics"]
        assert default["name"] == "默认话题" and state["windows"][0]["agent"]["topicId"] == default["id"]
        folder = Path(default["folder"] or "")
        assert folder.parent == root / "development-tasks" and (folder / "PLAN.md").is_file(), default
        with closing(sqlite3.connect(db_path)) as db:
            assert db.execute("SELECT topic_id FROM inbox WHERE id=?", (old_id,)).fetchone() == (default["id"],)
        for name in ("名字/非法", "含@符号"):
            assert http(base, "/api/topics", {"name": name})[0] == 400
        remote = http(base, "/api/topics", {"name": "其他", "project": str(other), "goal": "初始"})[1]
        topic_route = "/api/topics/" + remote["id"]
        assert http(base, topic_route, {"name": "设计", "goal": "交接"}, "PUT")[1]["goal"] == "交接"
        assert http(base, "/api/topics", {"name": "设计"})[0] == 409
        assert http(base, topic_route + "/finish", {}, "POST")[1]["finishedAt"]
        assert http(base, topic_route + "/reopen", {}, "POST")[1]["finishedAt"] is None
        assert http(base, "/api/windows", {"engine": "codex", "name": "未指定"})[0] == 400
        assert len(state["windows"]) == 1 and state["windows"][0]["agent"]["session"] == legacy_session
        legacy = state["windows"][0]["agent"]
        name_route = f"/api/windows/{legacy_window}/name"
        for invalid in ("", "@", "A\nB", "X" * 41):
            assert http(base, name_route, {"name":invalid}, "PUT")[0] == 400
        assert http(base, f"/api/windows/{new_id()}/name", {"name":"未知窗口"}, "PUT")[0] == 404
        assert http(base, name_route, {"name":"@旧窗新名"}, "PUT")[0] == 204
        renamed = http(base, "/api/state")[1]["windows"][0]["agent"]
        assert renamed == dict(legacy, name="旧窗新名") and not renamed["autoReceive"]
        assert http(base, f"/api/windows/{legacy_window}/session", {}, "POST")[0] == 400
        assert http(base, "/api/state")[1]["windows"][0]["agent"]["session"] == legacy_session
        folders = http(base, "/api/folders?" + urlencode({"path": directory}))[1]["folders"]
        assert {item["name"] for item in folders}.issuperset({"Project", "Other"})
        assert http(base, "/api/state", Origin="https://foreign.test")[0] == 403
        assert http(base, "/api/state", Host="foreign.test")[0] == 403
        assert http(base, "/api/state")[1]["projects"] == [str(project), str(other)]
        assert http(base, "/api/project", {"path": str(other)}, "PUT")[0] == 200
        assert http(base, "/api/project", {"path": str(other).swapcase() + os.sep}, "PUT")[0] == 200
        assert http(base, "/api/project", {"path": str(other)}, "PUT")[0] == 200
        assert http(base, "/api/state")[1]["projects"] == [str(project), str(other)]
        assert http(base, "/api/project", {"path": "relative/path"}, "PUT")[0] == 400
        files = http(base, "/api/files?" + urlencode({"project": project}))[1]["files"]
        assert files == ["interface notes.md"], files
        assert http(base, "/api/handoffs", {}, "POST")[0] == 404
        capabilities = http(base, "/api/engines/capabilities")[1]
        assert set(capabilities) == {"codex", "copilot", "claude", "codebuddy", "traecli", "qwen", "zcode", "qoder"}
        for engine, profile in capabilities.items():
            assert {"new","resume","archive","restore"} <= set(profile["sessionActions"])
            assert ("delete" in profile["sessionActions"]) == (engine in {"codex","copilot","claude","traecli"})
            assert ("fork" in profile["sessionActions"]) == (engine in {"codex","copilot","claude","traecli","codebuddy","qoder"})
            assert ("latest" in profile["sessionActions"]) == (engine == "copilot")
            assert profile["nativeArchive"] == (engine in {"codex","traecli"})
            assert profile["models"] == (engine not in {"qwen","zcode"})
            assert profile["efforts"] == (engine in {"codex","copilot","claude","traecli"})
            assert all(m["id"] and m["label"] for m in profile["modes"])
        for engine, guide in (("codebuddy", "套餐与用量"), ("qwen", "/usage"), ("zcode", "Coding Plan")):
            quota = http(base, "/api/quota/" + engine)[1]
            assert quota["status"] == "unsupported" and guide in quota["message"], (engine, quota)
        chord = "/api/settings/mention-shortcut"
        assert http(base, "/api/state")[1]["mentionShortcut"] == "Alt+Shift+A"
        for invalid in ("A", "Alt", "Enter", "Escape", "Tab", "Ctrl+W", "Alt+Shift+F", "Alt+A\\n"):
            assert http(base, chord, {"chord":invalid}, "PUT")[0] == 400, invalid
        assert http(base, chord, {"chord":"Ctrl+Alt+M"}, "PUT")[1]["chord"] == "Ctrl+Alt+M"
        shortcuts = "/api/shortcuts/codex"
        defaults = http(base, shortcuts)[1]
        assert "/status" in {item["command"] for item in defaults}
        copilot = http(base, "/api/shortcuts/copilot")[1]
        assert "/context" in {item["command"] for item in copilot}
        for invalid in ("", "/x\r", "\x1b/x", "/x\n/y", "x\u2028y", "x" * 201):
            assert http(base, shortcuts, {"action": "save", "command": invalid})[0] == 400
        saved = {"command": "/" + "x" * 199, "note": "示例"}
        assert http(base, shortcuts, dict(action="save", **saved))[1] == defaults + [saved]
        assert http(base, "/api/shortcuts/copilot")[1] == copilot

    with closing(sqlite3.connect(db_path)) as db:
        twin = new_id()
        for agent, name, scope in [(sender, "Sender", project), (recipient, "Recipient", other), (outsider, "Outsider", project),
                                   (twin, "Recipient", other)]:
            grant_hash = hashlib.sha256(agent.encode("ascii")).hexdigest().upper()
            db.execute("INSERT INTO windows(id,name,engine,project,grant_hash,online,topic_id) VALUES(?,?,'codex',?,?,1,?)",
                       (agent, name, str(scope), grant_hash, remote["id"] if agent == twin else default["id"]))
        assert db.execute("SELECT id FROM tasks").fetchone() == ("prior task",)
        db.commit()

        def text(result):
            assert not result.get("isError"), result
            return result["content"][0]["text"]

        peers = json.loads(text(mcp(executable, root, sender, "list_windows")))
        assert text(mcp(executable, root, sender, "read_handoff_doc", pathOrId=old_id)) == "旧版交接"
        assert "@Recipient" in [window["name"] for window in peers] and "@Sender" not in [window["name"] for window in peers]
        assert all(set(window) == {"name", "online"} for window in peers), peers
        global_peers = json.loads(text(mcp(executable, root, sender, "list_windows", allTopics=True, detail=True)))
        assert next(w for w in global_peers if w["id"] == twin)["name"] == "@设计/Recipient"
        assert not any(w["name"] == "@设计/Recipient" for w in peers)
        cross = json.loads(text(mcp(executable, root, sender, "send_to_agent", target="@设计/Recipient", message="跨话题")))
        assert db.execute("SELECT target,topic_id FROM inbox WHERE id=?", (cross["id"],)).fetchone() == (twin, remote["id"])
        assert mcp(executable, root, twin, "send_to_agent", target="@Sender", message="不可越界匹配")["isError"]
        details = json.loads(text(mcp(executable, root, sender, "list_windows", detail=True)))
        assert next(w for w in details if w["id"] == recipient) == {
            "name": "@Recipient", "online": True, "engine": "codex", "project": str(other), "id": recipient}
        assert mcp(executable, root, new_id(), "send_to_agent", target=recipient, message="spoof")["isError"]
        assert mcp(executable, root, sender, "send_to_agent", target=recipient, message="outside", files=[str(other / "private.txt")])["isError"]
        long_text = "中文交接说明\n" * 150
        message = json.loads(text(mcp(executable, root, sender, "send_to_agent", target="@Recipient",
                                      message=long_text.strip(), files=[attachment.name], autoReturnHint=True)))
        doc = Path(message["document"])
        assert doc == folder / "handoffs" / (message["id"] + ".md")
        assert doc.read_text(encoding="utf8") == long_text.strip()
        assert text(mcp(executable, root, recipient, "read_handoff_doc", pathOrId=str(doc))) == long_text.strip()
        assert text(mcp(executable, root, sender, "read_handoff_doc", pathOrId=message["id"])) == long_text.strip()
        assert mcp(executable, root, outsider, "read_handoff_doc", pathOrId=message["id"])["isError"]
        assert mcp(executable, root, recipient, "read_handoff_doc", pathOrId=str(other / doc.name))["isError"]
        short = json.loads(text(mcp(executable, root, sender, "send_to_agent", target=recipient, message="short", supersedePrevious=False)))
        assert short["document"] is None
        prefix = message["id"][:8]
        for grant in (sender, recipient):
            assert text(mcp(executable, root, grant, "read_handoff_doc", pathOrId=prefix.upper())) == long_text.strip()
        assert mcp(executable, root, outsider, "read_handoff_doc", pathOrId=prefix)["isError"]
        plain = mcp(executable, root, recipient, "read_handoff_doc", pathOrId=short["id"][:8])
        assert plain["isError"] and "没有交接文档" in plain["content"][0]["text"]
        collision = prefix + new_id()[8:]
        db.execute("UPDATE inbox SET id=?,source=?,target=? WHERE id=?", (collision, outsider, twin, short["id"]))
        db.commit()
        assert text(mcp(executable, root, sender, "read_handoff_doc", pathOrId=prefix)) == long_text.strip()
        db.execute("UPDATE inbox SET source=?,target=? WHERE id=?", (sender, recipient, collision))
        db.commit()
        ambiguous = mcp(executable, root, sender, "read_handoff_doc", pathOrId=prefix)
        assert ambiguous["isError"] and "多条" in ambiguous["content"][0]["text"]
        assert text(mcp(executable, root, sender, "read_handoff_doc", pathOrId=message["id"])) == long_text.strip()
        db.execute("UPDATE inbox SET id=? WHERE id=?", (short["id"], collision))
        db.commit()
        def send(agent=sender, **args):
            return json.loads(text(mcp(executable, root, agent, "send_to_agent", supersedePrevious=False, **args)))

        def denied(agent=sender, **args):
            assert mcp(executable, root, agent, "send_to_agent", **args)["isError"], args

        before = set(doc.parent.iterdir())
        outside = other / "outside.md"
        outside.write_text("private", encoding="utf8")
        for invalid in (str(outside), "../Other/outside.md", "missing.md", ".git"):
            denied(target=recipient, message="摘要", document=invalid)
        denied(target=recipient, message="长" * 501, document=attachment.name)
        external = send(target=recipient, message="核对接口", document=attachment.name, files=[attachment.name],
                        autoReturnHint=True)
        assert Path(external["document"]) == attachment
        assert text(mcp(executable, root, sender, "read_handoff_doc", pathOrId=str(attachment))) == "fixture"
        assert text(mcp(executable, root, recipient, "read_handoff_doc", pathOrId=external["id"])) == "fixture"
        assert mcp(executable, root, outsider, "read_handoff_doc", pathOrId=str(attachment))["isError"]
        denied(recipient, resend=external["id"])
        denied(outsider, resend=external["id"])
        denied(resend=external["id"], message="替换")
        denied(resend=external["id"], document=attachment.name)
        denied(resend=new_id())
        repeat = send(resend=external["id"])
        forward = send(resend=repeat["id"], target="@Outsider")
        legacy_repeat = send(resend=message["id"], target=outsider)
        short_repeat = send(resend=short["id"], target=outsider)
        assert short_repeat["document"] is None and legacy_repeat["document"] == message["document"]
        assert external["document"] == repeat["document"] == forward["document"]
        assert text(mcp(executable, root, outsider, "read_handoff_doc", pathOrId=str(doc))) == long_text.strip()
        attachment.write_text("updated", encoding="utf8")
        assert text(mcp(executable, root, outsider, "read_handoff_doc", pathOrId=forward["id"])) == "updated"
        assert set(doc.parent.iterdir()) == before
        rows = db.execute("SELECT id,target,text,files,return_hint,state FROM inbox WHERE text='核对接口'").fetchall()
        assert {row[0]: row[1] for row in rows} == {
            external["id"]: recipient, repeat["id"]: recipient, forward["id"]: outsider}
        assert len({row[2:] for row in rows}) == 1
        assert json.loads(rows[0][3]) == [str(attachment)] and "document" in rows[0][4] and rows[0][5] == "pending"
        db.execute("UPDATE inbox SET state='writing' WHERE id=?", (message["id"],))
        db.commit()

    handoffs = directory / "Runtime" / "handoffs"
    with host(executable, root, handoffs=handoffs, knowledge=root) as base:
        assert http(base, "/api/state")[1]["mentionShortcut"] == "Ctrl+Alt+M"
        assert http(base, chord, {"chord":None}, "PUT")[1]["chord"] == "Alt+Shift+A"
        assert http(base, shortcuts)[1] == defaults + [saved]
        assert http(base, shortcuts, {"action": "reset"})[1] == defaults
        view = http(base, "/api/state")[1]
        assert len(view["topics"]) == 2
        remote_inbox = next(w["inbox"] for w in view["windows"] if w["agent"]["id"] == twin)
        assert remote_inbox[0]["fromTopic"] == "默认话题"
        assert view["project"] == str(other) and view["projects"] == [str(project), str(other)]
        inbox = next(pane["inbox"] for pane in view["windows"] if pane["agent"]["id"] == recipient)
        assert {entry["id"]: entry["state"] for entry in inbox} == {
            message["id"]: "delivery_uncertain", short["id"]: "pending", repeat["id"]: "pending", external["id"]: "pending"}
        assert inbox[0]["from"] == "Sender" and inbox[0]["files"] == [str(attachment)]
        assert Path(inbox[0]["document"]) == doc
        assert (handoffs / old_doc.name).read_text(encoding="utf8") == old_doc.read_text(encoding="utf8")
        assert mcp(executable, root, recipient, "list_windows")["isError"]
        with closing(sqlite3.connect(db_path)) as db:
            assert db.execute("SELECT count(*) FROM inbox").fetchone() == (9,)
            db.execute("UPDATE windows SET online=1, grant_hash=? WHERE id=?",
                       (hashlib.sha256(sender.encode("ascii")).hexdigest().upper(), sender))
            db.commit()
        assert http(base, "/api/windows/" + twin + "/inbox/" + message["id"] + "/acknowledge", {}, "POST")[0] == 409
        assert http(base, "/api/windows/" + twin + "/inbox/" + short["id"] + "/acknowledge", {}, "POST")[0] == 409
        assert http(base, "/api/windows/" + recipient + "/inbox/" + short["id"] + "/acknowledge", {}, "POST")[0] == 204
        assert http(base, "/api/windows/" + recipient + "/inbox/" + message["id"] + "/acknowledge", {}, "POST")[0] == 204
        assert http(base, "/api/messages/" + message["id"])[1]["state"] == "delivery_uncertain"
        after_ignore = http(base, "/api/state?topic=" + default["id"] + "&recent=30")[1]
        remaining = next(w["inbox"] for w in after_ignore["windows"] if w["agent"]["id"] == recipient)
        assert {m["id"] for m in remaining} == {repeat["id"], external["id"]}
        pending_receipt = next(m for m in after_ignore["timeline"] if m["id"] == short["id"])
        assert pending_receipt["acknowledged"] and pending_receipt["state"] == "pending"
        receipt = next(m for m in after_ignore["timeline"] if m["id"] == message["id"])
        assert receipt["acknowledged"] and receipt["state"] == "delivery_uncertain"
        history = http(base, "/api/state?topic=" + remote["id"])[1]
        assert history["timelineTopic"] == remote["id"] and cross["id"] in [m["id"] for m in history["timeline"]]
        assert len(http(base, "/api/state?topic=" + default["id"])[1]["timeline"]) == 6
        assert mcp(executable, root, new_id(), "finish_topic", summary="无授权")["isError"]
        assert mcp(executable, root, sender, "finish_topic", summary=" ")["isError"]
        finished = json.loads(text(mcp(executable, root, sender, "finish_topic", summary="交付完成")))
        assert finished == {"id": default["id"], "status":"finished", "summary":"交付完成"}
        topics = {t["id"]:t for t in http(base, "/api/state")[1]["topics"]}
        assert topics[default["id"]]["activity"] == "finished" and topics[remote["id"]]["status"] == "active"
        reopened = http(base, "/api/topics/" + default["id"] + "/reopen", {}, "POST")[1]
        assert reopened["finishSummary"] is None and reopened["status"] == "active"
        old_doc.unlink()
        http(base, topic_route + "/finish", {}, "POST")
        resent = json.loads(text(mcp(executable, root, sender, "send_to_agent", resend=cross["id"])))
        assert "目录" in resent["hint"]
        assert text(mcp(executable, root, sender, "read_handoff_doc", pathOrId=str(doc))) == long_text.strip()
        assert text(mcp(executable, root, sender, "read_handoff_doc", pathOrId=legacy_repeat["id"])) == long_text.strip()
        assert set(handoffs.iterdir()) == {handoffs / old_doc.name}
        assert text(mcp(executable, root, sender, "read_handoff_doc", pathOrId=str(old_doc))) == "旧版交接"
        assert text(mcp(executable, root, sender, "read_handoff_doc", pathOrId=external["id"])) == "updated"
        moved = json.loads(text(mcp(executable, root, sender, "send_to_agent", target=recipient, message=long_text.strip())))
        assert Path(moved["document"]).parent == doc.parent

        remote_folder = Path(remote["folder"])
        assert remote_folder != folder and remote_folder.parent == folder.parent
        handoff = send(target=twin, message=long_text.strip())
        managed = Path(handoff["document"])
        assert managed.parent == remote_folder / "handoffs" and managed.read_text(encoding="utf8") == long_text.strip()
        assert send(target=recipient, resend=handoff["id"])["document"] == str(managed)
        own_plan = folder / "PLAN.md"
        supplied = send(target=twin, message="按计划 | 核实", document=str(own_plan))
        assert Path(supplied["document"]) == own_plan
        denied(target=twin, message="其他话题", document=str(remote_folder / "PLAN.md"))
        with closing(sqlite3.connect(db_path)) as db:
            for agent in (twin, outsider):
                db.execute("UPDATE windows SET grant_hash=?,online=1 WHERE id=?",
                           (hashlib.sha256(agent.encode()).hexdigest().upper(), agent))
            db.commit()
        assert text(mcp(executable, root, twin, "read_handoff_doc", pathOrId=supplied["id"])) == own_plan.read_text(encoding="utf8")
        assert mcp(executable, root, outsider, "read_handoff_doc", pathOrId=supplied["id"])["isError"]
        prior_log = (folder / "collab-log.md").read_text(encoding="utf8")
        assert "交付完成" in prior_log and "历史时间未记录" in prior_log and str(managed.name) not in prior_log
        assert http(base, topic_route + "/reopen", {}, "POST")[0] == 200
        assert http(base, topic_route + "/finish", {}, "POST")[0] == 200
        log = (remote_folder / "collab-log.md").read_text(encoding="utf8")
        assert "默认话题/Sender → Recipient" in log and "按计划 \\| 核实" in log
        assert log.count("handoffs/" + managed.name) == 1 and log.index(managed.name) < log.index("按计划")
        assert "pending" in log and time.strftime("%Y-%m-", time.gmtime()) in log
        assert not mcp(executable, root, sender, "finish_topic", summary="再次归档").get("isError")
        updated = (folder / "collab-log.md").read_text(encoding="utf8")
        assert "再次归档" in updated and "交付完成" not in updated and "Sender → 设计/Recipient" in updated
        assert updated.count("按计划 \\| 核实") == 1

    with host(executable, root, handoffs=handoffs, knowledge=root) as base:
        restored = http(base, "/api/state?topic=" + default["id"] + "&recent=30")[1]
        inbox = next(w["inbox"] for w in restored["windows"] if w["agent"]["id"] == recipient)
        assert all(m["id"] not in (message["id"], short["id"]) for m in inbox)
        pending_receipt = next(m for m in restored["timeline"] if m["id"] == short["id"])
        assert pending_receipt["acknowledged"] and pending_receipt["state"] == "pending"
        receipt = next(m for m in restored["timeline"] if m["id"] == message["id"])
        assert receipt["acknowledged"] and receipt["state"] == "delivery_uncertain"


def verify_first_run(executable, directory):
    sandbox = directory / "onboarding"
    sandbox.mkdir()
    initial = sandbox / "default"
    data = sandbox / "chosen-db"
    archive = sandbox / "archive"
    docs = sandbox / "handoffs"
    config = sandbox / "bootstrap.json"
    chosen = dict(data_dir=str(data), archive_root=str(archive), handoffs=str(docs))
    app = sandbox / "portable app"
    location = sandbox / "portable-location.json"
    with patch.dict(os.environ, LOCALAPPDATA=str(sandbox), FF_WORKBENCH_HOME=str(app), FF_LOCATION_CONFIG=str(location)):
        with host(executable, initial, fixed=False, initialize=False, use_defaults=True) as base:
            suggested = http(base, "/api/initialize")[1]
            expected = {key:str(app / "workbench-data" / leaf) for key, leaf in (
                ("data_dir", "database"), ("archive_root", "topics"), ("handoffs", "handoffs"))}
            assert not (app / "workbench-data").exists()
            assert all(suggested[key] == value for key, value in expected.items())
            assert suggested["nodeName"] and not suggested["needsNodeName"]
            assert http(base, "/api/initialize", expected)[0] == 204
            assert http(base, "/api/initialize")[1]["nodeName"] == suggested["nodeName"]
            assert http(base, "/api/settings")[1]["defaults"] == expected
            assert (Path(expected["data_dir"]) / "workbench.sqlite").is_file()
            assert all(Path(path).is_dir() for path in expected.values())
        with host(executable, initial, fixed=False, initialize=False, use_defaults=True) as base:
            assert http(base, "/api/initialize")[1]["initialized"]
    # Pre-setup versions saved implicit paths without a bootstrap file or the custom_paths marker.
    legacy = sandbox / "FF.AgentCollaboration/workbench"
    old_knowledge = sandbox / "legacy-knowledge"
    old_knowledge.mkdir()
    legacy_docs = sandbox / "legacy-documents"
    with host(executable, legacy, handoffs=legacy_docs, knowledge=old_knowledge):
        pass
    with closing(sqlite3.connect(legacy / "workbench.sqlite")) as db:
        db.execute("DELETE FROM settings WHERE key='custom_paths'")
        db.commit()
    with patch.dict(os.environ, LOCALAPPDATA=str(sandbox), FF_WORKBENCH_HOME=str(sandbox / "upgrade"), FF_LOCATION_CONFIG=str(sandbox / "legacy-pointer.json")):
        with host(executable, initial, fixed=False, initialize=False, use_defaults=True) as base:
            settings = http(base, "/api/settings")[1]
            assert Path(settings["data_dir"]) == legacy
            assert Path(settings["archive_root"]) == old_knowledge / "development-tasks"
            assert Path(settings["handoffs"]) == legacy_docs
    fixed = sandbox / "fixed"
    with host(executable, str(fixed) + os.sep, directory, initialize=False) as base:
        setup = http(base, "/api/initialize")[1]
        assert setup["fixed_data_dir"] and not setup["initialized"]
        assert http(base, "/api/initialize", dict(setup, nodeName="Fixed"))[0] == 204
        assert (fixed / "workbench.sqlite").is_file()
    with patch.dict(os.environ, LOCALAPPDATA=str(sandbox), FF_LOCATION_CONFIG=str(config)):
        with host(executable, initial, directory, fixed=False, initialize=False) as base:
            assert http(base, "/api/initialize")[1]["initialized"] is False
            assert http(base, "/api/state")[0] == 409 and not initial.exists()
            assert not config.exists()
            chosen["nodeName"] = "First node"
            existing = sandbox / "existing"
            existing.mkdir()
            old_file = existing / "workbench.sqlite"
            old_file.write_bytes(b"preserve existing file")
            assert http(base, "/api/initialize", dict(chosen, data_dir=str(existing)))[0] == 409
            assert old_file.read_bytes() == b"preserve existing file"
            assert http(base, "/api/initialize", dict(chosen, handoffs="relative"))[0] == 400
            assert not (data / "workbench.sqlite").exists() and not config.exists()
            with ThreadPoolExecutor(2) as pool:
                statuses = list(pool.map(lambda _: http(base, "/api/initialize", chosen)[0], range(2)))
            assert sorted(statuses) == [204, 409], statuses
            assert http(base, "/api/initialize")[1]["initialized"]
            settings = http(base, "/api/settings")[1]
            assert all(settings[key] == chosen[key] for key in ("data_dir","archive_root","handoffs")), settings
            assert not initial.exists() and not list(data.glob(".initialize-*"))
            topic = http(base, "/api/topics", {"name":"首次配置"})[1]
            assert Path(topic["folder"]).parent == archive
        with host(executable, initial, directory, fixed=False, initialize=False) as base:
            assert http(base, "/api/initialize")[1]["initialized"]
            assert [t["id"] for t in http(base, "/api/topics")[1]] == [topic["id"]]
            settings = http(base, "/api/settings")[1]
            assert all(settings[key] == chosen[key] for key in ("data_dir","archive_root","handoffs")), settings


def verify_location(executable, directory):
    profile = directory / "location-profile"
    config = profile / "bootstrap.json"
    knowledge = directory / "old-knowledge"
    cache = knowledge / "agent-collaboration" / "cache"
    independent = profile / "FF.AgentCollaboration" / "workbench"
    target = directory / "relocated"
    project = directory / "Project"
    with patch.dict(os.environ, LOCALAPPDATA=str(profile), FF_LOCATION_CONFIG=str(config)):
        with host(executable, cache, project, knowledge=knowledge):
            (cache / "handoffs" / "history.md").write_text("old", encoding="utf8")
        with host(executable, independent, project, knowledge=knowledge, fixed=False) as base:
            assert (independent / "handoffs" / "history.md").read_text(encoding="utf8") == "old"
            settings = http(base, "/api/settings")[1]
            assert settings["data_dir"] == str(independent) and not settings["pending_restart"]
            assert Path(settings["defaults"]["archive_root"]).parent == Path(settings["defaults"]["data_dir"]).parent
            paths = dict(data_dir=str(target), archive_root=str(target / "tasks"), handoffs=str(target / "docs"))
            foreign = directory / "foreign"
            with host(executable, foreign):
                pass
            assert http(base, "/api/settings", dict(paths, data_dir=str(foreign)), "PUT")[0] == 409
            assert not json.loads(config.read_text())["pending_migration"] and http(base, "/api/settings")[1] == settings
            code, saved = http(base, "/api/settings", paths, "PUT")
            assert code == 200 and saved["pending_restart"] and saved["active_data_dir"] == str(independent), saved
            assert json.loads(config.read_text())["pending_migration"]
            with closing(sqlite3.connect(target / "workbench.sqlite")) as backup:
                assert dict(backup.execute("SELECT key,value FROM settings"))["handoffs"] == paths["handoffs"]
            assert (target / "handoffs" / "history.md").exists()
            topic = http(base, "/api/topics", {"name":"热备之后的新增话题"})[1]
            (target / "workbench.sqlite").unlink()
            assert http(base, "/api/environment/repair", {"item":"data_dir"})[0] == 204
            with closing(sqlite3.connect(target / "workbench.sqlite")) as repaired:
                assert repaired.execute("SELECT id FROM topics WHERE id=?", (topic["id"],)).fetchone()
            topic = http(base, "/api/topics", {"name":"补备后提交的消息调研"})[1]
            locked = subprocess.run([executable,"workbench",independent,"--port","0"], capture_output=True, timeout=12)
            assert locked.returncode != 0 and json.loads(config.read_text())["pending_migration"]
        # host.terminate simulates a crash before ApplicationStopping gets a chance to refresh the backup.
        with host(executable, independent, fixed=False) as base:
            saved = http(base, "/api/settings")[1]
            assert saved["active_data_dir"] == str(target) and not saved["pending_restart"]
            assert any(item["id"] == topic["id"] for item in http(base,"/api/topics")[1])
            assert not json.loads(config.read_text())["pending_migration"]
            assert (independent / "workbench.sqlite").exists()
            code, back = http(base, "/api/settings", dict(paths, data_dir=str(independent)), "PUT")
            assert code == 200 and back["pending_restart"], (code, back)
        with host(executable, independent, fixed=False) as base:
            assert http(base, "/api/settings")[1]["active_data_dir"] == str(independent)
            assert any(item["id"] == topic["id"] for item in http(base, "/api/topics")[1])
        with host(executable, independent) as base:
            assert http(base,"/api/settings")[1]["active_data_dir"] == str(independent)

        stop_copy = directory / "stop-backup"
        with host(executable, independent, fixed=False, graceful=True) as base:
            settings = http(base, "/api/settings")[1]
            assert http(base, "/api/settings", dict(settings, data_dir=str(stop_copy)), "PUT")[0] == 200
            late = http(base, "/api/topics", {"name":"正常退出前提交"})[1]["id"]
        with closing(sqlite3.connect(stop_copy / "workbench.sqlite")) as stopped:
            assert stopped.execute("SELECT id FROM topics WHERE id=?", (late,)).fetchone()
        # Before T17 both former defaults can contain a database. The previous launcher's cache wins;
        # preserve the unrelated FF database and the source cache, and remember the chosen location.
        config.unlink()
        with host(executable, cache, project, knowledge=knowledge) as base:
            latest = http(base, "/api/topics", {"name":"旧启动器的最新话题"})[1]["id"]
        for _ in range(2):
            with host(executable, independent, project, knowledge=knowledge, fixed=False) as base:
                active = Path(http(base, "/api/settings")[1]["active_data_dir"])
                assert active == independent.with_name("workbench-from-cache")
                assert any(t["id"] == latest for t in http(base, "/api/topics")[1])
        config.unlink()
        first_copy = independent.with_name("workbench-from-cache")
        with host(executable, first_copy) as base:
            separate = http(base, "/api/topics", {"name":"已占用候选位置"})[1]["id"]
        for _ in range(2):
            with host(executable, independent, project, knowledge=knowledge, fixed=False) as base:
                assert Path(http(base, "/api/settings")[1]["active_data_dir"]) == first_copy.with_name(first_copy.name + "-2")
                ids = [item["id"] for item in http(base, "/api/topics")[1]]
                assert latest in ids and separate not in ids
        with closing(sqlite3.connect(first_copy / "workbench.sqlite")) as prior:
            assert prior.execute("SELECT id FROM topics WHERE id=?", (separate,)).fetchone()
        with closing(sqlite3.connect(independent / "workbench.sqlite")) as original:
            assert not original.execute("SELECT 1 FROM topics WHERE id=?", (latest,)).fetchone()


def verify_archives_and_migration(executable, directory):
    profile = directory / "profile"
    old = profile / "LX.AgentCollaboration" / "workbench"
    old_docs = directory / "original-documents"
    old_docs.mkdir()
    old_tree = old / "handoffs" / "nested" / "history.md"
    old_tree.parent.mkdir(parents=True)
    old_tree.write_bytes(b"original\r\n")
    old_lock = old / "workbench.lock"
    old_lock.touch()
    knowledge = directory / "knowledge"
    archives = knowledge / "development-tasks"
    link = archives / "existing-task"
    link.mkdir(parents=True)
    plan = link / "PLAN.md"
    plan.write_bytes(b"\xef\xbb\xbfexisting plan\r\n")
    target = knowledge / "agent-collaboration" / "cache"
    project = directory / "Project"
    message, agent = new_id(), new_id()
    document = old_docs / (message + ".md")
    document.write_text("历史交接正文", encoding="utf8")
    source = old / "workbench.sqlite"

    # Keep committed frames in the WAL and leave the original connection open across import.
    with patch.dict(os.environ, LOCALAPPDATA=str(profile)), closing(sqlite3.connect(source)) as legacy:
        assert legacy.execute("PRAGMA journal_mode=WAL").fetchone() == ("wal",)
        legacy.execute("PRAGMA wal_autocheckpoint=0")
        legacy.executescript("""
            CREATE TABLE windows(id TEXT PRIMARY KEY,name TEXT,engine TEXT,project TEXT,model TEXT,session TEXT NOT NULL,grant_hash TEXT);
            CREATE TABLE inbox(id TEXT PRIMARY KEY,source TEXT,target TEXT,sender TEXT,text TEXT,files TEXT,document INTEGER,return_hint TEXT,state TEXT);
            CREATE TABLE settings(key TEXT PRIMARY KEY,value TEXT NOT NULL);
        """)
        legacy.execute("INSERT INTO windows VALUES(?,'旧窗口','codex',?,NULL,?,NULL)", (agent, str(project), str(uuid4())))
        legacy.execute("INSERT INTO inbox VALUES(?,?,?,'旧窗口','历史交接','[]',1,NULL,'delivered')", (message, agent, agent))
        former_peer = new_id()
        legacy.execute("INSERT INTO inbox VALUES(?,?,?,'旧窗口','旧发件流水','[]',0,NULL,'delivered')", (new_id(), agent, former_peer))
        legacy.execute("INSERT INTO settings VALUES('handoffs',?)", (str(old_docs),))
        legacy.commit()
        assert Path(str(source) + "-wal").stat().st_size > 0

        def failed_import(reason):
            attempt = subprocess.run([executable, "workbench", target, "--fixed-data-dir", "--knowledge", knowledge, "--port", "0"],
                                     capture_output=True, text=True, encoding="utf8", timeout=15)
            assert attempt.returncode != 0 and reason in attempt.stderr, attempt.stderr
            assert not (target / "workbench.sqlite").exists()

        with old_lock.open("rb"):
            failed_import("旧工作台")
        copied = target / "handoffs" / "nested" / old_tree.name
        copied.parent.mkdir(parents=True)
        copied.write_bytes(b"independent file")
        failed_import("内容不同")
        assert copied.read_bytes() == b"independent file"
        copied.unlink()
        with host(executable, target, project, knowledge=knowledge) as base:
            default, = http(base, "/api/topics")[1]
            assert Path(default["folder"]).parent == archives
            assert copied.read_bytes() == old_tree.read_bytes() and (target / "handoffs" / document.name).read_bytes() == document.read_bytes()
            with closing(sqlite3.connect(target / "workbench.sqlite")) as db:
                db.execute("UPDATE windows SET grant_hash=?,online=1 WHERE id=?", (hashlib.sha256(agent.encode()).hexdigest().upper(), agent))
                db.commit()
            result = mcp(executable, target, agent, "read_handoff_doc", pathOrId=message)
            assert result["content"][0]["text"] == "历史交接正文", result
            assert http(base, "/api/topics/" + default["id"] + "/finish", {})[0] == 200
            assert former_peer in (Path(default["folder"]) / "collab-log.md").read_text(encoding="utf8")
            assert {"name": link.name, "path": str(link)} in http(base, "/api/topic-folders")[1]
            linked = http(base, "/api/topics", {"name": "接管话题", "folder": str(link)})[1]
            assert Path(linked["folder"]) == link and plan.read_bytes() == b"\xef\xbb\xbfexisting plan\r\n"
            assert (link / "handoffs").is_dir() and not (link / "collab-log.md").exists()
            assert link.name not in {item["name"] for item in http(base, "/api/topic-folders")[1]}
            assert http(base, "/api/topics", {"name": "重复关联", "folder": str(link)})[0] == 409
            assert http(base, "/api/topics", {"name": "越界", "folder": str(project)})[0] == 400
            initial = Path(default["folder"]).name
            date = initial[:10]
            assert initial == date + "-001-默认话题" and time.strptime(date, "%Y-%m-%d")
            (archives / (date + "-旧任务")).mkdir()
            (archives / "2000-01-02-090-另一天").mkdir()
            (archives / (date + "-007-人工记录")).mkdir()
            first = http(base, "/api/topics", {"name": "接口 + v2", "goal": "验收目标"})[1]
            second = http(base, "/api/topics", {"name": "接口_v2"})[1]
            plain = Path(first["folder"])
            assert plain.name == date + "-008-接口-v2"
            assert Path(second["folder"]).name == date + "-009-接口-v2"
            # Two workbenches may share archives while using independent SQLite databases.
            with host(executable, directory / "parallel-state", project, knowledge=knowledge) as peer:
                with ThreadPoolExecutor(max_workers=2) as pool:
                    barrier = threading.Barrier(2)
                    def create_topic(url, name):
                        barrier.wait(timeout=10)
                        status, created = http(url, "/api/topics", {"name": name})
                        assert status == 200, (status, created)
                        return Path(created["folder"]).name.split("-")[3]
                    numbers = list(pool.map(create_topic, (base, peer), ("并行甲", "并行乙")))
                assert sorted(numbers) == ["010", "011"], numbers
            skeleton = (plain / "PLAN.md").read_text(encoding="utf8")
            assert all(part in skeleton for part in ("接口 + v2", "验收目标", str(project)))
            assert http(base, "/api/topics/" + linked["id"] + "/finish", {})[0] == 200
            assert (link / "collab-log.md").is_file() and plan.read_bytes() == b"\xef\xbb\xbfexisting plan\r\n"
        assert legacy.execute("SELECT text,state FROM inbox WHERE id=?", (message,)).fetchone() == ("历史交接", "delivered")
        assert not legacy.execute("SELECT 1 FROM sqlite_master WHERE name='topics'").fetchall()
        legacy.execute("UPDATE inbox SET text='旧库后续变动'")
        legacy.commit()
        # Emulate a P3 finished topic with no archive binding on its first P4 startup.
        with closing(sqlite3.connect(target / "workbench.sqlite")) as db:
            db.execute("UPDATE topics SET folder=NULL,created_at='2001-01-02 12:00:00' WHERE id=?", (linked["id"],))
            db.commit()
        with host(executable, target, project, knowledge=knowledge) as base:
            restored = next(topic for topic in http(base, "/api/topics")[1] if topic["id"] == linked["id"])
            assert restored["status"] == "finished" and (Path(restored["folder"]) / "collab-log.md").is_file()
            assert Path(restored["folder"]).name == "2001-01-02-001-接管话题"
            with closing(sqlite3.connect(target / "workbench.sqlite")) as db:
                assert db.execute("SELECT text FROM inbox WHERE id=?", (message,)).fetchone() == ("历史交接",)
    missing = directory / "missing-knowledge"
    fallback = directory / "fallback"
    with host(executable, fallback, project, knowledge=missing) as base:
        topic = http(base, "/api/topics", {"name": "本地归档"})[1]
        assert Path(topic["folder"]).parent == fallback / "topics" and not missing.exists()


def verify_projects(executable, directory):
    root = directory / "tab-state"
    paths = [directory / name for name in ("Default", "Opened", "TopicProject", "WindowProject", "ClosedWindow")]
    for path in paths:
        path.mkdir()
    default, opened, topic_project, window_project, closed = paths
    missing = directory / "MissingProject"
    with host(executable, root) as base:
        fresh = http(base, "/api/state")[1]
        assert fresh["projects"] == [] and fresh["project"] is None, fresh
        assert http(base, "/api/topics", {"name": "隐式项目"})[0] == 400
    with host(executable, root, default) as base:
        assert http(base, "/api/project", {"path": str(opened)}, "PUT")[0] == 200
        topic = http(base, "/api/topics", {"name": "归属项目", "project": str(topic_project)})[1]
        with closing(sqlite3.connect(root / "workbench.sqlite")) as db:
            saved = json.dumps([str(default), str(opened), str(default).swapcase() + os.sep, str(missing)])
            db.execute("UPDATE settings SET value=? WHERE key='projects'", (saved,))
            for i, (path, is_closed) in enumerate(((window_project, 0), (window_project, 0), (missing, 0), (closed, 1))):
                db.execute("INSERT INTO windows(id,name,engine,project,topic_id,closed) VALUES(?,?,'codex',?,?,?)",
                           (new_id(), "窗口" + str(i), str(path), topic["id"], is_closed))
            db.commit()
        state = http(base, "/api/state")[1]
        assert state["projects"] == list(map(str, (default, opened, topic_project, window_project))), state["projects"]
        assert len(state["windows"]) == 3 and state["project"] == str(opened)
    with host(executable, root) as base:
        state = http(base, "/api/state")[1]
        assert state["project"] == str(opened) and state["projects"][:2] == [str(default), str(opened)]
        assert http(base, "/api/project", {"path": str(missing)}, "PUT")[0] == 404
        assert http(base, "/api/state")[1]["project"] == str(opened)
        for path in (topic_project, opened, default, window_project):
            assert http(base, "/api/project?" + urlencode({"path": str(path).swapcase() + os.sep}), method="DELETE")[0] == 204
        empty = http(base, "/api/state")[1]
        assert empty["project"] is None and empty["projects"] == []
        assert topic["id"] in [t["id"] for t in empty["topics"]] and len(empty["windows"]) == 3
    with host(executable, root) as base:
        assert http(base, "/api/state")[1]["projects"] == []
        # Removing one entry preserves other saved folders that are temporarily unavailable.
        missing.mkdir()
        assert http(base, "/api/state")[1]["projects"] == [str(missing)]
        assert http(base, "/api/project?" + urlencode({"path": missing}), method="DELETE")[0] == 204
        assert http(base, "/api/project", {"path": str(topic_project)}, "PUT")[0] == 200
        restored = http(base, "/api/state")[1]
        assert restored["project"] == str(topic_project) and restored["projects"] == [str(topic_project)]
        assert Path(topic["folder"], "PLAN.md").exists() and topic_project.is_dir()


def verify_install(executable, directory, fixture):
    local = directory / "install-profile"
    apps = local / "Microsoft" / "WindowsApps"
    apps.mkdir(parents=True)
    # Only the temporary profile and native test binaries are visible to this host.
    with patch.dict(os.environ, LOCALAPPDATA=str(local), USERPROFILE=str(local), PATH=str(apps), LX_CODEX_EXE="", LX_COPILOT_EXE="", LX_CLAUDE_EXE="", FF_CODEX_EXE="", FF_COPILOT_EXE="", FF_CLAUDE_EXE=""):
        with host(executable, directory / "install-state", directory) as base:
            engines = http(base, "/api/engines")[1]
            assert all(not engines[key] for key in ("codex", "copilot", "claude"))
            route = "/api/engines/codex/install"
            assert http(base, route, method="POST")[0] == 503  # missing WinGet
            (apps / "winget.exe").write_bytes(fixture.read_bytes())
            assert http(base, "/api/engines/other/install", method="POST")[0] == 400
            assert http(base, route, method="POST", Origin="https://example.org")[0] == 403
            (local / "deny.fixture").touch()
            assert http(base, route, method="POST")[0] == 503
            assert http(base, "/api/engines")[1]["codex"] is False
            (local / "deny.fixture").unlink()
            with ThreadPoolExecutor(max_workers=2) as pool:
                assert list(pool.map(lambda _: http(base, route, method="POST")[0], range(2))) == [204, 204]
            assert http(base, "/api/engines/copilot/install", method="POST")[0] == 204
            engines = http(base, "/api/engines")[1]
            assert engines["codex"] and engines["copilot"] and not engines["claude"]
            commands = (local / "installs.txt").read_text().splitlines()
            expected = " --exact --source winget --silent --disable-interactivity --accept-package-agreements --accept-source-agreements"
            assert commands == ["install --id OpenAI.Codex" + expected + " --scope user"] * 2 + [
                "install --id GitHub.Copilot" + expected], commands
            (local / "deny.fixture").touch()
            (apps / "npm.exe").write_bytes(fixture.read_bytes())
            assert http(base, "/api/engines/claude/install", method="POST")[0] == 204
            assert (local / "installs.txt").read_text().splitlines()[-2:] == [
                "install --id Anthropic.ClaudeCode" + expected, "install -g @anthropic-ai/claude-code"]


def verify_ff(executable, directory, fixture):
    project = directory / "FF project"
    project.mkdir()
    state = directory / "ff-state"
    home = directory / "claude-profile"
    shim = directory / "cli wrapper.cmd"
    shim.write_text('@echo off\n"%~dp0engine.exe" %*\n')
    import re
    history = home / "projects" / re.sub("[^A-Za-z0-9]", "-", str(project))
    history.mkdir(parents=True)
    history_id = str(uuid4())
    history_file = history / (history_id + ".jsonl")
    history_file.write_text(json.dumps({"sessionId":history_id, "cwd":str(project), "customTitle":"历史会话"}) + "\n", encoding="utf8")
    collision_id = str(uuid4())
    foreign = history / (collision_id + ".jsonl")
    foreign.write_text(json.dumps({"sessionId":collision_id, "cwd":str(directory)}), encoding="utf8")
    with patch.dict(os.environ, FF_CLAUDE_EXE=str(shim), CLAUDE_CONFIG_DIR=str(home)):
        with host(executable, state, project) as base:
            details = {item["engine"]: item for item in http(base, "/api/engines?detail=true")[1]}
            assert details["claude"]["version"] == "fixture 2.1"
            assert len(http(base, "/api/models/claude")[1]) == 6
            quota = http(base, "/api/quota/claude")[1]
            assert not quota["supported"] and quota["status"] == "unsupported" and "/usage" in quota["message"]
            assert len(http(base, "/api/shortcuts/claude")[1]) == 6
            assert [s["id"] for s in http(base, "/api/sessions/claude")[1]] == [history_id]
            defaults = http(base, "/api/settings")[1]["defaults"]
            paths = dict(archive_root=str(directory / "FF archives"), handoffs=str(directory / "FF documents"))
            assert http(base, "/api/settings", paths, "PUT")[1]["handoffs"] == paths["handoffs"]
            assert http(base, "/api/settings", dict(paths, handoffs="relative"), "PUT")[0] == 400
            Path(paths["handoffs"]).rmdir()
            assert http(base, "/api/environment/repair", {"item":"handoffs"})[0] == 204
            assert Path(paths["handoffs"]).is_dir()
            assert http(base, "/api/environment/repair", {"item":"unknown"})[0] == 400
            topic = http(base, "/api/topics", {"name":"接口 & UI","project":str(project)})[1]
            assert Path(topic["folder"]).parent == Path(paths["archive_root"])
            sender, peer = new_id(), new_id()
            with closing(sqlite3.connect(state / "workbench.sqlite")) as db:
                for actor in (sender, peer):
                    db.execute("INSERT INTO windows(id,topic_id,name,engine,project,online,grant_hash) VALUES(?,?,?,'claude',?,1,?)",
                               (actor,topic["id"],actor,str(project),hashlib.sha256(actor.encode()).hexdigest().upper()))
                db.commit()
            body = "分支资料" * 240
            message = json.loads(mcp(executable, state, sender, "send_to_agent", target=peer, message=body)["content"][0]["text"])
            original_path = Path(message["document"])
            assert original_path.parent == Path(paths["handoffs"])
            historical = new_id()
            archived_file = Path(topic["folder"])/"handoffs"/(historical+".md")
            archived_file.write_text("归档原文",encoding="utf8")
            with closing(sqlite3.connect(state/"workbench.sqlite")) as db:
                db.execute("INSERT INTO inbox(id,source,target,sender,text,files,document,state,document_ref,topic_id) "
                           "VALUES(?,?,?,?,'旧正文','[]',1,'delivered',?,?)",(historical,sender,peer,sender,str(archived_file),topic["id"]))
                db.commit()
            moved = dict(paths, handoffs=str(directory / "FF moved"))
            original_path.rename(original_path.with_suffix(".hold"))
            assert http(base, "/api/settings", moved, "PUT")[0] == 409
            original_path.with_suffix(".hold").rename(original_path)
            assert http(base, "/api/settings", moved, "PUT")[0] == 200
            assert (Path(moved["handoffs"])/archived_file.name).read_text("utf8") == "归档原文"
            assert mcp(executable,state,peer,"read_handoff_doc",pathOrId=str(archived_file))["content"][0]["text"] == "归档原文"
            assert (Path(moved["handoffs"])/original_path.name).read_text("utf8") == body
            assert mcp(executable, state, peer, "read_handoff_doc",pathOrId=str(original_path))["content"][0]["text"] == body
            route = "/api/windows"
            code, window = http(base, route, dict(topic=topic["id"], name="研究员",engine="claude",model="sonnet",effort="high",mode="plan",newSession=True))
            assert code == 200, code
            def args():
                for _ in range(80):
                    if (project / "grant-ready").is_file():
                        return (project / "claude-args.txt").read_text(encoding="utf-8-sig").splitlines()
                    time.sleep(.05)
                raise AssertionError("Claude fixture failed to receive its arguments and window grant")
            first = args()
            assert first[:4] == ["--session-id",window["session"],"--name","接口 & UI-研究员"], first[:4]
            assert first[4:10] == ["--model","sonnet","--effort","high","--permission-mode","plan"]
            server = json.loads(first[first.index("--mcp-config")+1])["mcpServers"]["lx_collab"]
            assert server["args"][-4:] == ["workbench-mcp", str(state), "--notify-port", str(urlsplit(base).port)]
            assert server["env"]["LX_P0_WINDOW_GRANT"] == "\u0024{LX_P0_WINDOW_GRANT}"
            assert first[-2:] == ["--allowedTools","mcp__lx_collab__*"]
            (project / "grant-ready").unlink()
            win = route + "/" + window["id"]
            assert http(base, win+"/session", {"session":history_id})[0] == 204
            resumed = args()
            assert resumed[:2] == ["--resume",history_id] and "--name" not in resumed
            assert http(base, win+"/history/delete", {"session":history_id})[0] == 409
            original_bytes = history_file.read_bytes()
            (project / "grant-ready").unlink()
            payload = {"topic":topic["id"],"engine":"claude","name":"分支研究","session":history_id,"forkSession":True}
            assert http(base, route, dict(payload, session=collision_id))[0] == 409
            status, fork = http(base, route, payload)
            assert status == 200 and fork["session"] != history_id
            launch = args()
            assert launch[:7] == ["--resume",history_id,"--fork-session","--session-id",fork["session"],"--name","接口 & UI-分支研究"]
            assert history_file.read_bytes() == original_bytes
            assert http(base, route + "/" + fork["id"], method="DELETE")[0] == 204
            assert http(base, win+"/session", {"newSession":True})[0] == 204
            assert http(base, win+"/history/archive", {"session":history_id})[0] == 204
            assert http(base, "/api/sessions/claude?archived=true")[1][0]["name"] == "历史会话"
            assert http(base, win+"/history/delete", {"session":history_id,"archived":True})[0] == 204
            assert not history_file.exists() and foreign.exists()
        with host(executable, state, project) as base:
            assert http(base, "/api/settings")[1]["archive_root"] == paths["archive_root"]
            assert http(base, "/api/settings", defaults, "PUT")[0] == 200


def verify_new_engines(executable, directory, fixture):
    state = directory / "multi-engine-data"
    env = {"FF_" + engine.upper() + "_EXE": str(fixture) for engine in ("codebuddy", "qwen", "traecli", "zcode", "qoder")}
    env["QODER_PERSONAL_ACCESS_TOKEN"] = ""
    env["QODER_CONFIG_DIR"] = str(directory / "qoder-profile")
    vault = directory / "zcode-test-credentials.json"
    vault.write_text('{"oauth:active_provider":"zai","oauth:zai:access_token":"test"}')
    env.update(OPENAI_API_KEY="test", CODEBUDDY_API_KEY="test", ZCODE_CREDENTIALS_FILE=str(vault))
    with patch.dict(os.environ, env), host(executable, state) as base:
        for engine in ("codebuddy", "qwen", "traecli", "zcode", "qoder"):
            project = directory / ("project " + engine)
            project.mkdir()
            topic = http(base, "/api/topics", {"name": engine, "project": str(project)})[1]
            payload = {"topic":topic["id"], "name":"助手", "engine":engine, "project":str(project), "newSession":True}
            if engine == "traecli":
                marker = project / "signed-out.fixture"
                marker.touch()
                account = "/api/engines/traecli/account?" + urlencode({"project": str(project)})
                assert http(base, account)[1] == {"status":"signed-out"}
                for selection in ({"newSession":True}, {"session":str(uuid4())}, {"session":str(uuid4()), "forkSession":True}):
                    assert http(base, "/api/windows", {**payload, "newSession":False, **selection})[0] == 401
                marker.unlink()
                assert http(base, account)[1] == {"status":"authenticated"}
                assert not any(w["agent"]["engine"] == engine for w in http(base, "/api/state")[1]["windows"])
            status, window = http(base, "/api/windows", payload)
            assert status == 200 and window["online"], (engine, window)
            route = "/api/windows/" + window["id"]
            launches = 3 if engine == "qoder" else 2
            for iteration in range(launches):
                if engine != "traecli":
                    view = next(v for v in http(base, "/api/state")[1]["windows"] if v["agent"]["id"] == window["id"])
                    assert view["activity"] == "idle" and not view["inputReady"] and view["agent"]["autoReceive"], view
                    captured = project / ("zcode-launch.txt" if engine == "zcode" else "claude-args.txt")
                    for _ in range(100):
                        if captured.exists() and captured.stat().st_size: break
                        time.sleep(.03)
                    args = captured.read_text(encoding="utf-8-sig").splitlines()
                    assert window["session"] in args, args
                    if engine == "zcode":
                        assert "--resume" in args and window["session"] == "sess_fixture1"
                        config = state / "engine-profiles" / window["id"] / ".zcode/cli/config.json"
                        server = json.loads(config.read_text())["mcp"]["servers"]["lx_collab"]
                        assert "env" not in server and not (project / ".zcode").exists()
                    elif engine == "qoder":
                        assert args[0] == ("--session-id" if iteration < 2 else "--resume"), args
                        assert ("--name" in args) == (iteration < 2), args
                        path = Path(args[args.index("--mcp-config")+1])
                        assert path.parent == state / "engine-profiles" / window["id"]
                        server = json.loads(path.read_text(encoding="utf8"))["mcpServers"]["lx_collab"]
                        assert server["env"] == {"LX_P0_WINDOW_GRANT": "${LX_P0_WINDOW_GRANT}"}
                        assert json.loads(args[args.index("--settings")+1])["ui"]["terminalBuffer"] is False
                    else:
                        assert args[0] == ("--resume" if iteration and engine != "qwen" else "--session-id")
                        server = json.loads(args[args.index("--mcp-config")+1])["mcpServers"]["lx_collab"]
                        if engine == "qwen": assert "env" not in server
                        else: assert server["env"]["LX_P0_WINDOW_GRANT"] == "${LX_P0_WINDOW_GRANT}"
                    assert server["args"][-4:] == ["workbench-mcp", str(state), "--notify-port", str(urlsplit(base).port)]
                assert http(base, route + "/stop", method="POST")[0] == 204
                if iteration + 1 < launches:
                    if engine != "traecli": captured.unlink()
                    if engine == "qoder" and iteration == 1:
                        (project / "acp-sessions.json").write_text(json.dumps([{"sessionId": window["session"], "cwd": str(project), "title": "saved"}]))
                    assert http(base, route + "/start", method="POST")[0] == 204

    # 旧版将三个开关全部锁为关闭。升级只发生一次，用户后来关闭的开关应当保留。
    with closing(sqlite3.connect(state / "workbench.sqlite")) as db, db:
        db.execute("DELETE FROM settings WHERE key='native_inbox_v2'")
        db.execute("UPDATE windows SET auto_receive=0")
    with host(executable, state) as base:
        agents = {v["agent"]["engine"]: v["agent"] for v in http(base, "/api/state")[1]["windows"]}
        assert all(agents[e]["autoReceive"] for e in ("qwen","codebuddy","zcode"))
        assert not agents["traecli"]["autoReceive"]
        assert http(base, "/api/windows/" + agents["qwen"]["id"] + "/auto-receive", {"enabled":False}, "PUT")[0] == 204
    with host(executable, state) as base:
        qwen = next(v["agent"] for v in http(base, "/api/state")[1]["windows"] if v["agent"]["engine"] == "qwen")
        assert not qwen["autoReceive"]


def verify_legacy_qwen(executable, directory, fixture):
    project = directory / "legacy qwen project"
    project.mkdir()
    (project / "qwen-legacy.fixture").touch()
    state = directory / "legacy-qwen-state"
    system = directory / "qwen-admin.json"
    defaults = directory / "qwen-defaults.json"
    defaults.write_text('{"ui":{"hideBanner":true}}')
    env = dict(FF_QWEN_EXE=str(fixture), OPENAI_API_KEY="test", NO_COLOR="1", QWEN_CODE_SYSTEM_SETTINGS_PATH=str(system),
               QWEN_CODE_SYSTEM_DEFAULTS_PATH=str(defaults))
    with patch.dict(os.environ, env), host(executable, state, project) as base:
        topic = http(base, "/api/topics", {"name":"旧版千问", "project":str(project)})[1]
        status, agent = http(base, "/api/windows", {"name":"助手", "engine":"qwen", "topic":topic["id"], "newSession":True})
        assert status == 200, agent
        route = "/api/windows/" + agent["id"]
        config = state / "engine-profiles" / agent["id"] / "qwen-mcp.json"
        server = json.loads(config.read_text(encoding="utf8"))["mcpServers"]["lx_collab"]
        assert server["args"][-4:] == ["workbench-mcp", str(state), "--notify-port", str(urlsplit(base).port)]
        assert server["env"]["LX_P0_WINDOW_GRANT"] == "${LX_P0_WINDOW_GRANT}"
        captured = project / "qwen-launch.txt"
        for step, option in enumerate(("--session-id", "--session-id", "--resume")):
            for _ in range(100):
                if captured.exists() and captured.stat().st_size: break
                time.sleep(.03)
            assert captured.read_text(encoding="utf-8-sig").splitlines() == [option, agent["session"]]
            assert http(base, route + "/stop", method="POST")[0] == 204
            captured.unlink()
            if step == 2: break
            if step == 1:
                (project / "acp-sessions.json").write_text(json.dumps([{
                    "sessionId":agent["session"], "cwd":str(project), "title":"已保存的千问会话"}]), encoding="utf8")
                listed = http(base, "/api/sessions/qwen?" + urlencode({"project":project,"includeBound":"true"}))[1]
                assert [(s["id"],s["name"]) for s in listed] == [(agent["session"],"已保存的千问会话")]
            assert http(base, route + "/start", method="POST")[0] == 204
        assert not system.exists() and defaults.read_text() == '{"ui":{"hideBanner":true}}'
        assert not (project / ".qwen").exists()


def verify_native(executable, directory):
    compiler = Path(os.environ["WINDIR"]) / "Microsoft.NET/Framework/v4.0.30319/csc.exe"
    fixture = directory / "engine.exe"
    subprocess.run([compiler, "/nologo", "/out:" + str(fixture),
                    Path(__file__).with_name("WorkbenchEngineFixture.cs")], check=True)
    verify_new_engines(executable, directory, fixture)
    verify_legacy_qwen(executable, directory, fixture)
    verify_install(executable, directory, fixture)
    verify_ff(executable, directory, fixture)
    snapshots = directory / "quota.json"
    projects = [directory / name for name in ("PaneA", "PaneB")]
    for project in projects:
        project.mkdir()
    document = projects[0] / "接口.md"
    document.write_text("交接正文", encoding="utf8")
    (projects[1] / "capture-input.fixture").touch()
    try:
        with patch.dict(os.environ, LX_CODEX_EXE=str(fixture), LX_COPILOT_EXE=str(fixture), LX_TEST_QUOTA=str(snapshots)):
            with host(executable, directory / "native-state", projects[0]) as base:
                assert http(base, "/api/topics")[1] == []
                topic = http(base, "/api/topics", {"name": "测试", "project": str(projects[0])})[1]["id"]
                windows = []
                for project in projects:
                    data = {"engine": "codex", "name": project.name, "topic": topic, "project": str(project)}
                    _, window = http(base, "/api/windows", data)
                    assert window["autoReceive"] is True
                    assert http(base, "/api/windows", data)[0] == 409
                    windows.append(window["id"])
                    for _ in range(100):
                        if (project / "holder.ready").exists():
                            break
                        time.sleep(.05)
                    assert (project / "holder.ready").exists()
                # Exercise the MCP, inbox and real ConPTY input route with a local line reader.
                grant = new_id()
                with closing(sqlite3.connect(directory / "native-state" / "workbench.sqlite")) as db:
                    db.execute("UPDATE windows SET grant_hash=? WHERE id=?",
                               (hashlib.sha256(grant.encode("ascii")).hexdigest().upper(), windows[0]))
                    db.commit()
                route = "/api/windows/" + windows[1] + "/name"
                assert http(base, route, {"name":"panea"}, "PUT")[0] == 409
                assert http(base, route, {"name":"审核员"}, "PUT")[0] == 204
                assert mcp(executable, directory / "native-state", grant, "send_to_agent",
                           target="@PaneB", message="旧地址")["isError"]
                queued = mcp(executable, directory / "native-state", grant, "send_to_agent",
                             target="@审核员", message="请审核接口", document=document.name,
                             files=[document.name], autoReturnHint="回送结论")
                assert not queued.get("isError"), queued
                message_id = json.loads(queued["content"][0]["text"])["id"]
                assert http(base, route, {"name":"审核负责人"}, "PUT")[0] == 204
                input_route = "/api/windows/" + windows[1] + "/input"
                # A line reader with no xterm observer receives Enter, but cannot attest that a CLI draft cleared.
                assert http(base, input_route, {"messageId": message_id})[0] == 503
                inbox = next(w["inbox"] for w in http(base, "/api/state")[1]["windows"] if w["agent"]["id"] == windows[1])
                assert [(item["id"], item["state"]) for item in inbox] == [(message_id, "delivery_uncertain")]
                assert http(base, input_route, {"messageId": message_id})[0] == 409
                received = (projects[1] / "received.fixture").read_text(encoding="utf-8-sig")
                assert received == (f"MSG_ID: {message_id[:8]} | FROM_AGENT: PaneA | SUMMARY: 请审核接口 | "
                                    f"HANDOFF_DOC: {document} | ATTACH_FILES: {document} | RETURN_HINT: 回送结论"), received
                assert http(base, "/api/windows/" + windows[0], method="DELETE")[0] == 204
                with (projects[0] / "session.lock").open("r+b"):
                    pass
                try:
                    (projects[1] / "session.lock").open("r+b").close()
                except PermissionError:
                    pass
                else:
                    raise AssertionError("Closing PaneA released PaneB's writer")
                def quota(refresh=False):
                    return http(base, "/api/quota/copilot" + ("?refresh=1" if refresh else ""))[1]

                snapshots.write_text('{"chat":{"isUnlimitedEntitlement":true},"premium_interactions":{"remainingPercentage":74.5}}')
                first = quota()
                assert first["primary"]["remainingPercent"] == 74.5 and first["supported"]
                snapshots.write_text('{"premium_interactions":{"remainingPercentage":0}}')
                assert quota() == first
                assert quota(True)["primary"]["remainingPercent"] == 0
                snapshots.write_text('{"premium_interactions":{"isUnlimitedEntitlement":true}}')
                assert quota(True)["unlimited"]
                snapshots.write_text('{}')
                assert quota(True)["supported"] is False
                snapshots.write_text(json.dumps({"primary": {"usedPercent": 100}, "credits": {"balance": "12.50", "unlimited": False}}))
                codex = http(base, "/api/quota/codex?refresh=1")[1]
                assert codex["primary"]["remainingPercent"] == 0 and codex["credits"]["balance"] == "12.50"
                for balance, unlimited in (("0", False), (None, True), (None, False)):
                    snapshots.write_text(json.dumps({"credits": {"balance": balance, "unlimited": unlimited}}))
                    codex = http(base, "/api/quota/codex?refresh=1")[1]
                    if balance is not None or unlimited:
                        assert codex["supported"] and codex["credits"] == {"balance": balance, "unlimited": unlimited}
                    else:
                        assert not codex["supported"] and codex["credits"] is None
                assert http(base, "/api/windows/" + windows[1] + "/auto-receive", {"enabled":False}, "PUT")[0] == 204
            # Force-terminating the host bypasses managed disposal.
            for _ in range(100):
                try:
                    (projects[1] / "session.lock").open("r+b").close()
                    break
                except PermissionError:
                    time.sleep(.05)
            else:
                raise AssertionError("Host exit left a session writer alive")
            with host(executable, directory / "native-state") as restarted:
                agent = next(w["agent"] for w in http(restarted, "/api/state")[1]["windows"] if w["agent"]["id"] == windows[1])
                assert agent["name"] == "审核负责人" and not agent["autoReceive"]
    finally:
        for project in projects:
            (project / "finish.fixture").touch()
        time.sleep(.2)


if __name__ == "__main__":
    temp_base = Path(tempfile.gettempdir()) / "codex-task"
    temp_base.mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="lx-workbench-", dir=temp_base) as work:
        verify(Path(sys.argv[1]).resolve(), Path(work).resolve())
        verify_archives_and_migration(Path(sys.argv[1]).resolve(), Path(work).resolve())
        verify_first_run(Path(sys.argv[1]).resolve(), Path(work).resolve())
        verify_location(Path(sys.argv[1]).resolve(), Path(work).resolve())
        verify_projects(Path(sys.argv[1]).resolve(), Path(work).resolve())
        verify_native(Path(sys.argv[1]).resolve(), Path(work).resolve())
    print("PASS: first-run setup, concurrent initialization, restart, topic archives and logs, WAL migration and retry, scoped handoffs, resend, shortcuts, native delivery, writer release and quota")
