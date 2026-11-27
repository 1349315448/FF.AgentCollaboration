"""Verify the project knowledge library and permissions with real HTTP, MCP, and SQLite."""
from concurrent.futures import ThreadPoolExecutor
from contextlib import closing
import hashlib
import json
import os
from pathlib import Path
import sqlite3
import subprocess
import sys
import tempfile
from urllib.parse import urlencode

from check_knowledge_leader import verify_leader
from check_workbench import host, http, mcp, new_id


def git(project, *options):
    return subprocess.check_output(["git", "-C", str(project), *options], encoding="utf8").strip()


def verify_library(exe, case):
    project, other_project, runtime = [case / name for name in ("project", "other-project", "workbench")]
    for folder in (project, other_project):
        folder.mkdir()
        git(folder, "init", "-q")
    with host(exe, runtime, project) as url, closing(sqlite3.connect(runtime / "workbench.sqlite")) as db:
        topic = http(url, "/api/topics", {"name": "接口交接", "project": str(project)})[1]["id"]
        another_topic = http(url, "/api/topics", {"name": "另一个话题", "project": str(project)})[1]["id"]
        leader, member, outsider = [new_id() for _ in range(3)]
        for window, name, parent in ((leader, "组长", topic), (member, "成员", topic), (outsider, "外窗", another_topic)):
            db.execute("""INSERT INTO windows(id,name,engine,project,topic_id,online,grant_hash,role)
                       VALUES(?,?,'codex',?,?,1,?,?)""",
                       (window, name, str(project), parent, hashlib.sha256(window.encode()).hexdigest().upper(), name + "职责"))
        db.commit()

        def call(window, tool, fails=False, **args):
            response = mcp(exe, runtime, window, tool, **args)
            assert bool(response.get("isError")) == fails, response
            return None if fails else json.loads(response["content"][0]["text"])

        def mode(value):
            response = http(url, "/api/knowledge/project", {"project": str(project), "mode": value}, "PUT")
            assert response[0] == 200 and response[1]["mode"] == value, response
            assert http(url, "/api/state")[1]["knowledgeModes"][str(project)] == value

        def search(**args):
            response = http(url, "/api/knowledge/search?" + urlencode({"project": str(project), **args}, doseq=True))
            assert response[0] == 200, response
            return response[1]

        directory = project / ".ff-acp-knowledge"
        assert http(url, "/api/state")[1]["knowledgeModes"][str(project)] == "disabled"
        assert not directory.exists() and git(project, "status", "--porcelain") == ""
        call(member, "kb_search", True)
        added = http(url, "/api/project", {"path": str(other_project), "knowledgeMode": "shared"}, "PUT")
        assert added[0] == 200, added
        assert (other_project / ".ff-acp-knowledge" / "project" / "INDEX.md").is_file()
        exclude = project / ".git" / "info" / "exclude"
        with exclude.open("a", encoding="utf8") as stream:
            stream.write("\nlocal-note.txt\n")
        mode("local_only")
        mode("local_only")
        assert git(project, "status", "--porcelain") == ""
        assert exclude.read_text(encoding="utf8").count(".ff-acp-knowledge/") == 1
        mode("shared")
        assert "local-note.txt" in exclude.read_text(encoding="utf8")
        assert ".ff-acp-knowledge" in git(project, "status", "--porcelain")
        assert (directory / ".gitignore").read_text(encoding="utf8").splitlines() == [".index/"]
        assert git(project, "check-ignore", ".ff-acp-knowledge/.index/knowledge.db")
        assert http(url, "/api/topics/" + topic + "/leader", {"windowId": leader}, "PUT")[0] in (200, 204)

        for scope, path in [("topic", "PLAN.md"), ("topic", "TEAM.md"), ("topic", "SUMMARY.md"), ("project", "policy.md")]:
            call(member, "kb_write", True, scope=scope, path=path, content="没有写入权限")
        content = "# 核对接口\n\n使用终端确认回执。"
        meta = {"theme": "消息投递", "keywords": ["确认回执", "协议"], "status": "working"}
        plan_path = call(leader, "kb_team_context")["plan"]["path"]
        plan = call(leader, "kb_write", scope="topic", path="PLAN.md", content=content,
                    expectedVersion=call(leader,"kb_read",path=plan_path)["version"], **meta)
        assert plan["path"].startswith("topics/接口交接-" + topic[:6] + "/") and content in plan["body"]
        assert plan["body"].count("[成员任务记录](records/)")==1
        assert call(member, "kb_read", path=plan["path"])["frontmatter"]["theme"] == "消息投递"
        call(outsider, "kb_read", True, path=plan["path"])
        context = json.dumps(call(member, "kb_team_context"), ensure_ascii=False)
        assert "组长" in context and "成员职责" in context and "使用终端确认回执" in context
        search(topicId=topic)
        assert not list((directory / "agents").glob("*/MEMORY.md"))
        assert call(member, "kb_team_context").get("memory") is None
        memory = call(member, "kb_write", scope="agent", path="tasks/notes.md", content="成员的个人记录")
        assert memory["path"].startswith("agents/成员-" + member[:6] + "/")
        legacy = call(member, "kb_write", scope="agent", path="MEMORY.md", content="已有个人经验")
        assert call(member, "kb_team_context")["memory"]["path"] == legacy["path"]
        call(leader, "kb_write", True, scope="agent", path=memory["path"], content="不能改成员记录")
        call(leader, "kb_read", True, path=memory["path"])
        record = call(member, "kb_write", scope="topic", path="records/summary.md", content="成员写自己的交接")
        call(outsider, "kb_read", True, path=record["path"])
        call(outsider, "kb_write", True, scope="topic", path=record["path"], content="越权写入")
        peer = new_id()
        db.execute("""INSERT INTO windows(id,name,engine,project,topic_id,online,grant_hash)
                      VALUES(?,'联调','codex',?,?,1,?)""",(peer,str(project),topic,hashlib.sha256(peer.encode()).hexdigest().upper()))
        db.commit()
        with ThreadPoolExecutor(max_workers=3) as pool:
            notes = [pool.submit(call, agent, "kb_write", scope="topic", path="records/同名任务.md", content=body)
                     for agent,body in [(member,"成员计划"),(peer,"联调计划")]]
            edit = pool.submit(call,leader,"kb_write",scope="topic",path="PLAN.md",content=content+"\n新增验收项",
                               expectedVersion=plan["version"])
            plan = edit.result()
            a,b = [note.result() for note in notes]
        assert a["path"] != b["path"] and plan["body"].count("[成员任务记录](records/)")==1
        selector = call(leader,"kb_team_context")["taskRecords"]["arguments"]
        records = call(leader,"kb_search",**selector)
        assert {r["path"] for r in records} == {a["path"],b["path"],record["path"]}
        assert [call(leader,"kb_search",**selector,limit=1,offset=i)[0]["path"] for i in range(3)] == [r["path"] for r in records]
        assert not call(outsider,"kb_search",recordsOnly=True)
        call(outsider,"kb_search",True,**selector)
        call(member,"kb_write",True,scope="topic",path=a["path"],content="缺少版本")
        call(leader,"kb_write",True,scope="topic",path=plan_path,content="缺少版本")
        with ThreadPoolExecutor(max_workers=2) as pool:
            updates = [pool.submit(mcp,exe,runtime,member,"kb_write",scope="topic",path=a["path"],
                                   content=body,expectedVersion=a["version"]) for body in ("证据 A","证据 B")]
            replies = [job.result() for job in updates]
        assert sum(not reply.get("isError") for reply in replies)==1, replies
        assert call(leader,"kb_read",path=plan_path)["version"]==plan["version"]
        call(member,"kb_write",True,scope="topic",path=b["path"],content="跨成员覆盖",expectedVersion=b["version"])
        policy = call(leader, "kb_write", scope="project", path="policy.md", content="所有话题可读取")
        assert call(outsider, "kb_read", path=policy["path"])["body"].strip()=="所有话题可读取"
        hits = search(topicId=topic, query="确认回执", theme="消息投递", keywords=["协议"])
        assert [hit["path"] for hit in hits] == [plan["path"]], hits
        assert not call(outsider, "kb_search", query="确认回执")
        assert not call(leader, "kb_search", query="成员的个人记录")
        assert http(url, "/api/topics/" + topic + "/leader", {"windowId": member}, "PUT")[0] in (200, 204)
        call(leader, "kb_write", True, scope="topic", path="PLAN.md", content="卸任后不可修改")
        assert http(url, "/api/topics/" + topic + "/leader", {"windowId": leader}, "PUT")[0] in (200, 204)

        for path in ("../outside.md", ".index/knowledge.db", str(directory / plan["path"]), "project/a/../../outside.md"):
            call(leader, "kb_read", True, path=path)
        outside = case / "outside"
        outside.mkdir()
        (outside / "secret.md").write_text("private outside file", encoding="utf8")
        junction = directory / "project" / "outside"
        if os.name == "nt":
            subprocess.run(["cmd.exe", "/d", "/c", "mklink", "/J", junction, outside], check=True, capture_output=True)
        else:
            junction.symlink_to(outside, target_is_directory=True)
        try:
            call(leader, "kb_read", True, path="project/outside/secret.md")
            call(leader, "kb_write", True, scope="project", path="outside/secret.md", content="不能覆盖")
            assert (outside / "secret.md").read_text(encoding="utf8") == "private outside file"
            assert not search(query="private outside file")
        finally:
            os.rmdir(junction) if os.name == "nt" else junction.unlink()
        invalid = directory / "project" / "invalid.md"
        invalid.write_bytes(b"\xffinvalid")
        call(leader, "kb_read", True, path="project/invalid.md")
        call(leader, "kb_write", True, scope="project", path="large.md", content="汉" * 175000)
        assert not (directory / "project" / "large.md").exists()
        index = directory / ".index" / "knowledge.db"
        assert index.is_file()
        for remove in (True, False):
            if remove:
                index.unlink()
            else:
                index.write_bytes(b"invalid SQLite file")
            assert search(query="确认回执")[0]["path"] == plan["path"]
        with closing(sqlite3.connect(index)) as derived:
            assert "fts5" in derived.execute("SELECT sql FROM sqlite_master WHERE name='knowledge_fts'").fetchone()[0]
        example = "新版计划\n\n~~~md\n[成员任务记录](records/)\n~~~"
        refreshed = call(leader, "kb_write", scope="topic", path="PLAN.md", content=example, expectedVersion=plan["version"])
        assert refreshed["path"] == plan["path"]
        assert refreshed["body"].startswith(example) and refreshed["body"].rstrip().endswith("\n\n[成员任务记录](records/)")
        call(leader, "kb_write", True, scope="topic", path="PLAN.md", content="过期版本", expectedVersion=plan["version"])
        assert (directory / plan["path"]).is_file()
        assert http(url, "/api/topics/" + topic, {"name":"新话题名", "project":str(project)}, "PUT")[0] == 200
        assert call(leader, "kb_write", scope="topic", path="PLAN.md", content="改名后",expectedVersion=refreshed["version"])["path"] == plan["path"]
        db.execute("INSERT INTO settings VALUES(?, '1')", ("peer_reader." + leader,))
        db.commit()
        call(leader, "kb_write", True, scope="project", path="forbidden.md", content="专用审阅窗口")
        call(leader, "kb_team_context", True)
        guide = mcp(exe, runtime, leader, "read_team_skill")
        assert not guide.get("isError") and guide["content"][0]["text"].splitlines() == (exe.parent / "ff-team-member/SKILL.md").read_text(encoding="utf8").splitlines()
        initialized = subprocess.run([exe, "workbench-mcp", runtime], text=True, encoding="utf8", capture_output=True,
            input=json.dumps({"id":1,"jsonrpc":"2.0","method":"initialize"})+"\n",
            env=dict(os.environ,LX_P0_WINDOW_GRANT=leader),check=True,timeout=15)
        instructions = json.loads(initialized.stdout)["result"]["instructions"]
        assert "专用审阅会话" in instructions and "你是本话题组长" not in instructions
        skill = mcp(exe, runtime, member, "read_knowledge_skill")["content"][0]["text"]
        assert all(term in skill for term in ("ff-acp-knowledge", "kb_team_context", "need_info", "finalReply"))
        mode("disabled")
        call(member, "kb_search", True)
        assert (directory / plan["path"]).exists()
        mode("shared")
        assert search(query="改名后")[0]["path"] == plan["path"]
    with host(exe, runtime, project) as url:
        assert http(url, "/api/state")[1]["knowledgeModes"][str(project)] == "shared"
        assert (directory / plan["path"]).exists()
    print("PASS knowledge modes, role scopes, metadata, path guards, FTS5 sync/recovery, rename and persistence")


if __name__ == "__main__":
    temporary = Path(tempfile.gettempdir()) / "codex-task"
    temporary.mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(dir=temporary, prefix="ff-p19-") as location:
        exe = Path(sys.argv[1]).resolve()
        case = Path(location).resolve()
        verify_library(exe, case)
        verify_leader(exe, case)
