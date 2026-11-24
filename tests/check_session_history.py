"""Exercise archive/restore and permanent deletion against installed CLIs using disposable, model-free history."""
from contextlib import closing
from datetime import datetime, timezone
import json
import msvcrt
import os
from pathlib import Path
import sqlite3
import sys
import tempfile
import time
from unittest.mock import patch
from urllib.parse import urlencode
from uuid import uuid4

from check_workbench import host, http


def seed(engine, home, project):
    sid = str(uuid4())
    now = datetime.now(timezone.utc)
    timestamp = now.isoformat(timespec="milliseconds").replace("+00:00", "Z")
    if engine == "codex":
        folder = home / "sessions" / now.strftime("%Y/%m/%d")
        folder.mkdir(parents=True, exist_ok=True)
        path = folder / f"rollout-{now:%Y-%m-%dT%H-%M-%S}-{sid}.jsonl"
        meta = dict(id=sid, cwd=str(project), timestamp=timestamp, originator="codex_cli_rs",
                    cli_version="0.160.0", source="cli", model_provider="openai")
        records = [dict(timestamp=timestamp, type="session_meta", payload=meta),
                   dict(timestamp=timestamp, type="event_msg", payload=dict(type="user_message", message="Drawer test"))]
    else:
        folder = home / "session-state" / sid
        folder.mkdir(parents=True)
        path = folder / "events.jsonl"
        (folder / "workspace.yaml").write_text(f"id: {sid}\ncwd: {project}\nsummary: Drawer test\n", encoding="utf8")
        first = str(uuid4())
        records = [dict(type="session.start", id=first, timestamp=timestamp, parentId=None,
                        data=dict(sessionId=sid, startTime=timestamp, version=1, producer="copilot-agent",
                                  context=dict(cwd=str(project)))),
                   dict(type="user.message", id=str(uuid4()), timestamp=timestamp, parentId=first,
                        data=dict(content="Drawer test", attachments=[]))]
    path.write_text("".join(json.dumps(record) + "\n" for record in records), encoding="utf8")
    return sid, path


def verify(executable, case):
    state = case / "workbench"
    project, other = case / "one", case / "two"
    project.mkdir()
    other.mkdir()
    env = {key: str(case / key) for key in ("CODEX_HOME", "COPILOT_HOME")}
    holdings = {}
    with patch.dict(os.environ, env):
        with host(executable, state, project) as base:
            topic = http(base, "/api/topics", {"name": "Drawer", "project": str(project)})[1]["id"]
            for engine, key in (("codex", "CODEX_HOME"), ("copilot", "COPILOT_HOME")):
                home = Path(env[key])
                sid, path = seed(engine, home, project)
                foreign, foreign_path = seed(engine, home, other)
                window, bound = uuid4().hex, uuid4().hex
                with closing(sqlite3.connect(state / "workbench.sqlite")) as db:
                    for id_, session in ((window, None), (bound, sid)):
                        db.execute("INSERT INTO windows(id,name,engine,project,topic_id,session) VALUES(?,?,?,?,?,?)",
                                   (id_, id_, engine, str(project), topic, session))
                    db.commit()
                route = f"/api/windows/{window}/history/"
                normal = f"/api/sessions/{engine}?" + urlencode({"project": str(project)})
                archive = normal + "&archived=true"
                assert http(base, normal)[1] == []
                all_rows = http(base, normal + "&includeBound=true")[1]
                assert any(s["id"] == sid and s["mayBeInUse"] for s in all_rows), (engine, all_rows)
                assert http(base, route + "delete", {"session": sid})[0] == 409
                assert http(base, route + "delete", {"session": foreign})[0] == 409
                assert path.exists() and foreign_path.exists()
                assert http(base, "/api/windows/" + bound, method="DELETE")[0] == 204
                if engine == "codex":
                    writer_lock = home / "thread-writer-locks" / (sid + ".lock")
                    writer_lock.parent.mkdir(exist_ok=True)
                    with writer_lock.open("ab+") as lock:
                        msvcrt.locking(lock.fileno(), msvcrt.LK_NBLCK, 1)
                        assert http(base, route + "delete", {"session": sid})[0] == 409
                else:
                    writer_lock = path.parent / f"inuse.{os.getpid()}.lock"
                    writer_lock.write_text(str(os.getpid()))
                    assert http(base, route + "delete", {"session": sid})[0] == 409
                    writer_lock.unlink()
                assert path.exists()
                assert http(base, route + "archive", {"session": sid})[0] == 204
                assert http(base, normal)[1] == [] and http(base, archive)[1][0]["id"] == sid
                holdings[engine] = (sid, path, foreign_path, route, normal, archive, home)
        with host(executable, state, project) as base:
            for engine, (sid, path, foreign_path, route, normal, archive, home) in holdings.items():
                assert http(base, archive)[1][0]["id"] == sid
                assert http(base, route + "restore", {"session": sid, "archived": True})[0] == 204
                assert http(base, normal)[1][0]["id"] == sid
                assert http(base, route + "delete", {"session": sid, "archived": True})[0] == 409
                assert http(base, route + "delete", {"session": sid})[0] == 204
                assert not path.exists() and foreign_path.exists()
                archived_id, archived_path = seed(engine, home, project)
                assert http(base, route + "archive", {"session": archived_id})[0] == 204
                assert http(base, route + "delete", {"session": archived_id, "archived": True})[0] == 204
                assert http(base, normal)[1] == [] and http(base, archive)[1] == []
                assert not archived_path.exists() and foreign_path.exists()
                if engine == "codex":
                    assert not list(home.rglob(f"*{archived_id}*.jsonl"))
                print(f"PASS {engine}: bound and foreign protection, archive across restart, restore, native delete from both lists")


