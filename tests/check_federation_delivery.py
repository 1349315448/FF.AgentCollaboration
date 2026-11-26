"""Federation documents and real MCP transport. All projects, credentials and databases are test-owned."""
from contextlib import closing
import hashlib
import json
from pathlib import Path
import sqlite3
import time
from urllib.request import urlopen
from urllib.parse import urlsplit

from check_workbench import http, mcp, new_id


def query(root,sql,*args):
    with closing(sqlite3.connect(root/"workbench.sqlite")) as db:
        result=db.execute(sql,args).fetchall()
        db.commit()
        return result


def wait_rows(root,sql,*args):
    end=time.monotonic()+25
    while time.monotonic()<end:
        result=query(root,sql,*args)
        if result: return result
        time.sleep(.2)
    raise AssertionError(sql)


def verify_documents(exe,case,provider,requester,client,local,topic,target,w,remote,grant,wire,token,share,node,policy):
    def upload(key,name,data,credential=token):
        return wire("/api/federation/uploads",credential,data,**{"X-FF-File-Name":name,"X-FF-Upload-Key":key})
    assert policy(quotaMode="unlimited")[0]==200
    assert upload(new_id(),"a.md",b"test",share)[0]==403
    assert upload(new_id(),"a.exe",b"test")[0]==415
    assert upload(new_id(),"a.md",b"bad\0file")[0]==415
    assert upload(new_id(),"a.md",b"\xff")[0]==415
    assert wire("/api/federation/uploads",token,b"",**{"Content-Length":str(1048577),"X-FF-File-Name":"a.md"})[0]==413
    assert policy(quotaMode="unlimited",allowUpload=False)[0]==200
    assert upload(new_id(),"a.md",b"test")[0]==403
    assert policy(quotaMode="unlimited")[0]==200
    key=new_id()
    status,result=upload(key,"../../CON.md",b"x"*1048576)
    assert status==200,(status,result)
    ref=result["uploadId"]
    assert upload(key,"../../CON.md",b"x"*1048576)[1]==result
    assert upload(key,"../../CON.md",b"other")[0]==409
    req=new_id()
    body={"id":req.upper(),"text":"read document","uploadId":ref.upper()}
    assert wire("/api/federation/messages",token,body)[0]==200
    assert wire("/api/federation/messages",token,dict(body,id=req,uploadId=ref))[0]==200
    assert wire("/api/federation/messages",token,dict(body,id=new_id()))[0]==409
    path,bound=query(provider,"SELECT relative,bound FROM fed_file WHERE id=?",ref)[0]
    assert result["pathHint"]=="federation-inbox/"+Path(path).as_posix(),result
    assert bound and "document.md" in path and (provider/"federation-inbox"/path).stat().st_size==1048576
    sender,wgrant,sourcegrant=new_id(),new_id()*2,new_id()*2
    def hashed(v):return hashlib.sha256(v.encode()).hexdigest().upper()
    query(requester,"INSERT INTO windows(id,name,engine,project,topic_id,auto_receive,online,grant_hash) VALUES(?,'Writer','codex',?,?,0,1,?)",
          sender,str(case),target,hashed(sourcegrant))
    query(provider,"UPDATE windows SET online=1,grant_hash=? WHERE id=?",hashed(wgrant),w)
    formal=case/"spec.md";formal.write_text("跨端交接原文",encoding="utf8")
    def tool(db,key,kind,**kwargs):
        response=mcp(exe,db,key,kind,**kwargs)
        assert not response.get("isError"),response
        return response["content"][0]["text"]
    assert any(t.get("id")==remote for t in json.loads(tool(requester,sourcegrant,"list_windows",detail=True)))
    mid=json.loads(tool(requester,sourcegrant,"send_to_agent",target="@Reviewer",message="请审阅",document=str(formal)))["id"]
    row=wait_rows(provider,"SELECT f.local,i.document_ref FROM fed_incoming f JOIN inbox i ON i.id=f.local WHERE f.request=?",mid)[0]
    assert Path(row[1]).resolve().is_relative_to((provider/"federation-inbox").resolve()) and Path(row[1]).read_bytes()==formal.read_bytes(),row
    assert tool(provider,wgrant,"read_handoff_doc",pathOrId=row[0])=="跨端交接原文"
    report=case/"project"/"result.md";report.write_text("回信正文",encoding="utf8")
    assert mcp(exe,requester,sourcegrant,"send_to_agent",target="@reply-"+row[0],message="wrong node").get("isError")
    tool(provider,wgrant,"send_to_agent",target="@reply-"+row[0],message="已审阅",document=str(report))
    received=wait_rows(requester,"SELECT id,document_ref FROM inbox WHERE target=? AND text LIKE '%已审阅%'",sender)
    assert len(received)==1 and Path(received[0][1]).resolve().is_relative_to((requester/"federation-inbox").resolve()),received
    assert tool(requester,sourcegrant,"read_handoff_doc",pathOrId=received[0][0])=="回信正文"
    # A second, independently approved collaborator cannot bind or download this grant's files.
    other_node,claim=new_id(),new_id()*2
    headers={"X-FF-Node-Id":other_node}
    share_url=http(local,"/api/peers/windows/"+w)[1]["link"]
    share_token=urlsplit(share_url).path.split("/")[-1]
    assert wire("/api/federation/applications",share_token,{"claim":claim,"purpose":"独立审阅方"},**headers)[0]==200
    other_grant=query(provider,"SELECT id FROM fed_grant WHERE node=?",other_node)[0][0]
    assert http(local,"/api/peers/grants/"+other_grant,{"action":"approve","quotaMode":"unlimited"},"PUT")[0]==200
    other_token=wire("/api/federation/application",claim,**headers)[1]["token"]
    unbound=upload(new_id(),"private.md",b"grant-bound")[1]["uploadId"]
    assert wire("/api/federation/messages",other_token,{"id":new_id(),"text":"共同审阅","uploadId":unbound},**headers)[0]==409
    reply_file=query(provider,"SELECT file_id FROM fed_reply_doc WHERE grant_id=?",grant)[0][0]
    assert wire("/api/federation/documents/"+reply_file,other_token,**headers)[0]==404
    assert query(provider,"SELECT total FROM fed_grant WHERE id=?",other_grant)==[(0,)]
    assert http(local,"/api/peers/grants/"+other_grant,{"action":"revoke"},"PUT")[0]==200
    # An open capsule can receive attachments without a local sender terminal.
    manual=new_id()
    assert http(client,"/api/peers/remotes/"+remote+"/messages",{"id":manual,"text":"人工审阅","topicId":target})[0]==200
    manual_local=query(provider,"SELECT local FROM fed_incoming WHERE request=?",manual)[0][0]
    tool(provider,wgrant,"send_to_agent",target="@reply-"+manual_local,message="人工审阅回信",document=str(report))
    def mailbox_document(summary):
        seq=wait_rows(requester,"SELECT seq FROM fed_mail WHERE topic=? AND body=? AND kind='reply-in'",target,summary)[0][0]
        with urlopen(client+"/api/peers/mail/"+str(seq)+"/document",timeout=5) as response:
            assert response.read()==report.read_bytes()
    mailbox_document("人工审阅回信")
    query(requester,"UPDATE windows SET closed=1 WHERE id=?",sender)
    tool(provider,wgrant,"send_to_agent",target="@reply-"+row[0],message="窗口关闭后的回信",document=str(report))
    mailbox_document("窗口关闭后的回信")
    assert not query(requester,"SELECT id FROM inbox WHERE text LIKE '%窗口关闭后的回信%'")
    query(requester,"UPDATE windows SET closed=0 WHERE id=?",sender)

    # A dedicated native reader only sees the claimed message. Stopping it releases the claim.
    query(provider,"INSERT OR REPLACE INTO settings VALUES(?, '1')","peer_reader."+w)
    query(provider,"UPDATE fed_incoming SET active=1 WHERE local=?",row[0])
    assert json.loads(tool(provider,wgrant,"list_windows",allTopics=True,detail=True))==[{"name":"@reply-"+row[0],"online":True}]
    assert tool(provider,wgrant,"read_handoff_doc",pathOrId=row[0])=="跨端交接原文"
    assert mcp(exe,provider,wgrant,"read_handoff_doc",pathOrId=manual_local).get("isError")
    assert mcp(exe,provider,wgrant,"send_to_agent",target="@reply-"+manual_local,message="非本次请求").get("isError")
    query(provider,"UPDATE windows SET online=0 WHERE id=?",w)
    assert query(provider,"SELECT active FROM fed_incoming WHERE local=?",row[0])==[(0,)]
    query(provider,"UPDATE windows SET online=1,grant_hash=? WHERE id=?",hashed(wgrant),w)
    assert json.loads(tool(provider,wgrant,"list_windows"))==[]
    query(provider,"DELETE FROM settings WHERE key=?","peer_reader."+w)
    orphan=upload(new_id(),"old.md",b"temporary")[1]["uploadId"]
    query(provider,"UPDATE fed_file SET created=? WHERE id=?",int(time.time())-1801,orphan)
    assert http(local,"/api/peers/storage/cleanup",{})[0]==200
    assert not query(provider,"SELECT id FROM fed_file WHERE id=?",orphan)
    assert formal.exists() and report.exists() and Path(row[1]).exists()
    query(provider,"UPDATE fed_file SET created=? WHERE id=?",int(time.time())-31*86400,ref)
    assert http(local,"/api/peers/storage/cleanup",{})[0]==200
    assert not (provider/"federation-inbox"/path).exists()
    assert formal.exists() and report.exists()
    for count,age in ((20,0),(100,65)):
        query(provider,"DELETE FROM fed_upload_rate WHERE node=?",node)
        with closing(sqlite3.connect(provider/"workbench.sqlite")) as db:
            db.executemany("INSERT INTO fed_upload_rate VALUES(?,?)",[(node,int(time.time())-age)]*count)
            db.commit()
        assert upload(new_id(),"limited.md",b"file")[0]==429
    # Both POSTs now receive a real upload-rate refusal. Only a first, definitely
    # refused attempt may be cancelled. A previously ambiguous attempt stays locked.
    for previous in (0,2):
        retry=new_id()
        query(requester,"INSERT INTO fed_outbox(id,remote,window_id,body,name,content,started,created) VALUES(?,?,?,?,?,?,?,?)",
              retry,remote,sender,"重试审阅","retry.md",b"retry",previous,int(time.time()))
        actual=wait_rows(requester,"SELECT started FROM fed_outbox WHERE id=? AND next_try>0 AND state='pending'",retry)
        assert actual==[(previous,)],actual
        reply=mcp(exe,requester,sourcegrant,"send_to_agent",cancel=retry)
        assert bool(reply.get("isError"))==(previous==2),reply
        if previous==2:
            query(requester,"UPDATE fed_outbox SET next_try=? WHERE id=?",int(time.time())+3600,retry)
    query(provider,"DELETE FROM fed_upload_rate WHERE node=?",node)
    query(provider,"UPDATE fed_file SET size=? WHERE id=?",50*1048576,unbound)
    assert upload(new_id(),"limited.md",b"file")[0]==507
    query(provider,"UPDATE fed_file SET node=?,size=? WHERE id=?",other_node,500*1048576,unbound)
    assert upload(new_id(),"limited.md",b"file")[0]==507
    query(provider,"UPDATE fed_file SET node=?,size=? WHERE id=?",node,len(b"grant-bound"),unbound)
    for needs_reply in (True,False):
        text="回执联调-"+str(needs_reply)
        rid=json.loads(tool(requester,sourcegrant,"send_to_agent",target=remote,message=text,autoReturnHint=needs_reply,supersedePrevious=False))["id"]
        original=wait_rows(provider,"SELECT local FROM fed_incoming WHERE request=?",rid)[0][0]
        query(provider,"UPDATE inbox SET state='delivered' WHERE id=?",original)
        if needs_reply:
            query(provider,"INSERT OR REPLACE INTO settings VALUES(?, '1')","peer_reader."+w)
            query(provider,"UPDATE windows SET session=? WHERE id=?",new_id(),w)
            query(provider,"UPDATE fed_incoming SET active=1 WHERE local=?",original)
        tool(provider,wgrant,"report_task_status",messageId=original[:8],status="working")
        wait_rows(requester,"SELECT seq FROM fed_received WHERE remote=? AND request_id=? AND body='work:working'",remote,rid)
        for node_url in (client,local):
            row=next(m for m in http(node_url,"/api/state")[1]["federation"]["mail"] if m["text"].startswith(text))
            assert (row["deliveryState"],row["workState"])==("delivered","working"),row
        if needs_reply:
            query(provider,"UPDATE windows SET online=0 WHERE id=?",w)
            query(provider,"UPDATE windows SET online=1,grant_hash=? WHERE id=?",hashed(wgrant),w)
            assert query(provider,"SELECT active FROM fed_incoming WHERE local=?",original)==[(2,)]
            args=dict(target="@reply-"+original,replyTo=original[:8])
            tool(provider,wgrant,"send_to_agent",**args,message="中间",finalReply=False)
            assert query(provider,"SELECT work_state FROM inbox WHERE id=?",original)==[("working",)]
            tool(provider,wgrant,"send_to_agent",**args,message="最终交付",finalReply=True)
        else:
            tool(provider,wgrant,"report_task_status",messageId=original,status="completed")
        if needs_reply: query(provider,"DELETE FROM settings WHERE key=?","peer_reader."+w)
        end="replied" if needs_reply else "completed"
        wait_rows(requester,"SELECT seq FROM fed_received WHERE remote=? AND request_id=? AND body=?",remote,rid,"work:"+end)
        for node_url in (client,local):
            row=next(m for m in http(node_url,"/api/state")[1]["federation"]["mail"] if m["text"].startswith(text))
            assert row["workState"]==end and row["deliveryState"]=="delivered",row
    print("PASS bilateral transport and collaboration receipts with intermediate, final and no-reply outcomes")
    query(provider,"UPDATE windows SET online=0,grant_hash=NULL WHERE id=?",w)
    query(requester,"UPDATE windows SET online=0,grant_hash=NULL WHERE id=?",sender)
    print("PASS authorized upload, UTF-8, size, quotas, binding, MCP round-trip with SSE Markdown, expiration")
    print("PASS late and manual mailbox attachments, dedicated reader isolation and stop, retry/cancel after a lost response")
