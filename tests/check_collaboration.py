"""T19 black-box checks using disposable CLI profiles, HTTP, MCP and SQLite fixtures."""
from concurrent.futures import ThreadPoolExecutor
from contextlib import closing
import hashlib
import json
import os
from pathlib import Path
import shutil
import sqlite3
import subprocess
import sys
import tempfile
import time
from urllib.parse import urlsplit
from urllib.request import urlopen

from check_workbench import host, http, mcp, new_id


def verify_missing_skill(exe, case):
    install = case / "incomplete-install"
    install.mkdir()
    # Copy the application runtime only. Keep a decoy skill in the launch directory.
    for file in exe.parent.iterdir():
        if file.is_file() and file.suffix.lower() in (".dll", ".exe", ".json"):
            shutil.copy2(file, install)
    decoy = case / "ff-agent-collab" / "SKILL.md"
    decoy.parent.mkdir()
    decoy.write_text("wrong directory", encoding="utf8")
    data = case / "missing-skill-state"
    result = subprocess.run([install / exe.name, "workbench", data, "--fixed-data-dir", "--port", "0"],
                            cwd=case, capture_output=True, text=True, encoding="utf8", timeout=10)
    assert result.returncode == 1 and "缺少协作平台技能文件" in result.stderr, result
    assert os.path.normcase(str(install / "ff-agent-collab" / "SKILL.md")) in os.path.normcase(result.stderr)
    assert "LX_WORKBENCH" not in result.stdout and not data.exists()


