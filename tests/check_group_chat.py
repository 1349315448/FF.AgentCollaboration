"""Group chat through the HTTP and MCP entry points, using isolated workbench data."""
from contextlib import closing
import hashlib
from http.client import HTTPSConnection
import json
import os
from pathlib import Path
import sqlite3
import ssl
import subprocess
import sys
import tempfile
import time
import uuid
from urllib.request import Request, urlopen
from urllib.parse import urlsplit

from check_workbench import host, http, mcp, new_id
from check_federation import until


def agent(db, topic, project, name):
    window, secret = new_id(), new_id() + new_id()
    digest = hashlib.sha256(secret.encode()).hexdigest().upper()
    db.execute("""INSERT INTO windows(id,name,project,topic_id,engine,grant_hash,online,auto_receive)
                  VALUES(?,?,?,?,'codex',?,1,0)""", (window,name,str(project),topic,digest))
    return window, secret


def check(exe, case):
    project, other = case / "repo", case / "external"
    project.mkdir()
    other.mkdir()
    (project / "src" / "成果 文件").mkdir(parents=True)
    (project / "接口说明.md").write_text("approved project attachment",encoding="utf8")
    root = case / "state"
    with host(exe,root,project) as base:
        def group(name,folder=project):
            code,value=http(base,"/api/topics",{"name":name,"project":str(folder)})
            assert code==200,value
            return value["id"]
        def history(topic):
            status,rows=http(base,f"/api/topics/{topic}/chat")
            assert status==200,rows
            return rows
        def send(topic, text, targets=None, **extra):
            return http(base,f"/api/topics/{topic}/chat",{"text":text,"targets":targets,**extra})
        team,linked,unlinked = group("开发"),group("测试"),group("未关联")
        assert http(base,"/api/project",{"path":str(other)},"PUT")[0]==200
        external = group("别的项目",other)
        db = sqlite3.connect(root/"workbench.sqlite")
        if "--browser" in sys.argv:
            offline,_ = agent(db,team,project,"离线成员")
            db.execute("UPDATE windows SET online=0 WHERE id=?",(offline,))
        lead, lead_grant = agent(db,team,project,"组长")
        member, member_grant = agent(db,team,project,"开发")
        target,target_grant = agent(db,linked,project,"测试组长")
        linked_member,linked_key = agent(db,linked,project,"测试成员")
        third,_ = agent(db,unlinked,project,"其他组长")
        external_lead,_ = agent(db,external,other,"外部组长")
        for topic, window in [(team,lead),(linked,target),(unlinked,third),(external,external_lead)]:
            db.execute("UPDATE topics SET leader_window_id=? WHERE id=?",(window,topic))
        db.commit()
        assert send(team,"普通记录，无 Agent 调用")[0]==200
        assert send(team,"仅引用 @开发 的名字，未选收件人")[0]==200
        assert db.execute("SELECT count(*) FROM inbox").fetchone()[0]==0
        assert mcp(exe,root,lead_grant,"send_to_agent",message="没有明确收件人").get("isError")
        assert send(team,"未关联", [target])[0]==403
        assert send(team,"未关联不可跨项目", [external_lead])[0]==403
        assert http(base,f"/api/topics/{team}/chat/links",{"group":external})[0]==403
        assert http(base,f"/api/topics/{team}/chat/links",{"group":linked})[0]==204
        assert http(base,f"/api/topics/{linked}/chat/links")[1][0]["leaderId"]==lead
        assert send(team,"请一起执行",[member,target],files=["接口说明.md"],requestReply=True)[0]==200
        post=next(post for post in history(team) if post["text"]=="请一起执行")
        assert len(post["receipts"])==2 and {r["delivery"] for r in post["receipts"]}=={"pending"}
        assert post["recipients"]==["开发","测试组长"]
        assert next(p for p in history(linked) if p["id"]==post["id"])["recipients"]==["测试组长"]
        assert len(next(p for p in history(linked) if p["id"]==post["id"])["receipts"])==1
        assert not any(p["id"]==post["id"] for p in history(unlinked))
        attachment=f"/api/topics/{team}/chat/{post['id']}/attachments/0"
        with urlopen(base+attachment) as response:
            assert response.read()==b"approved project attachment"
            assert response.headers["Content-Disposition"].startswith("attachment;")
        assert http(base,attachment.replace(team,unlinked))[0]==403
        first=next(r for r in post["receipts"] if r["target"]==member)["id"]
        db.execute("UPDATE inbox SET state='delivered' WHERE id=?",(first,))
        db.commit()
        assert [r["delivery"] for r in next(p for p in history(team) if p["id"]==post["id"])["receipts"]].count("delivered")==1
        reply=mcp(exe,root,member_grant,"send_to_agent",target="@工作台用户",message="开发完成",replyTo=first)
        assert not reply.get("isError"), reply
        assert next(p for p in history(team) if p["text"]=="开发完成")["replyTo"]==post["id"]
        assert send(team,"接口可以先拆出来吗",[member])[0]==200
        question=next(p for p in history(team) if p["text"]=="接口可以先拆出来吗")
        discussion=question["receipts"][0]["id"]
        run_hint=db.execute("SELECT return_hint FROM inbox WHERE id=?",(discussion,)).fetchone()[0]
        assert run_hint is None
        db.execute("UPDATE inbox SET native_offer='manual' WHERE id=?",(discussion,))
        db.commit()
        received=mcp(exe,root,member_grant,"receive_message",messageId=discussion)
        prompt=json.loads(received["content"][0]["text"])["message"]
        assert all(hint in prompt for hint in ("GROUP_REPLY:","@工作台用户","replyTo")), prompt
        finished=mcp(exe,root,member_grant,"report_task_status",messageId=discussion,status="completed")
        assert not finished.get("isError"), finished
        reply=mcp(exe,root,member_grant,"send_to_agent",target="@工作台用户",message="可以先拆 API",replyTo=discussion)
        assert not reply.get("isError"), reply
        assert next(p for p in history(team) if p["text"]=="可以先拆 API")["replyTo"]==question["id"]
        denied=mcp(exe,root,member_grant,"send_to_agent",target=target,message="非组长跨组")
        assert denied.get("isError"), denied
        posted=mcp(exe,root,lead_grant,"send_to_agent",targets=[member,target],message="组长多选")
        assert not posted.get("isError"),posted
        assert next(p for p in history(team) if p["text"]=="组长多选")["receipts"][1]["target"]==target
        assert mcp(exe,root,member_grant,"update_team_context",goal="越权修改").get("isError")
        edit=mcp(exe,root,lead_grant,"update_team_context",goal="小组目标",roles=[{"agentId":member,"role":"开发接口"}])
        assert not edit.get("isError"),edit
        assert db.execute("SELECT goal FROM topics WHERE id=?",(team,)).fetchone()[0]=="小组目标"
        assert db.execute("SELECT role FROM windows WHERE id=?",(member,)).fetchone()[0]=="开发接口"
        assert mcp(exe,root,lead_grant,"update_team_context",goal="回滚测试",roles=[{"agentId":target,"role":"越权"}]).get("isError")
        assert db.execute("SELECT goal FROM topics WHERE id=?",(team,)).fetchone()[0]=="小组目标"
        linked_message=next(r for r in post["receipts"] if r["target"]==target)["id"]
        db.execute("UPDATE inbox SET state='delivered' WHERE id=?",(linked_message,))
        db.commit()
        cross_reply=mcp(exe,root,target_grant,"send_to_agent",target="@工作台用户",replyTo=linked_message,
                        message="跨组反馈，待复核",finalReply=False)
        assert not cross_reply.get("isError"), cross_reply
        mirrored=next(p for p in history(team) if p["text"]=="跨组反馈，待复核")
        assert mirrored["topic"]==linked and mirrored["replyTopic"]==team
        handoffs=http(base,"/api/state")[1]["recentHandoffs"]
        route=next(r for r in handoffs if r["id"]==mirrored["id"])
        assert (route["sourceTopic"],route["targetTopic"])==(linked,team)
        assert http(base,f"/api/topics/{team}/chat/links/{linked}",method="DELETE")[0]==204
        assert mcp(exe,root,lead_grant,"send_to_agent",target=target,message="解除后拒绝").get("isError")
        assert mcp(exe,root,target_grant,"send_to_agent",target="@工作台用户",replyTo=linked_message,
                   message="解除后不能跨组回工作台用户").get("isError")
        assert send(team,"解除后拒绝",[target])[0]==403
        assert history(linked)
        routes=f"/api/topics/{team}/chat/links"
        selected={"group":linked,"enabled":True,"members":[member],"peerMembers":[linked_member]}
        assert http(base,routes,[selected,{"group":unlinked,"enabled":True,"members":[],"peerMembers":[]}],"PUT")[0]==204
        assert send(team,"工作台点名获准组员",[linked_member])[0]==200
        assert not mcp(exe,root,member_grant,"send_to_agent",target="@测试/测试成员",message="获准成员跨组").get("isError")
        assert not mcp(exe,root,linked_key,"send_to_agent",target=member,message="获准成员双向").get("isError")
        assert mcp(exe,root,linked_key,"send_to_agent",target=third,message="禁止传递授权").get("isError")
        assert mcp(exe,root,member_grant,"send_to_agent",target=third,message="按关联隔离成员名单").get("isError")
        assert http(base,routes,[{"group":linked,"enabled":False},{"group":external,"enabled":True}],"PUT")[0]==403
        assert send(team,"检查整批回滚",[linked_member])[0]==200
        assert http(base,routes,[{**selected,"peerMembers":[member]}],"PUT")[0]==403
        assert http(base,routes,[{**selected,"members":[],"peerMembers":[]}],"PUT")[0]==204
        assert send(team,"收回成员权限",[linked_member])[0]==403
        assert http(base,routes,[{"group":linked,"enabled":False},{"group":unlinked,"enabled":False}],"PUT")[0]==204
        boundary="ff-"+uuid.uuid4().hex
        sample=b"attachment payload\n"*20000
        payload=(f"--{boundary}\r\nContent-Disposition: form-data; name=\"message\"\r\n\r\n" +
                 json.dumps({"text":"上传附件"}) +
                 f"\r\n--{boundary}\r\nContent-Disposition: form-data; name=\"files\"; filename=\"note.txt\"\r\n"+
                 "Content-Type: text/plain\r\n\r\n").encode()+sample+f"\r\n--{boundary}--\r\n".encode()
        with urlopen(Request(base+f"/api/topics/{team}/chat",data=payload,headers={
            "Content-Type":"multipart/form-data; boundary="+boundary})) as response:
            uploaded=json.load(response)["id"]
        with urlopen(base+f"/api/topics/{team}/chat/{uploaded}/attachments/0") as response:
            assert response.read()==sample
            assert "note.txt" in response.headers["Content-Disposition"]
        rows=http(base,f"/api/topics/{team}/chat?refresh={post['id']},{uploaded}")[1]
        assert {row["id"] for row in rows}=={post["id"],uploaded}
        assert not http(base,f"/api/topics/{unlinked}/chat?refresh={post['id']}")[1]
        package = "src/成果 文件/发布.zip"
        (project/package).write_bytes(b"PK\x03\x04\0")
        address = "https://example.org/releases/v1?q=1#download"
        result=mcp(exe,root,member_grant,"send_to_agent",target="@工作台用户",
                   message="成果清单\n"+address, files=["接口说明.md",package])
        assert not result.get("isError"),result
        result=mcp(exe,root,member_grant,"send_to_agent",target="@工作台用户",message="仅交付网址\n"+address)
        assert not result.get("isError"),result
        manifest=next(p for p in history(team) if p["text"].startswith("成果清单"))
        assert [a["path"] for a in manifest["attachments"]]==["接口说明.md",package]
        assert not manifest["receipts"] and not manifest["replyTo"]
        assert mcp(exe,root,member_grant,"send_to_agent",target="@工作台用户",message="纯文本").get("isError")
        assert mcp(exe,root,member_grant,"send_to_agent",target="@工作台用户",message="越界",files=["../etc"]).get("isError")
        db.close()
        if "--browser" in sys.argv:
            subprocess.run(["node",Path(__file__).with_suffix(".cjs"),base,team,member,linked],check=True,timeout=100,
                           env=dict(os.environ, FF_CHAT_TEST_HOST=str(exe), FF_CHAT_TEST_STATE=str(root), LX_P0_WINDOW_GRANT=lead_grant))
    print("PASS group privacy, reciprocal links, multiple targets, durable receipts, user replies, leader changes and attachment download")