def verify_titles(executable, case):
    project = case / "project"
    project.mkdir(parents=True)
    env = {key: str(case / key) for key in ("CODEX_HOME", "COPILOT_HOME")}
    with patch.dict(os.environ, env), host(executable, case / "state", project) as base:
        for engine, key in (("codex", "CODEX_HOME"), ("copilot", "COPILOT_HOME")):
            home = Path(env[key])
            original, source_file = seed(engine, home, project)
            topic_name = "中文话题 " + engine
            topic = http(base, "/api/topics", {"name": topic_name, "project": str(project)})[1]["id"]
            agent_name = "接口 开发"
            window = {"name": agent_name, "topic": topic, "engine": engine}
            catalog = "/api/sessions/" + engine + "?" + urlencode({"project": str(project), "includeBound": "true"})

            def titles():
                return {row["id"]: row["name"] for row in http(base, catalog)[1]}

            def check_native_title(sid, expected):
                if engine == "codex":
                    # 原生 /resume 名称索引，空会话尚未出现在 thread/list 中。
                    names = [json.loads(line) for line in (home / "session_index.jsonl").read_text(encoding="utf8").splitlines()]
                    assert [row["thread_name"] for row in names if row["id"] == sid][-1] == expected
                else:
                    deadline = time.monotonic() + 20
                    while titles().get(sid) != expected:
                        assert time.monotonic() < deadline, (sid, expected)
                        time.sleep(.2)
                    assert "user_named: true" in (home / "session-state" / sid / "workspace.yaml").read_text(encoding="utf8")

            before = titles()[original]
            # Codex 选择已有会话，Copilot 自动接续唯一的最近会话。
            selection = {"session": original} if engine == "codex" else {"newSession": False}
            status, resumed = http(base, "/api/windows", {**window, **selection})
            assert status == 200 and resumed["session"] == original and titles()[original] == before
            assert http(base, "/api/windows/" + resumed["id"], method="DELETE")[0] == 204
            status, forked = http(base, "/api/windows", {**window,"session":original,"forkSession":True})
            assert status == 200 and forked["session"] != original, (engine,status,forked)
            branch = forked["session"]
            check_native_title(branch, topic_name + "-" + agent_name)
            branch_file = (next(home.rglob("*" + branch + "*.jsonl")) if engine == "codex"
                           else home / "session-state" / branch / "events.jsonl")
            assert "Drawer test" in branch_file.read_text("utf8") and "Drawer test" in source_file.read_text("utf8")
            assert titles()[original] == before
            assert http(base, "/api/windows/" + forked["id"], method="DELETE")[0] == 204
            print(f"PASS {engine}: native fork has independent ID, original context and independent title")
            status, fresh = http(base, "/api/windows", {**window, "newSession": True})
            assert status == 200 and fresh["session"] != original
            check_native_title(fresh["session"], topic_name + "-" + agent_name)
            route = "/api/windows/" + fresh["id"]

            def bound_session():
                windows = http(base, "/api/state")[1]["windows"]
                return next(row["agent"]["session"] for row in windows if row["agent"]["id"] == fresh["id"])

            assert http(base, route + "/session", {"newSession": True})[0] == 204
            second = bound_session()
            assert second not in (original, fresh["session"])
            check_native_title(second, topic_name + "-" + agent_name)
            assert http(base, route + "/session", {"session": original})[0] == 204
            assert titles()[original] == before
            topic_name += " 二期"
            assert http(base, "/api/topics/" + topic, {"name": topic_name, "project": str(project)}, "PUT")[0] == 200
            assert http(base, route + "/session", {"newSession": True})[0] == 204
            current = bound_session()
            assert current not in (original, fresh["session"], second)
            check_native_title(current, topic_name + "-" + agent_name)
            assert titles()[original] == before
            assert http(base, route, method="DELETE")[0] == 204
            print(f"PASS {engine}: native topic-agent title at creation and after switching, original title preserved")


if __name__ == "__main__":
    temp = Path(tempfile.gettempdir()) / "codex-task"
    temp.mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(dir=temp, prefix="lx-session-history-") as work:
        verify(Path(sys.argv[1]).resolve(), Path(work).resolve())
        verify_titles(Path(sys.argv[1]).resolve(), Path(work).resolve() / "titles")