def verify(exe, case):
    verify_missing_skill(exe, case)
    root = case / "state"
    project = case / "project"
    project.mkdir()
    plan = project / "PLAN.md"
    plan.write_text("正式文档", encoding="utf8")
    with host(exe, root, project) as url, closing(sqlite3.connect(root / "workbench.sqlite")) as db:
        topic = http(url, "/api/topics", {"name": "协作测试", "project": str(project)})[1]["id"]
        sender, peer, third = (new_id() for _ in range(3))
        for ident, name, engine in ((sender, "CTO", "copilot"), (peer, "Dev", "codex"), (third, "QA", "claude")):
            db.execute("INSERT INTO windows(id,name,engine,project,grant_hash,online,topic_id) VALUES(?,?,?,?,?,1,?)",
                       (ident, name, engine, str(project), hashlib.sha256(ident.encode()).hexdigest().upper(), topic))
        db.commit()

        def event(stream):
            assert stream.readline().strip() == b"data: state"
            assert stream.readline().strip() == b""

        with urlopen(url + "/api/events", timeout=8) as first, urlopen(url + "/api/events", timeout=8) as second:
            event(first)
            event(second)
            assert http(url, "/api/events/wake", {})[0] == 403
            legacy = mcp(exe, root, sender, "send_to_agent", target=peer, message="no wake from legacy MCP")
            assert not legacy.get("isError"), legacy
            event(first)
            event(second)
            # 刚完成低频核对，新提交须在下一轮 5 秒兜底之前唤醒两个页面。
            started = time.monotonic()
            sent = mcp(exe, root, sender, "send_to_agent", notify_port=urlsplit(url).port,
                       target=peer, message="SSE broadcast")
            message_id = json.loads(sent["content"][0]["text"])["id"]
            event(first)
            event(second)
            elapsed = time.monotonic() - started
            assert elapsed < 3, elapsed
        with urlopen(url + "/api/events", timeout=3) as reconnected:
            event(reconnected)
            assert http(url, "/api/messages/" + message_id)[1]["state"] == "pending"
        assert http(url, f"/api/windows/{peer}/inbox/{message_id}/acknowledge", {})[0] == 204
        print(f"PASS legacy reconciliation, SSE reconnect and two-page MCP notification in {elapsed:.2f}s")

        def call(who=sender, error=False, **args):
            result = mcp(exe, root, who, "send_to_agent", **args)
            assert bool(result.get("isError")) == error, result
            return None if error else json.loads(result["content"][0]["text"])

        def send(message, **args):
            return call(target=peer, message=message, **args)["id"]

        def visible():
            return {r[0] for r in db.execute("SELECT id FROM inbox WHERE acknowledged=0 AND target=?", (peer,))}

        old = send("first")
        independent = send("second", supersedePrevious=False)
        foreign = call(third, target=peer, message="independent sender")["id"]
        elsewhere = call(target=third, message="other recipient")["id"]
        db.execute("UPDATE inbox SET state='delivery_uncertain' WHERE id=?", (old,))
        db.commit()
        call(error=True, target=peer, message="bad path", document="../missing.md")
        assert {old, independent, foreign} == visible()
        latest = send("replacement")
        assert visible() == {latest, foreign}, visible()
        assert http(url, "/api/messages/" + old)[1]["acknowledged"]
        assert not http(url, "/api/messages/" + elsewhere)[1]["acknowledged"]
        call(third, error=True, cancel=latest)
        call(error=True, cancel=latest, message="ambiguous")
        assert call(cancel=latest)["id"] == latest
        call(error=True, cancel=latest)
        one = send("one")
        two = send("two", supersedePrevious=False)
        assert call(cancel=True, target="@Dev")["id"] == two
        assert visible() == {one, foreign}
        for state in ("writing", "delivered"):
            db.execute("UPDATE inbox SET state=? WHERE id=?", (state, one))
            db.commit()
            send("revise again")
            call(error=True, cancel=one)
            assert not http(url, "/api/messages/" + one)[1]["acknowledged"]

        with ThreadPoolExecutor(max_workers=4) as pool:
            replacements = set(pool.map(send, ["revision A", "revision B", "revision C", "revision D"]))
        assert len(visible() & replacements) == 1
        newest = db.execute("SELECT id FROM inbox WHERE source=? AND target=? ORDER BY rowid DESC LIMIT 1", (sender, peer)).fetchone()[0]
        assert visible() & replacements == {newest}

        def cleanup(days):
            status, data = http(url, "/api/settings/logs/cleanup", {"days":days})
            assert status == 200, (status, data)
            return data

        for invalid in ({}, {"days": 8}, {"days": None}):
            assert http(url, "/api/settings/logs/cleanup", invalid)[0] == 400
        assert http(url, "/api/settings/logs")[1]["total"] == db.execute("SELECT count(*) FROM inbox").fetchone()[0]
        cleanup(0)
        managed = call(target=peer, message="正文" * 1300)
        path = Path(managed["document"])
        clone = call(resend=managed["id"])
        formal = send("plan", document=str(plan))
        locked = send("currently writing")
        db.execute("UPDATE inbox SET created_at=datetime('now','-32 days') WHERE id=?", (managed["id"],))
        db.execute("UPDATE inbox SET created_at=NULL WHERE id=?", (formal,))
        db.execute("UPDATE inbox SET created_at=datetime('now','-16 days') WHERE id=?", (clone["id"],))
        db.execute("UPDATE inbox SET state='writing',created_at=NULL WHERE id=?", (locked,))
        db.commit()
        assert cleanup(30)["deleted"] == 2 and plan.is_file() and path.is_file()
        assert cleanup(15)["files"] == 1 and not path.exists()
        assert cleanup(7)["deleted"] == 0
        assert cleanup(0)["total"] == 1 and plan.is_file()

        # An attachment is an independent reference, even after the original log disappears.
        managed = call(target=peer, message="attachment body " * 200)
        path = Path(managed["document"])
        holder = send("holder", supersedePrevious=False)
        db.execute("UPDATE inbox SET files=? WHERE id=?", (json.dumps([str(path)]), holder))
        db.execute("UPDATE inbox SET created_at=datetime('now','-8 days') WHERE id=?", (managed["id"],))
        db.commit()
        assert cleanup(7)["files"] == 0 and path.is_file()
        assert cleanup(0)["files"] == 1 and not path.exists()

        # User documents with a GUID name, and absolute legacy references outside Handoffs, stay in place.
        formal_guid = project / (new_id() + ".md")
        formal_guid.write_text("user document", encoding="utf8")
        identifier = send("existing", document=str(formal_guid))
        db.execute("UPDATE inbox SET document=1 WHERE id=?", (identifier,))
        db.commit()
        assert cleanup(0)["files"] == 0 and formal_guid.read_text(encoding="utf8") == "user document"
        assert db.execute("SELECT state FROM inbox WHERE id=?", (locked,)).fetchone() == ("writing",)

        skill_files = [case / "cli-user" / folder / "skills/ff-agent-collab/SKILL.md"
                       for folder in (".codex", ".claude", ".agents")]
        assert not any(p.exists() for p in skill_files)
        canonical = (Path(__file__).resolve().parents[1] / "src/FF.AgentCollaboration/Skills/ff-agent-collab/SKILL.md").read_text(encoding="utf8")
        assert (exe.parent / "ff-agent-collab/SKILL.md").read_text(encoding="utf8") == canonical
        old = "<!-- Managed by FF.AgentCollaboration -->\nold package"
        for path in skill_files:
            path.parent.mkdir(parents=True, exist_ok=True)
        skill_files[0].write_text(old, encoding="utf8")
        skill_files[2].write_text("my own skill", encoding="utf8")
        def health():
            return next(i for i in http(url, "/api/environment")[1] if i["id"] == "collab_skill")
        assert not health()["ready"]
        assert http(url, "/api/environment/repair", {"item":"collab_skill"})[0] == 204
        assert health()["ready"] and not skill_files[0].exists() and not skill_files[1].exists()
        backup, = skill_files[0].parent.glob("SKILL.md.*.bak")
        assert backup.read_text(encoding="utf8") == old and skill_files[2].read_text(encoding="utf8") == "my own skill"
        init = {"jsonrpc":"2.0","method":"initialize","id":1}
        for grant, name in ((sender, "CTO"), (peer, "Dev"), (third, "QA"), (new_id(), None)):
            data = subprocess.run([exe, "workbench-mcp", root], input=json.dumps(init) + "\n", text=True,
                                  encoding="utf8", capture_output=True, check=True,
                                  env=dict(os.environ, LX_P0_WINDOW_GRANT=grant))
            reply = json.loads(data.stdout)
            skill = mcp(exe, root, grant, "read_collaboration_skill")
            if name is None:
                assert "error" in reply and "result" not in reply and skill["isError"]
            else:
                rule = reply["result"]["instructions"]
                assert "@" + name in rule and "协作测试" in rule and "document" in rule and "supersedePrevious" in rule and "read_collaboration_skill" in rule
                assert skill["content"][0]["text"].splitlines() == canonical.splitlines()
        pending = send("survives host restart", supersedePrevious=False)

    # Startup also retires a managed legacy entry without registering any global skill.
    skill_files[1].write_text(old, encoding="utf8")
    with host(exe, root, project) as url:
        assert not any(p.exists() for p in skill_files[:2]) and skill_files[2].read_text(encoding="utf8") == "my own skill"
        inbox = next(w["inbox"] for w in http(url, "/api/state")[1]["windows"] if w["agent"]["id"] == peer)
        assert {item["id"]:item["state"] for item in inbox} == {pending:"pending", locked:"delivery_uncertain"}
    print("PASS supersede, cancel, protected cleanup, authorized window skill and legacy global retirement")


if __name__ == "__main__":
    temp = Path(tempfile.gettempdir()) / "codex-task"
    temp.mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="ff-collab-", dir=temp) as directory:
        verify(Path(sys.argv[1]).resolve(), Path(directory))