def check_layout(exe, case):
    with host(exe,case/"state",case) as base:
        topic = http(base,"/api/topics",{"name":"窗格总览"})[1]["id"]
        with closing(sqlite3.connect(case/"state"/"workbench.sqlite")) as db:
            ids = [agent(db,topic,case,f"窗格 {i:02}")[0] for i in range(1,11)]
            db.commit()
        subprocess.run(["node",Path(__file__).with_suffix(".cjs"),base,topic,"--layout",*ids],check=True,timeout=75)


def check_peers(exe, case):
    left, right = case / "left", case / "right"
    left.mkdir()
    right.mkdir()
    with host(exe,left/"db",left,federation=True) as provider, host(exe,right/"db",right,federation=True) as requester:
        def group(base, folder, name):
            topic = http(base,"/api/topics",{"name":name})[1]["id"]
            with sqlite3.connect(folder/"db"/"workbench.sqlite") as db:
                leader, key = agent(db,topic,folder,name+"组长")
                member, _ = agent(db,topic,folder,name+"组员")
                db.execute("UPDATE topics SET leader_window_id=? WHERE id=?",(leader,topic))
            return topic, leader, member, key
        provider_topic, lead, member, provider_key = group(provider,left,"提供组")
        requester_topic, requester_lead, _, requester_key = group(requester,right,"请求组")
        assert http(provider,f"/api/peers/windows/{member}",{"enabled":True},"PUT")[0]==403
        status,share = http(provider,f"/api/peers/windows/{lead}",{"enabled":True},"PUT")
        assert status==200 and share["link"],share
        status,capsule = http(requester,"/api/peers/remotes",{"topicId":requester_topic,"link":share["link"],"purpose":"用户群聊"})
        assert status==200,capsule
        remote = capsule["id"]
        def federation(base):
            return http(base,"/api/peers")[1]
        grant = until(lambda:federation(provider)["grants"],lambda grants:len(grants)==1)[0]
        assert http(provider,"/api/peers/grants/"+grant["id"],{"action":"approve"},"PUT")[0]==200
        until(lambda:federation(requester)["remotes"],lambda remotes:any(r["status"]=="approved" and r["connected"] for r in remotes))
        top = [
            (requester,requester_topic,"remote:"+remote,"另一端用户好",provider,provider_topic),
            (provider,provider_topic,"grant:"+grant["id"],"这里收到你的用户消息",requester,requester_topic),
        ]
        def count(folder):
            with sqlite3.connect(folder/"db"/"workbench.sqlite") as db:
                return db.execute("SELECT count(*) FROM inbox").fetchone()[0]
        before = count(left),count(right)
        for base, topic, address, text, dest, dest_topic in top:
            route = f"/api/topics/{topic}/chat"
            assert http(base,route,{"text":text,"humanPeer":address})[0]==200
            reached = until(lambda:http(dest,f"/api/topics/{dest_topic}/chat")[1],
                            lambda posts:any(p["text"]==text for p in posts))
            message = next(p for p in reached if p["text"]==text)
            assert message["kind"]=="human-in" and message["sender"].endswith(" · 用户") and not message["receipts"],message
            until(lambda:http(base,route)[1],lambda posts:any(
                p["text"]==text and any(r["delivery"]=="delivered" for r in p["receipts"]) for p in posts))
        second, second_lead, _, _ = group(requester,right,"另一个请求组")
        code,value = http(requester,"/api/peers/remotes",{"topicId":second,"link":share["link"],"purpose":"二群验证"})
        assert code==200 and value["id"]==remote,(code,value)
        route = f"/api/topics/{provider_topic}/chat"
        recipient = "grant:"+grant["id"]
        assert http(provider,route,{"text":"没有唯一目标组","humanPeer":recipient})[0]==200
        until(lambda:http(provider,route)[1],lambda posts:any(
            p["text"]=="没有唯一目标组" and p["receipts"][0]["delivery"]=="no_group" for p in posts))
        for topic,leader in [(requester_topic,requester_lead),(second,second_lead)]:
            assert http(requester,"/api/peers/remotes/"+remote+"/contact",{"topicId":topic,"agentId":leader},"PUT")[0]==204
        contacts=until(lambda:federation(provider)["contacts"],lambda rows:len(rows)==2)
        assert all(c["online"] and c["connected"] for c in contacts),contacts
        with closing(sqlite3.connect(right/"db"/"workbench.sqlite")) as db, db:
            db.execute("UPDATE windows SET online=0 WHERE id=?",(requester_lead,))
        def contact_state():
            return next(c for c in federation(provider)["contacts"] if c["name"]=="请求组组长")
        until(contact_state,lambda c:c["connected"] and not c["online"])
        with closing(sqlite3.connect(right/"db"/"workbench.sqlite")) as db, db:
            db.execute("UPDATE windows SET online=1 WHERE id=?",(requester_lead,))
        until(contact_state,lambda c:c["connected"] and c["online"])
        with closing(sqlite3.connect(left/"db"/"workbench.sqlite")) as db, db:
            db.execute("UPDATE windows SET online=0 WHERE id=?",(lead,))
        def provider_state():
            return next(r for r in federation(requester)["remotes"] if r["id"]==remote)
        until(provider_state,lambda r:r["connected"] and not r["online"])
        with closing(sqlite3.connect(left/"db"/"workbench.sqlite")) as db, db:
            db.execute("UPDATE windows SET online=1 WHERE id=?",(lead,))
        until(provider_state,lambda r:r["connected"] and r["online"])
        renewal={"topicId":requester_topic,"link":share["link"],"purpose":"续接旧胶囊","resumeRemoteId":remote}
        assert http(requester,"/api/peers/remotes",dict(renewal,topicId=provider_topic))[0] in (403,404,409)
        assert http(requester,"/api/peers/remotes",renewal)==(200,{"id":remote})
        print("PASS direct peer Agent state in both directions and scoped renewal")
        contact=next(c for c in contacts if c["name"]=="另一个请求组组长")
        assert http(provider,route,{"text":"只发到第二个组","humanPeer":recipient+":"+contact["id"]})[0]==200
        until(lambda:http(requester,f"/api/topics/{second}/chat")[1],lambda posts:any(p["text"]=="只发到第二个组" for p in posts))
        assert not any(p["text"]=="只发到第二个组" for p in http(requester,f"/api/topics/{requester_topic}/chat")[1])
        assert (count(left),count(right))==before
        print("PASS peer user messages in both directions, authenticated receipts, and no Agent mailbox activation")

        removed=next(c for c in contacts if c["name"]=="请求组组长")
        assert http(provider,f"/api/peers/windows/{lead}/contacts/{removed['id']}",method="DELETE")[0]==204
        until(lambda:federation(requester)["remotes"],lambda remotes:any(
            requester_lead in r.get("deniedAgentIds",[]) for r in remotes if r["topicId"]==requester_topic))
        denied=mcp(exe,right/"db",requester_key,"send_to_agent",target=remote,message="已撤权 Agent 不得发送")
        assert denied.get("isError"),denied
        requester_mail=count(right)
        user_route=f"/api/topics/{requester_topic}/chat"
        assert http(requester,user_route,{"text":"用户仍可点名","targets":[remote]})[0]==200
        until(lambda:http(requester,user_route)[1],lambda posts:any(
            p["text"]=="用户仍可点名" and p["receipts"][0]["delivery"]=="accepted" for p in posts))
        with sqlite3.connect(right/"db"/"workbench.sqlite") as db:
            db.execute("UPDATE topics SET leader_window_id=NULL WHERE id=?",(requester_topic,))
        unlinked, _, _, _ = group(requester,right,"没有协作关系的小组")
        assert http(requester,f"/api/topics/{unlinked}/chat",{"text":"未授权小组","targets":[remote]})[0] in (403,404)
        body="@提供组组长 请核对接口"
        if "--browser" in sys.argv:
            subprocess.run(["node",Path(__file__).with_suffix(".cjs"),requester,requester_topic,"--external-user"],check=True)
        else:
            assert http(requester,user_route,{"text":body,"targets":[remote]})[0]==200
        messages=until(lambda:http(provider,f"/api/topics/{provider_topic}/chat")[1],
                       lambda posts:any(p["text"]=="来自工作台用户："+body for p in posts))
        incoming=next(p for p in messages if p["text"]=="来自工作台用户："+body)
        assert incoming["kind"]=="incoming",incoming
        assert incoming["recipients"]==["提供组组长"],incoming
        with sqlite3.connect(left/"db"/"workbench.sqlite") as db:
            task, prompt, hint=db.execute("SELECT id,text,return_hint FROM inbox WHERE text LIKE ?",("%"+body+"%",)).fetchone()
            assert "READ_ONLY" in prompt and f"GROUP_REPLY: 如需答复，请用 send_to_agent 向 @reply-{task}" in prompt and hint is None
            db.execute("UPDATE inbox SET state='delivered' WHERE id=?",(task,))
        reply=mcp(exe,left/"db",provider_key,"send_to_agent",target="@reply-"+task,message="接口已核对",replyTo=task)
        assert not reply.get("isError"),reply
        messages=until(lambda:http(requester,user_route)[1],lambda posts:any(p["text"]=="接口已核对" for p in posts))
        assert next(p for p in messages if p["text"]=="接口已核对")["replyTo"]==next(p for p in messages if p["text"]==body)["id"]
        assert count(right)==requester_mail
        assert http(provider,"/api/peers/grants/"+grant["id"],{"action":"configure","quotaMode":"unlimited","paused":True},"PUT")[0]==200
        assert http(requester,user_route,{"text":"暂停后不可送达","targets":[remote]})[0]==200
        until(lambda:http(requester,user_route)[1],lambda posts:any(
            p["text"]=="暂停后不可送达" and p["receipts"][0]["delivery"]=="peer_paused" for p in posts))
        print("PASS approved external Agent can be addressed by a user without a leader; Agent revoke, group scope, peer pause and reply routing enforced")


