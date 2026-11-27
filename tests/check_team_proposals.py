"""Verify human-confirmed teams through a disposable workbench and real MCP."""
from contextlib import closing
from hashlib import sha256
import json
import os
from pathlib import Path
import sqlite3
import subprocess
import sys
import tempfile
from unittest.mock import patch
from urllib.parse import urlsplit

from check_workbench import host, http, mcp, new_id


def verify(exe, case):
    state, project = case / "state", case / "one"
    project.mkdir()
    fixture = case / "team-terminal.exe"
    compiler = Path(os.environ["WINDIR"]) / "Microsoft.NET/Framework/v4.0.30319/csc.exe"
    subprocess.run([compiler, "/nologo", "/main:TopicTerminalFixture", "/out:" + str(fixture),
                    Path(__file__).with_name("TopicTerminalFixture.cs"),
                    Path(__file__).with_name("WorkbenchEngineFixture.cs")], check=True)
    with patch.dict(os.environ, LX_CODEX_EXE=str(fixture), FF_CLAUDE_EXE=str(fixture), OPENAI_API_KEY="fixture",
                    FF_TEST_WORKBENCH_EXE=str(exe), FF_TEST_WORKBENCH_STATE=str(state)):
        with host(exe, state, project) as url, closing(sqlite3.connect(state / "workbench.sqlite")) as db:
            run(exe, state, project, url, db)


