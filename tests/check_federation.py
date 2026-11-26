"""Two independent workbenches, using the actual LAN TLS listeners and requester worker."""
from concurrent.futures import ThreadPoolExecutor
from contextlib import closing, ExitStack
import hashlib
from http.client import HTTPSConnection
import json
import os
import socket
from pathlib import Path
import sqlite3
import ssl
import subprocess
import sys
import tempfile
import time
from urllib.parse import parse_qs, urlsplit
from unittest.mock import patch

from check_workbench import host, http, new_id
from check_federation_delivery import verify_documents
from cloud_forwarder import forward_port


def until(read, match):
    deadline = time.monotonic() + 15
    while time.monotonic() < deadline:
        value = read()
        if match(value):
            return value
        time.sleep(.15)
    raise AssertionError(value)


def verify(exe, case):
    provider, requester = case / "provider", case / "requester"
    project=case/"project"
    project.mkdir()
    subprocess.run(["git","init","-q",project],check=True)
    cloud = "--cloud" in sys.argv
    with host(exe, provider, project) as local, host(exe, requester, case) as client, ExitStack() as forwarding:
        def identity(url, name):
            assert http(url, "/api/peers/node", {"nodeName": name}, "PUT")[0] == 200
            return http(url, "/api/peers/node")[1]
        a, b = identity(local, "代码服务器"), identity(client, "审阅端")
        assert a["nodeId"] != b["nodeId"]
        assert identity(client, "审阅端改名")["nodeId"] == b["nodeId"]
        w = new_id()
        topic = http(local, "/api/topics", {"name":"提供方"})[1]["id"]
        target = http(client, "/api/topics", {"name":"请求方"})[1]["id"]
        with closing(sqlite3.connect(provider / "workbench.sqlite")) as db:
            db.execute("INSERT INTO windows(id,name,engine,project,topic_id,auto_receive,session,session_title) VALUES(?, 'Reviewer','codex',?,?,0,?,'Reviewer')", (w,str(project),topic,new_id()))
            db.commit()
        share = "/api/peers/windows/" + w
        grant_url = "/api/peers/grants/"
        def set_share(**extra):
            return http(local, share, dict(enabled=True, **extra), "PUT")
        code, shared = set_share()
        assert code == 200, (code, shared.get("error"))
        link = shared["link"]
        parsed = urlsplit(link)
        assert parsed.port == urlsplit(local).port and parsed.hostname == a["activeIp"]
        token = parsed.path.split("/")[-1]
        fingerprint = parse_qs(parsed.query)["pin"][0]
        def wire(path, key=None, data=None, **headers):
            # Probe this fixture's bound socket directly. The actual requester separately resolves the advertised DNS name.
            conn = HTTPSConnection(a["activeIp"], parsed.port, context=ssl._create_unverified_context(), timeout=5)
            try:
                conn.connect()
                assert hashlib.sha256(conn.sock.getpeercert(True)).hexdigest() == fingerprint.lower()
                base = {"Host": parsed.netloc, "X-FF-Node-Id": b["nodeId"], "X-FF-Node-Name": "Peer"}
                if key: base["Authorization"] = "Bearer " + key
                if data is not None: base["Content-Type"] = "application/json"
                body=data if isinstance(data,bytes) else json.dumps(data) if data is not None else None
                conn.request("POST" if data is not None else "GET",path,body,base | headers)
                res = conn.getresponse()
                raw = res.read()
                return res.status, json.loads(raw) if raw and res.getheader("Content-Type","").startswith("application/json") else None
            finally:
                conn.close()
        if cloud:
            def advertise(origin):
                return http(local,"/api/peers/node",{"nodeName":a["nodeName"],"publicEndpoint":origin},"PUT")
            for bad in ("http://cloud.example", "https://127.0.0.1", "https://169.254.169.254",
                        "https://user@cloud.example", "https://cloud.example/path", "https://cloud.example/?token=x"):
                assert advertise(bad)[0] == 400
            for address, expected_port in (("203.0.113.7",parsed.port), ("203.0.113.7:8443",8443),
                                           ("https://203.0.113.7",443), ("cloud.example:443",443)):
                assert advertise(address)[0] == 200
                advertised = urlsplit(http(local,share)[1]["link"])
                assert advertised.port == expected_port
                assert advertised.hostname == ("cloud.example" if "cloud.example" in address else "203.0.113.7")
            assert advertise("")[0] == 200
            mapped = forwarding.enter_context(forward_port(a["activeIp"], parsed.port))
            hostname = socket.gethostname()
            if a["activeIp"] not in socket.gethostbyname_ex(hostname)[2]:
                hostname = a["activeIp"]
            origin = f"https://{hostname.lower()}:{mapped}"
            authority = urlsplit(origin).netloc
            assert wire("/api/federation/application",Host=authority)[0] == 403
            assert advertise(origin)[0] == 200
            assert identity(local,a["nodeName"])["publicEndpoint"] == origin
            link = http(local,share)[1]["link"]
            parsed = urlsplit(link)
            assert parsed.netloc == authority and parsed.path.endswith(token)
            assert not b.get("publicEndpoint")
            assert wire("/api/federation/application",Host="unconfigured.example")[0] == 403
        for route in ("/","/index.html","/api/state","/api/peers/node","/api/git/status","/api/workspace/tree","/api/managed-mcps","/api/managed-mcps/import","/api/managed-skills","/api/managed-skills/import","/api/managed-skills/native/item","/api/windows/"+w+"/terminal"):
            assert wire(route,token,Host="127.0.0.1:"+str(parsed.port))[0] == 403, route
            assert wire(route,token)[0] == 403, route
        for route in ("/api/managed-skills","/api/managed-skills/import","/api/managed-skills/native-cleanup/test"):
            assert wire(route,token,{})[0] == 403, route
        msg = "/api/federation/messages"
        assert wire(msg,None,{})[0] == 401
        assert wire(msg,token,{"id":new_id(),"text":"hello"})[0] == 403
        code, error = http(local, "/api/peers/remotes", {"topicId":topic,"link":link,"purpose":"本机验证"})
        assert code == 409 and "本工作台" in error["error"], (code,error)
        assert not http(local,"/api/peers")[1]["remotes"]
        remote = http(client, "/api/peers/remotes", {"topicId":target,"link":link,"purpose":"代码审阅"})[1]["id"]
        view = lambda url: http(url,"/api/peers")[1]
        g = until(lambda:view(local)["grants"],lambda ls: len(ls)==1)[0]
        assert g["status"] == "pending" and g["nodeName"] == "审阅端改名"
        approve = lambda **kw: http(local,grant_url+g["id"],dict(action="approve",quotaMode="total",quotaLimit=2,**kw),"PUT")
        assert approve(permissionMode="read_write")[0] in (400,403,409)
        assert approve()[0] == 200
        rem = lambda: next(r for r in view(client)["remotes"] if r["id"] == remote)
        until(rem,lambda r:r["status"]=="approved" and r["connected"])
        with closing(sqlite3.connect(requester / "workbench.sqlite")) as db:
            claim, credential = db.execute("SELECT claim,token FROM fed_remote WHERE id=?",(remote,)).fetchone()
        # The original POST response may have been lost just before the host approved.
        replayed=wire("/api/federation/applications",token,{"claim":claim,"purpose":"代码审阅"})[1]
        assert replayed["status"]=="approved" and replayed["token"]==credential
        assert wire(msg,credential,{"id":new_id(),"text":"wrong peer"},**{"X-FF-Node-Id":new_id()})[0] == 403
        assert wire(msg,credential,{"id":new_id(),"text":"browser"},Origin=local)[0] == 403
        assert wire(msg,credential,{"id":new_id(),"text":"x"*66000})[0] == 413
        dest = "/api/peers/remotes/"+remote+"/messages"
        def send(id, text="review"):
            return http(client,dest,{"id":id,"text":text})
        first = new_id()
        assert send(first)[0] == send(first)[0] == 200
        assert send(first,"other")[0] == 409
        with ThreadPoolExecutor(3) as pool:
            codes = list(pool.map(lambda _:send(new_id())[0],range(3)))
        assert sorted(codes) == [200,429,429], codes
        assert view(local)["grants"][0]["used"] == 2
        until(rem,lambda r:r["quota"]["remaining"] == 0)
        # Reattaching or sharing the grant across topics preserves the credential and stream cursor.
        assert http(client,"/api/peers/remotes/"+remote+"?topic="+target,method="DELETE")[0] == 204
        assert not view(client)["remotes"]
        until(lambda:view(local)["grants"][0],lambda item:not item["connected"])
        offline_reply={"id":new_id(),"requestId":first,"text":"断线时的回信"}
        assert http(local,grant_url+g["id"]+"/replies",offline_reply)[0] == 200
        assert http(local,grant_url+g["id"]+"/replies",offline_reply)[0] == 200
        assert http(client,"/api/peers/remotes",{"topicId":target,"link":link,"purpose":"审阅"})[1]["id"] == remote
        until(rem,lambda r:r["connected"] and r["lastReply"]=="断线时的回信")
        if cloud:
            assert advertise("")[0] == 200
            until(rem,lambda r:not r["connected"] and r["status"]=="approved")
            assert wire("/api/federation/application",claim)[0] == 403
            assert http(local,grant_url+g["id"]+"/replies",{"id":new_id(),"requestId":first,"text":"云端离线续收"})[0] == 200
            assert advertise(origin)[0] == 200
            until(rem,lambda r:r["connected"] and r["lastReply"]=="云端离线续收")
            print("PASS outbound cloud pairing, forwarded HTTPS port, messages and SSE cursor recovery")
        with closing(sqlite3.connect(requester / "workbench.sqlite")) as db:
            assert db.execute("SELECT count(*) FROM fed_received WHERE remote=? AND body=?",(remote,offline_reply["text"])).fetchone()[0]==1
        reply = http(local, grant_url+g["id"]+"/replies", {"id":new_id(),"requestId":first,"text":"审核完成"})
        assert reply[0] == 200, reply
        until(rem,lambda r:r["lastReply"]=="审核完成")
        with closing(sqlite3.connect(provider / "workbench.sqlite")) as db:
            assert db.execute("SELECT count(*) FROM inbox").fetchone()[0] == 2
            assert db.execute("SELECT count(*) FROM inbox WHERE text LIKE '[EXTERNAL_COLLAB_POLICY: READ_ONLY%' AND wake_requested=0").fetchone()[0] == 2
            assert db.execute("SELECT online FROM windows WHERE id=?",(w,)).fetchone()[0] == 0
        def policy(**kw):
            return http(local,grant_url+g["id"],{"action":"configure",**kw},"PUT")
        assert policy(quotaMode="unlimited",paused=True)[0] == 200
        assert send(new_id())[0] == 403
        until(rem,lambda r:r["quota"]["paused"] and r["quota"]["mode"]=="unlimited")
        assert policy(quotaMode="daily",quotaLimit=4)[0] == 200
        assert send(new_id())[0] == 200
        assert policy(quotaMode="hourly",quotaLimit=3)[0] == 200
        assert send(new_id())[0] == 429
        with closing(sqlite3.connect(provider / "workbench.sqlite")) as db:
            db.execute("UPDATE fed_grant SET hour=hour-3600,day=day-86400 WHERE id=?",(g["id"],))
            db.commit()
        assert send(new_id())[0] == 200
        assert policy(quotaMode="daily",quotaLimit=2)[0] == 200
        assert send(new_id())[0] == 200
        assert send(new_id())[0] == 429
        assert policy(quotaMode="total",quotaLimit=1,resetTotal=True)[0] == 200
        assert send(new_id())[0] == 200
        until(rem,lambda r:r["quota"]["mode"]=="total" and r["quota"]["limit"]==1 and r["quota"]["remaining"]==0)
        assert policy(permissionMode="read_write",confirmWrite=True)[0] == 403
        assert set_share(permissionMode="read_write",confirmWrite=True)[0] == 200
        assert policy(permissionMode="read_write",confirmWrite=True,quotaMode="unlimited")[0] == 200
        assert send(new_id())[1]["permissionMode"] == "read_write"
        new_link=set_share(rotate=True)[1]["link"]
        assert new_link != link
        assert wire("/api/federation/applications",token,{"claim":new_id()*2,"purpose":"过期链接"})[0] == 403
        assert send(new_id())[1]["permissionMode"] == "read_only"
        verify_documents(exe,case,provider,requester,client,local,topic,target,w,remote,g["id"],wire,credential,token,b["nodeId"],policy)
        assert http(local,grant_url+g["id"],{"action":"revoke"},"PUT")[0] == 200
        until(rem,lambda r:r["status"]=="revoked" and not r["connected"])
        assert rem().get("quota") is None
        terminal=wire("/api/federation/application",claim)[1]
        assert terminal["status"]=="revoked" and not terminal["agentName"] and not terminal["online"] and not terminal["token"]
        assert send(new_id())[0] in (403,409)
        if "--ui" in sys.argv:
            subprocess.run(["node",str(Path(__file__).with_name("check_federation_ui.cjs")),local,client,w],input=new_link,text=True,encoding="utf-8",check=True,timeout=110)
        selected=a["addresses"][-1]
        code, saved=http(local,"/api/peers/node",{"nodeName":a["nodeName"],"selectedIp":selected},"PUT")
        assert code==200 and saved["activeIp"]==a["activeIp"] and saved["selectedIp"]==selected
    with host(exe, provider, case) as reopened:
        same = http(reopened,"/api/peers/node")[1]
        assert same["nodeId"] == a["nodeId"] and same["nodeName"] == a["nodeName"] and same["activeIp"]==selected
        if cloud:
            assert same["publicEndpoint"] == origin and urlsplit(http(reopened,share)[1]["link"]).netloc == authority
        assert parse_qs(urlsplit(http(reopened,share)[1]["link"]).query)["pin"] == [fingerprint]
    with closing(sqlite3.connect(provider / "workbench.sqlite")) as db:
        db.execute("DELETE FROM settings WHERE key='node_name'")
        db.commit()
    with host(exe, provider, case) as upgraded:
        assert not http(upgraded,"/api/initialize")[1]["needsNodeName"]
        assert http(upgraded,"/api/peers/node")[1]["nodeName"] == __import__("socket").gethostname()
        assert http(upgraded,share)[1]["link"]
        assert identity(upgraded,"升级后补名")["nodeId"]==a["nodeId"]
        assert not http(upgraded,"/api/initialize")[1]["needsNodeName"]
    print("PASS dual-node LAN isolation, approval, idempotency, atomic quotas, reply SSE and durable identity")


if __name__ == "__main__":
    temp = Path(tempfile.gettempdir()) / "codex-task"
    temp.mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(dir=temp, prefix="ff-federation-") as folder:
        case=Path(folder)
        fixture=case/"terminal.exe"
        compiler=Path(os.environ["WINDIR"])/"Microsoft.NET/Framework/v4.0.30319/csc.exe"
        subprocess.run([compiler,"/nologo","/main:TopicTerminalFixture","/out:"+str(fixture),
                        Path(__file__).with_name("TopicTerminalFixture.cs"),Path(__file__).with_name("WorkbenchEngineFixture.cs")],check=True)
        with patch.dict(os.environ,LX_CODEX_EXE=str(fixture),OPENAI_API_KEY="test",FF_TEST_WORKBENCH_EXE=sys.argv[1],FF_TEST_WORKBENCH_STATE=str(case/"provider")):
            verify(sys.argv[1], case)