def check_permanent(exe, case):
    provider, requester = case / "provider", case / "requester"
    provider.mkdir()
    requester.mkdir()
    left_project, right_project = provider/"project", requester/"project"
    left_project.mkdir()
    right_project.mkdir()
    with host(exe,provider/"db",left_project,federation=True) as left, host(exe,requester/"db",right_project,federation=True) as right:
        def group(base, folder, name):
            topic = http(base,"/api/topics",{"name":name})[1]["id"]
            with sqlite3.connect(folder/"db"/"workbench.sqlite") as db:
                leader, leader_key = agent(db,topic,folder/"project",name+"组长")
                member, key = agent(db,topic,folder/"project",name+"组员")
                excluded, excluded_key = agent(db,topic,folder/"project",name+"未选")
                db.execute("UPDATE topics SET leader_window_id=? WHERE id=?",(leader,topic))
            return topic, leader, member, key, excluded, excluded_key, leader_key

        a, a_lead, a_member, a_key, a_excluded, a_excluded_key, a_leader_key = group(left,provider,"甲组")
        b, b_lead, b_member, b_key, _, _, _ = group(right,requester,"乙组")
        unsupported, _, _, _, _, _, _ = group(right,requester,"无关小组")
        assert http(left,f"/api/peers/windows/{a_member}",{"enabled":True},"PUT")[0] == 403
        share = http(left,f"/api/peers/windows/{a_lead}",{"enabled":True},"PUT")[1]["link"]

        def policy(action="approve",limit=20,paused=False):
            return {"action":action,"permissionMode":"read_only","quotaMode":"total","quotaLimit":limit,"paused":paused,"allowUpload":True}

        def links(base):
            return http(base,"/api/peers")[1]["groups"]

        if "--browser" in sys.argv:
            subprocess.run(["node",Path(__file__).with_suffix(".cjs"),left,a,"--invite",right,b],check=True,timeout=75)
            link=links(left)[0]["id"]
        else:
            status, result = http(right,"/api/peers/groups",{"topicId":b,"link":share,"purpose":"常驻接口审阅",
                "selection":{"members":[b_member],"policy":policy()}})
            assert status==200,result
            link=result["id"]
            state=until(lambda:links(left),lambda rows:any(row["id"]==link and row["status"]=="pending" for row in rows))
            assert state[0]["status"]=="pending"
            assert not state[0]["recipients"], state
            assert http(left,"/api/peers/grants/"+link,{"action":"approve"},"PUT")[0]==404
            assert http(left,"/api/peers/groups/"+link,{"members":[a_member],"policy":policy(limit=2)},"PUT")[0]==204
        for base in (left,right):
            until(lambda:links(base),lambda rows:any(row["id"]==link and row["status"]=="approved" and row["connected"] for row in rows))
        def key(base,name):
            return next(p["key"] for p in links(base)[0]["recipients"] if p["name"]==name)
        def posts(base,topic):
            return http(base,f"/api/topics/{topic}/chat")[1]
        def wait_post(base,topic,message):
            return next(p for p in until(lambda:posts(base,topic),lambda rows:any(p["text"]==message for p in rows)) if p["text"]==message)

        left_target=key(right,"甲组组员")
        right_target=key(left,"乙组组员")
        assert not any(p["name"]=="甲组未选" for p in links(right)[0]["recipients"])
        handbook=mcp(exe,provider/"db",a_leader_key,"read_team_skill")
        assert "小组合作" in handbook["content"][0]["text"]
        assert http(right,f"/api/topics/{unsupported}/chat",{"text":"无关组越权","targets":[left_target]})[0]>=400
        with closing(sqlite3.connect(requester/"db"/"workbench.sqlite")) as db:
            own_node=db.execute("SELECT value FROM settings WHERE key='node_id'").fetchone()[0]
            token=db.execute("SELECT token FROM fed_group_link WHERE id=?",(link,)).fetchone()[0]
        with closing(sqlite3.connect(provider/"db"/"workbench.sqlite")) as db:
            files_before=db.execute("SELECT count(*) FROM fed_file").fetchone()[0]
        server=urlsplit(share)
        with closing(HTTPSConnection(server.hostname,server.port,context=ssl._create_unverified_context())) as conn:
            conn.request("POST",f"/api/federation/groups/{link}/files",b"must not land",{
                "Authorization":"Bearer "+token,"X-FF-Node-Id":own_node,"X-FF-Node-Name":"test",
                "X-FF-Group-Message":new_id(),"X-FF-Group-Recipient":a_excluded,"X-FF-File-Name":"test.txt",
                "X-FF-Upload-Key":new_id()})
            assert conn.getresponse().status==403
        with closing(sqlite3.connect(provider/"db"/"workbench.sqlite")) as db:
            assert db.execute("SELECT count(*) FROM fed_file").fetchone()[0]==files_before
        forbidden=mcp(exe,provider/"db",a_excluded_key,"send_to_agent",target="@"+right_target,message="未入选成员越权")
        assert forbidden.get("isError"),forbidden
        assert http(right,f"/api/topics/{b}/chat",{"text":"本组私聊"})[0]==200
        assert not any(p["text"]=="本组私聊" for p in posts(left,a))
        if "--browser" in sys.argv:
            assert not any(p["window"]==a_lead for p in links(right)[0]["recipients"])
            assert any(p["window"]==b_lead for p in links(left)[0]["recipients"])
            subprocess.run(["node",Path(__file__).with_suffix(".cjs"),right,b,b_member,left_target,"--permanent"],check=True,timeout=55)
            subprocess.run(["node",Path(__file__).with_suffix(".cjs"),left,a,a_member,right_target,"--permanent","--lender"],check=True,timeout=55)

        (right_project/"接口.txt").write_text("group attachment",encoding="utf8")
        status,value=http(right,f"/api/topics/{b}/chat",{"text":"跨端讨论","targets":[left_target],"files":["接口.txt"],"requestReply":True})
        assert status==200,value
        inbound=wait_post(left,a,"跨端讨论")
        received=inbound["receipts"][0]
        assert received["target"]==a_member and inbound["sender"].endswith("乙组 · "+links(left)[0]["user"])
        with urlopen(left+f"/api/topics/{a}/chat/{inbound['id']}/attachments/0") as response:
            assert response.read()==b"group attachment"
        with sqlite3.connect(provider/"db"/"workbench.sqlite") as db:
            prompt=db.execute("SELECT text FROM inbox WHERE id=?",(received["id"],)).fetchone()[0]
            assert "READ_ONLY" in prompt
            audit=db.execute("""SELECT r.permission FROM fed_request r JOIN fed_incoming i
                ON i.grant_id=r.grant_id AND i.request=r.id WHERE i.local=?""",(received["id"],)).fetchone()
            assert audit==("read_only",)
            db.execute("UPDATE inbox SET native_offer='manual' WHERE id=?",(received["id"],))
        claim=mcp(exe,provider/"db",a_key,"receive_message",messageId=received["id"])
        assert not claim.get("isError"),claim
        (left_project/"回执.md").write_text("review evidence",encoding="utf8")
        reply=mcp(exe,provider/"db",a_key,"send_to_agent",target="@reply-"+received["id"],replyTo=received["id"],
            message="已核对接口",document="回执.md")
        assert not reply.get("isError"),reply
        response=wait_post(right,b,"已核对接口")
        original=wait_post(right,b,"跨端讨论")
        assert response["replyTo"]==original["id"],response
        with urlopen(right+f"/api/topics/{b}/chat/{response['id']}/attachments/0") as download:
            assert download.read()==b"review evidence"
        until(lambda:posts(right,b),lambda rows:any(post["text"]=="跨端讨论" and
            post["receipts"][0]["work"]=="replied" for post in rows))

        reverse=mcp(exe,provider/"db",a_key,"send_to_agent",target="@"+right_target,message="提供方主动派送")
        assert not reverse.get("isError"),reverse
        assert wait_post(right,b,"提供方主动派送")["receipts"][0]["target"]==b_member
        boundary="ff-"+uuid.uuid4().hex
        body=(f"--{boundary}\r\nContent-Disposition: form-data; name=\"message\"\r\n\r\n"+
              json.dumps({"text":"第二次","targets":[left_target]})+
              f"\r\n--{boundary}\r\nContent-Disposition: form-data; name=\"files\"; filename=\"second.txt\"\r\n"+
              f"Content-Type: text/plain\r\n\r\nsecond attachment\r\n--{boundary}--\r\n").encode()
        with urlopen(Request(right+f"/api/topics/{b}/chat",data=body,
                     headers={"Content-Type":"multipart/form-data; boundary="+boundary})) as upload:
            assert upload.status==200
        assert wait_post(left,a,"第二次")["attachments"][0]["name"]=="second.txt"
        assert http(right,f"/api/topics/{b}/chat",{"text":"超额","targets":[left_target]})[0]==200
        until(lambda:posts(right,b),lambda rows:any(post["text"]=="超额" and
            post["receipts"][0]["delivery"]=="quota_exceeded" for post in rows))
        assert not any(post["text"]=="超额" for post in posts(left,a))

        with closing(sqlite3.connect(provider/"db"/"workbench.sqlite")) as db, db:
            requester_node=db.execute("SELECT peer_node FROM fed_group_link WHERE id=?",(link,)).fetchone()[0]
            fixture_file=new_id()
            db.execute("""INSERT INTO fed_file(id,owner,node,retry,name,relative,size,digest,created,bound)
                       VALUES(?,?,?,?,'占用.txt','占用.txt',?,'fixture',?,?)""",
                       (fixture_file,link,requester_node,new_id(),50*1024*1024,int(time.time()),new_id()))
        assert http(right,f"/api/topics/{b}/chat",{"text":"附件满","targets":[left_target],"files":["接口.txt"]})[0]==200
        until(lambda:posts(right,b),lambda rows:any(post["text"]=="附件满" and
            post["receipts"][0]["delivery"]=="attachment_failed" for post in rows))
        user=next(p["key"] for p in links(left)[0]["recipients"] if p["window"]=="workbench-user")
        assert http(left,f"/api/topics/{a}/chat",{"text":"存储满仍能反向发文字","targets":[user]})[0]==200
        assert not wait_post(right,b,"存储满仍能反向发文字")["receipts"]

        assert http(left,"/api/peers/groups/"+link,{"members":[a_member],"policy":policy("configure",paused=True)},"PUT")[0]==204
        until(lambda:links(right),lambda rows:rows[0]["status"]=="paused")
        assert http(right,f"/api/topics/{b}/chat",{"text":"暂停后拒绝","targets":[left_target]})[0]>=400
        assert http(left,"/api/peers/groups/"+link,{"members":[],"policy":policy("configure")},"PUT")[0]==204
        until(lambda:links(right),lambda rows:rows[0]["status"]=="approved" and
            not any(p["window"]==a_member for p in rows[0]["recipients"]))
        assert http(right,f"/api/topics/{b}/chat",{"text":"已移除成员","targets":[left_target]})[0]>=400
        with sqlite3.connect(provider/"db"/"workbench.sqlite") as db:
            db.execute("UPDATE topics SET leader_window_id=? WHERE id=?",(a_member,a))
            db.execute("UPDATE topics SET leader_window_id=? WHERE id=?",(a_lead,a))
        until(lambda:links(right),lambda rows:rows[0]["status"]=="paused")
        assert http(left,"/api/peers/groups/"+link,{"members":[a_member],"policy":policy("configure")},"PUT")[0]==204
        until(lambda:links(right),lambda rows:rows[0]["status"]=="approved")
        with sqlite3.connect(provider/"db"/"workbench.sqlite") as db:
            db.execute("UPDATE topics SET leader_window_id=? WHERE id=?",(a_member,a))
        assert http(left,"/api/peers/groups/"+link,{"members":[a_lead],"policy":policy("configure")},"PUT")[0]==204
        share=http(left,f"/api/peers/windows/{a_member}",{"enabled":True},"PUT")[1]["link"]
        # An old signaling room can expire while the provider is approving the group.
        expired=new_id()
        with sqlite3.connect(requester/"db"/"workbench.sqlite") as db:
            db.execute("INSERT INTO settings(key,value) VALUES(?,?)",("rtc_room."+expired,json.dumps({"expiresAt":"2020-01-01T00:00:00Z"})))
            db.execute("UPDATE fed_group_link SET endpoint=?,seen=0 WHERE id=?",("rtc://"+expired,link))
        stale=until(lambda:links(right)[0],lambda row:row["needsInvitation"])
        assert not stale["connected"] and all(not recipient["online"] for recipient in stale["recipients"])
        status, rebound=http(right,"/api/peers/groups",{"topicId":b,"link":share,"purpose":"续接",
            "selection":{"members":[b_member],"policy":policy()}})
        assert status==200 and rebound["id"]==link,(status,rebound)
        until(lambda:links(right),lambda rows:rows[0]["status"]=="approved" and rows[0]["remote"]["leader"]==a_member
              and rows[0]["connected"] and not rows[0]["needsInvitation"])

        status,new=http(right,"/api/peers/groups",{"topicId":unsupported,"link":share,"purpose":"取消尚未审批的申请",
            "selection":{"members":[],"policy":policy()}})
        assert status==200,new
        until(lambda:links(left),lambda rows:any(row["id"]==new["id"] and row["status"]=="pending" for row in rows))
        assert http(right,"/api/peers/groups/"+new["id"],{"members":[],"policy":policy("revoke")},"PUT")[0]==204
        until(lambda:links(left),lambda rows:any(row["id"]==new["id"] and row["status"]=="revoked" for row in rows))
        assert http(right,"/api/peers/groups/"+link,{"members":[],"policy":policy("revoke")},"PUT")[0]==204
        until(lambda:links(left),lambda rows:any(row["id"]==link and row["status"]=="revoked" for row in rows))
        assert any(post["text"]=="跨端讨论" for post in posts(left,a))
        assert any(post["text"]=="已核对接口" for post in posts(right,b))
        print("PASS permanent group mutual approval, member isolation, both directions, attachments, correlated replies, quotas, pause and revoke")


if __name__=="__main__":
    folder=Path(tempfile.gettempdir()).resolve()/"codex-task"
    folder.mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="ff-group-chat-",dir=folder) as temp:
        test = check_layout if "--layout" in sys.argv else check_permanent if "--permanent" in sys.argv else check_peers if "--peers" in sys.argv else check
        test(Path(sys.argv[1]).resolve(),Path(temp))