def run(exe, state, project, url, db):
    node = http(url, "/api/peers/node")[1]
    assert node["activeIp"] is None and not node["restartRequired"], node
    assert http(url, "/api/knowledge/project", {"project":str(project),"mode":"shared"}, "PUT")[0] == 200
    topic = http(url, "/api/topics", {"name":"团队编排", "project":str(project), "goal":"核对终端任务计划"})[1]["id"]
    leader, successor = new_id(), new_id()
    for agent, name in ((leader,"规划"),(successor,"接任")):
        db.execute("""INSERT INTO windows(id,name,engine,project,topic_id,online,auto_receive,grant_hash)
                   VALUES(?,?,'codex',?,?,1,0,?)""", (agent,name,str(project),topic,sha256(agent.encode()).hexdigest().upper()))
    db.commit()
    topic_route = "/api/topics/" + topic
    port = int(urlsplit(url).port)
    document_root = project / ".ff-acp-knowledge" / "topics"
    suggestions = [
        {"name":"验证","role":"核对流程。","taskSummary":"验证消息送达","suggestedEngine":"codex"},
        {"name":"不选","role":"处理额外需求。","taskSummary":"本轮跳过","suggestedEngine":"claude"}
    ]

    def invoke(grant, name, error=False, **args):
        result = mcp(exe, state, grant, name, notify_port=port, **args)
        assert bool(result.get("isError")) == error, result
        return result["content"][0]["text"] if error else json.loads(result["content"][0]["text"])

    def propose(grant, error=False, members=None):
        return invoke(grant,"propose_team_agents",error,summary="将验证工作分给合适的成员",
                      members=suggestions if members is None else members)

    def set_leader(agent):
        assert http(url,topic_route+"/leader",{"windowId":agent},"PUT")[0]==204

    def decision(proposal, action, data=None):
        return http(url,topic_route+"/team-proposals/"+proposal+"/"+action,data,"POST",timeout=45)

    def snapshot():
        return http(url,"/api/state?topic="+topic)[1]

    assert "组长" in propose(leader,True)
    assert snapshot()["teamProposals"] == []
    set_leader(leader)
    assert "组长" in propose(successor,True)
    propose(leader,True,members=[])
    propose(leader,True,members=suggestions*5)
    propose(leader,True,members=[suggestions[0],dict(suggestions[0], name="@验证")])
    propose(leader,True,members=[dict(suggestions[0], name="接任")])
    inventory = invoke(leader,"list_available_engines")
    assert {"codex","claude"} <= {e["engine"] for e in inventory if e["installed"]}, inventory
    selected = {"members":[{"index":0,"name":"确认后的成员","engine":"claude",
                            "model":"sonnet","effort":"high","role":"用户确认后的职责","mode":"default"}]}
    original=propose(leader)
    proposal=propose(leader)
    assert proposal["status"]=="awaiting_user_confirmation" and proposal["count"]==2
    assert [row["id"] for row in snapshot()["teamProposals"]]==[proposal["proposalId"]]
    assert len(snapshot()["windows"])==2 and not (project/"terminal-pids.txt").exists()
    route=topic_route+"/team-proposals/"+proposal["proposalId"]+"/confirm"
    assert http(url,route,selected,"POST",Origin="https://untrusted.example")[0]==403
    assert decision(original["proposalId"],"confirm",selected)[0]==409
    assert decision(proposal["proposalId"],"confirm",{"members":[]})[0]==400
    rejected=decision(proposal["proposalId"],"confirm",{"members":[dict(selected["members"][0],name="接任")]})
    assert rejected[0] in (400,409) and len(snapshot()["windows"])==2, rejected
    status,batch=decision(proposal["proposalId"],"confirm",selected)
    assert status==200 and batch["status"]=="created" and len(batch["members"])==1, (status,batch)
    agent=batch["members"][0]["windowId"]
    view=next(w["agent"] for w in snapshot()["windows"] if w["agent"]["id"]==agent)
    assert (view["name"],view["engine"],view["role"],view["online"])==("确认后的成员","claude","用户确认后的职责",True),view
    assert len((project/"terminal-pids.txt").read_text().splitlines())==1
    assert decision(proposal["proposalId"],"confirm",selected)[0]==409
    ids=db.execute("SELECT target,text FROM inbox WHERE source='workbench-system'").fetchall()
    assert any(target==leader and "确认后的成员" in text and "不选" in text for target,text in ids),ids
    team=next(document_root.glob("*/TEAM.md"))
    roster=team.read_text(encoding="utf8")
    assert all(name in roster for name in ("@规划","@接任","@确认后的成员","用户确认后的职责","验证消息送达"))
    assert "@不选" not in roster
    db.execute("UPDATE windows SET grant_hash=? WHERE id=?",(sha256(agent.encode()).hexdigest().upper(),agent))
    db.commit()
    context=invoke(agent,"kb_team_context")
    assert context["leader"]["id"]==leader and "确认后的成员" in context["team"]["summary"]
    assert context["plan"]["path"].endswith("PLAN.md") and "核对终端任务计划" in context["plan"]["summary"]
    team_path=context["team"]["path"]
    team_version=invoke(leader,"kb_read",path=team_path)["version"]
    invoke(leader,"kb_write",True,scope="topic",path="TEAM.md",content="未带版本")
    plan_path=context["plan"]["path"]
    invoke(leader,"kb_write",scope="topic",path="PLAN.md",content="# 团队验收总计划",
           expectedVersion=invoke(leader,"kb_read",path=plan_path)["version"])
    with team.open("a",encoding="utf8") as notes:
        notes.write("\n团队自定义备忘。\n")
    now_pending=propose(leader)["proposalId"]
    set_leader(successor)
    invoke(successor,"kb_write",True,scope="topic",path="TEAM.md",content="过期名册",expectedVersion=team_version)
    assert decision(now_pending,"confirm",selected)[0]==409
    assert "组长" in propose(leader,True)
    invoke(leader,"kb_write",True,scope="topic",path="PLAN.md",content="卸任后不可修改")
    invoke(successor,"kb_write",scope="topic",path="PLAN.md",content="# 新组长接手计划",
           expectedVersion=invoke(successor,"kb_read",path=plan_path)["version"])
    assert "团队自定义备忘" in team.read_text(encoding="utf8")
    context=invoke(agent,"kb_team_context")
    assert context["leader"]["id"]==successor and "新组长接手计划" in context["plan"]["summary"]
    pending=propose(successor)["proposalId"]
    assert decision(pending,"reject")[0]==204
    assert decision(pending,"reject")[0]==409
    assert db.execute("SELECT 1 FROM inbox WHERE target=? AND source='workbench-system' AND text LIKE '%拒绝%'",(successor,)).fetchone()
    print("PASS leader-only proposals, explicit selection and single startup, team notice and roster, transfer and rejection")
    remote = new_id()
    db.execute("""INSERT INTO fed_remote(id,window_id,endpoint,pin,provider,claim,purpose,name,agent,status)
               VALUES(?,?,'https://127.0.0.1:1',?,?,?,'团队验证','测试节点','审阅伙伴','approved')""",
               (remote,new_id(),"a"*64,new_id(),new_id()))
    db.execute("INSERT INTO fed_remote_topic VALUES(?,?)",(remote,topic))
    db.commit()
    invitation = dict(name="建议名称",role="审阅跨端修改",taskSummary="核对工作回执",remoteId=remote)
    proposal_id = propose(successor,members=[suggestions[0],invitation])["proposalId"]
    assert not db.execute("SELECT 1 FROM team_remote_members").fetchone()
    db.execute("INSERT INTO fed_remote_denial VALUES(?,?)",(remote,successor))
    db.commit()
    inputs = {"members":[dict(index=0,engine="codex"),dict(index=1,role="回执审阅")]}
    assert decision(proposal_id,"confirm",inputs)[0]==403
    assert not db.execute("SELECT 1 FROM windows WHERE name='验证'").fetchone()
    assert not db.execute("SELECT 1 FROM team_remote_members").fetchone()
    db.execute("DELETE FROM fed_remote_denial WHERE remote=?",(remote,))
    db.commit()
    status,batch = decision(proposal_id,"confirm",{"members":[inputs["members"][1]]})
    assert status==200 and batch["status"]=="created", (status,batch)
    member=batch["members"][0]
    assert member["remoteId"]==remote and not member["online"] and not member.get("windowId"), member
    assert len((project/"terminal-pids.txt").read_text().splitlines())==1
    assert not db.execute("SELECT 1 FROM fed_outbox").fetchone()
    assert next(p for p in invoke(successor,"list_windows",detail=True) if p["id"]==remote)["teamMember"]
    member=next(p for p in invoke(successor,"kb_team_context")["members"] if p["id"]==remote)
    assert member["remote"] and member["authorized"] and member["taskSummary"]=="核对工作回执", member
    assert "已经是" in propose(successor,True,members=[invitation])
    assert "外部成员 · 测试节点" in team.read_text(encoding="utf8")
    print("PASS external team confirmation, offline admission, live revocation and atomic rollback, no local process or dispatch")
    if "--ui" in sys.argv:
        subprocess.run(["node", Path(__file__).with_name("check_team_proposal_ui.cjs"), url, exe, state, topic, successor],
                       check=True, timeout=180)
    assert http(url,"/api/peers/remotes/"+remote+"?topic="+topic,method="DELETE")[0]==204
    assert remote not in {p["id"] for p in invoke(successor,"kb_team_context")["members"]}
    assert "审阅伙伴" not in team.read_text(encoding="utf8")


if __name__ == "__main__":
    tmp=Path(tempfile.gettempdir())/"codex-task"
    tmp.mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(dir=tmp,prefix="ff-team-proposals-") as root:
        verify(Path(sys.argv[1]).resolve(),Path(root).resolve())
