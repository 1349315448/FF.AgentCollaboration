"""Exercise signaling, two isolated workbenches and their actual WebRTC workers."""

from contextlib import closing, contextmanager, ExitStack
import hashlib
import json
import os
import signal
from pathlib import Path
from queue import Queue
import socket
import sqlite3
import struct
import subprocess
import sys
import tempfile
import threading
import time
from unittest.mock import patch
from urllib.parse import urlsplit
from http.client import HTTPSConnection
import ssl

from check_workbench import host, http, mcp, new_id


@contextmanager
def stun_probe():
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as listener:
        listener.bind(("127.0.0.1", 0))
        listener.settimeout(.2)
        observed = threading.Event()
        stop = threading.Event()
        cookie = 0x2112A442

        def answer():
            while not stop.is_set():
                try:
                    request, source = listener.recvfrom(2048)
                except socket.timeout:
                    continue
                if len(request) < 20 or request[:2] != b"\x00\x01" or request[4:8] != cookie.to_bytes(4, "big"):
                    continue
                address = int.from_bytes(socket.inet_aton(source[0]), "big") ^ cookie
                mapped = struct.pack("!HHBBHI", 0x0020, 8, 0, 1, source[1] ^ (cookie >> 16), address)
                listener.sendto(struct.pack("!HH", 0x0101, len(mapped)) + request[4:20] + mapped, source)
                observed.set()

        worker = threading.Thread(target=answer, daemon=True)
        worker.start()
        try:
            yield f"stun:127.0.0.1:{listener.getsockname()[1]}", observed
        finally:
            stop.set()
            worker.join(timeout=1)


@contextmanager
def signals(dll, root, key, *, block_udp=False, port=None, processes=None):
    if port is None:
        with socket.socket() as probe:
            probe.bind(("127.0.0.1", 0))
            port = probe.getsockname()[1]
    endpoint = f"https://127.0.0.1:{port}"
    env = dict(os.environ, InternalService__Key=key, Signaling__PublicOrigin=endpoint,
               Signaling__CertificatePath=str(root / "signal.pfx"),
               Signaling__PairingsPath=str(root / "data" / "signaling-pairings.json"))
    env.pop("FF_UPDATE_ROOT",None)
    env.pop("FF_UPDATE_BOOT",None)
    with ExitStack() as scope:
        if block_udp:
            blocked = scope.enter_context(socket.socket(socket.AF_INET, socket.SOCK_DGRAM))
            blocked.bind(("127.0.0.1", port))
        process = scope.enter_context(subprocess.Popen(["dotnet", dll, "--urls", endpoint], cwd=root, env=env,
                                      stdout=subprocess.PIPE, text=True, encoding="utf-8"))
        if processes is not None:
            processes.append(process)
        lines = Queue()
        def read():
            for line in process.stdout:
                lines.put(line)
            lines.put(None)
        threading.Thread(target=read, daemon=True).start()
        try:
            while True:
                line = lines.get(timeout=15)
                assert line is not None, "signaling host failed"
                if line.startswith("FF_SIGNALING_SPKI_SHA256 "):
                    yield endpoint, line.split()[1]
                    break
        finally:
            if process.poll() is None:
                process.kill()
                process.wait(timeout=10)


def until(read, matches):
    deadline = time.monotonic() + 50
    while time.monotonic() < deadline:
        value = read()
        if matches(value):
            return value
        time.sleep(.2)
    raise AssertionError(value)


def browser_check(url, mode):
    subprocess.run(["node", str(Path(__file__).with_name("check_review_dialog.cjs")), url, mode],
                   check=True, timeout=35)


def verify_presence(get, provider, requester, local, remote, p, q, window, member, capsule, proof, link):
    def set_online(data, agent, online):
        with closing(sqlite3.connect(data / "workbench.sqlite")) as db, db:
            db.execute("UPDATE windows SET online=? WHERE id=?", (online, agent))

    assert http(requester, "/api/peers/remotes/" + capsule + "/contact", {"topicId": q, "agentId": member}, "PUT")[0] == 204
    contact = lambda: next(iter(get(provider)["contacts"]), {})
    peer = lambda: next(row for row in get(requester)["remotes"] if row["id"] == capsule)
    until(contact, lambda row: row.get("online"))
    until(peer, lambda row: row["connected"] and row["online"])
    set_online(remote, member, 0)
    set_online(local, window, 0)
    until(contact, lambda row: row and not row["online"])
    until(peer, lambda row: row["connected"] and not row["online"])
    set_online(remote, member, 1)
    set_online(local, window, 1)
    until(contact, lambda row: row.get("online"))
    until(peer, lambda row: row["online"])

    workers = {int(item.name) for item in proof.iterdir()}
    assert len(workers) == 2, workers
    for pid in workers:
        os.kill(pid, signal.SIGTERM)
    until(peer, lambda row: not row["connected"])
    until(peer, lambda row: row["connected"] and row["online"])
    until(contact, lambda row: row.get("online"))
    assert {int(item.name) for item in proof.iterdir()} - workers, "WebRTC workers were not rebuilt"
    print("PASS WebRTC transport loss recovers both legacy presence directions while the ticket is valid")

    policy = {"action": "approve", "permissionMode": "read_only", "quotaMode": "unlimited", "allowUpload": True}
    selection = {"memberMode": "all", "members": [], "policy": policy}
    status, result = http(requester, "/api/peers/groups",
                          {"topicId": q, "link": link, "purpose": "同步小组状态", "selection": selection})
    assert status == 200, result
    group = result["id"]
    def roster(node):
        return next((item for item in get(node)["groups"] if item["id"] == group), {})
    until(lambda: roster(provider), lambda row: row.get("status") == "pending")
    assert http(provider, "/api/peers/groups/" + group, selection, "PUT")[0] == 204
    for base, agent in ((provider, member), (requester, window)):
        until(lambda: roster(base), lambda row: any(x["window"] == agent and x["online"] for x in row.get("recipients", [])))
    set_online(remote, member, 0)
    set_online(local, window, 0)
    for base, agent in ((provider, member), (requester, window)):
        until(lambda: roster(base), lambda row: row.get("connected") and
              any(x["window"] == agent and not x["online"] for x in row["recipients"]))
    set_online(remote, member, 1)
    set_online(local, window, 1)
    for base, agent in ((provider, member), (requester, window)):
        until(lambda: roster(base), lambda row: any(x["window"] == agent and x["online"] for x in row.get("recipients", [])))
    print("PASS permanent groups exchange both Agent states over WebRTC")

    status, invite = http(provider, "/api/peers/windows/" + window + "/signaling-invitation", {}, "POST")
    assert status == 200, invite
    renewal = {"topicId": q, "link": invite["link"], "purpose": "续接旧胶囊", "resumeRemoteId": capsule}
    assert http(requester, "/api/peers/remotes", dict(renewal, resumeRemoteId=new_id()))[0] == 409
    status, renewed = http(requester, "/api/peers/remotes", renewal)
    assert status == 200 and renewed["id"] == capsule, renewed
    until(peer, lambda row: row["connected"] and row["online"] and row["contactAgentId"] == member)
    until(contact, lambda row: row.get("online") and row.get("connected"))
    print("PASS new invitation reconnects the existing grant and preserves the return contact")
    assert http(requester,"/api/peers/groups/"+group,dict(selection,policy=dict(policy,action="revoke")),"PUT")[0]==204
    until(lambda:roster(provider),lambda row:row.get("status")=="revoked")
    until(lambda:roster(requester),lambda row:row.get("status")=="revoked")
    print("PASS group revoke reaches both workbenches before old transport is released")


def verify(dll, exe, bridge, root, ui, relay_only):
    key = new_id() * 2
    work = root / "project"
    work.mkdir()
    proof = root / "relay-workers"
    proof.mkdir()
    local, remote = root / "provider", root / "requester"
    processes = []
    with stun_probe() as (ice_url, observed), signals(dll, root, key, block_udp=relay_only, processes=processes) as (origin, pin), patch.dict(os.environ,
        LOCALAPPDATA=str(root / "profile"), Signaling__BridgeExecutable=bridge,
        FF_TEST_RELAY_ONLY="1" if relay_only else "0", FF_TEST_RELAY_PROOF=str(proof)), \
        host(exe, local, work) as provider, host(exe, remote, work) as requester:
        address = urlsplit(origin)
        if not relay_only:
            with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as udp:
                udp.settimeout(4)
                transaction = os.urandom(12)
                udp.sendto(bytes.fromhex("000100002112a442") + transaction, (address.hostname, address.port))
                reply = udp.recv(64)
                assert reply[:8] == bytes.fromhex("0101000c2112a442") and reply[8:20] == transaction
                assert int.from_bytes(reply[26:28], "big") ^ 0x2112 == udp.getsockname()[1]
                assert bytes(a ^ b for a, b in zip(reply[28:32], reply[4:8])) == socket.inet_aton("127.0.0.1")
        for url, signing_key in ((provider, key), (requester, None)):
            status, settings = http(url, "/api/peers/signaling/settings", {
                "origin": origin, "fingerprint": pin, "signingKey": signing_key,
                "iceServers": [] if relay_only else [{"url": ice_url}]}, "PUT")
            assert status == 200 and settings["hasSigningKey"] == (signing_key is not None), settings
            assert "signingKey" not in settings
        p = http(provider, "/api/topics", {"name": "Provider", "project": str(work)})[1]["id"]
        q = http(requester, "/api/topics", {"name": "Requester", "project": str(work)})[1]["id"]
        window, reviewer = new_id(), new_id()
        with closing(sqlite3.connect(local / "workbench.sqlite")) as db:
            db.execute("INSERT INTO windows(id,name,engine,project,topic_id,online,grant_hash) VALUES(?, 'RTC Agent','codex',?,?,1,?)",
                       (window, str(work), p, hashlib.sha256(reviewer.encode()).hexdigest().upper()))
            db.execute("UPDATE topics SET leader_window_id=? WHERE id=?", (window, p))
            db.commit()
        member = new_id()
        with closing(sqlite3.connect(remote / "workbench.sqlite")) as db, db:
            db.execute("INSERT INTO windows(id,name,engine,project,topic_id,online) VALUES(?, 'Remote Planner','codex',?,?,1)",
                       (member, str(work), q))
            db.execute("UPDATE topics SET leader_window_id=? WHERE id=?", (member, q))
        share = "/api/peers/windows/" + window
        status, available = http(provider, share, {"enabled": True}, "PUT")
        assert status == 200 and available["rtc"] and not available["link"], (status, available)
        if ui:
            browser_check(provider, "share")
        status, response = http(provider, share + "/signaling-invitation", {}, "POST")
        assert status == 200, status
        link = response["link"]
        assert urlsplit(link).path.startswith("/signal/") and len(urlsplit(link).fragment) == 129
        status, capsule = http(requester, "/api/peers/remotes", {"topicId": q, "link": link, "purpose": "后台传输验证"})
        assert status == 200, (status, capsule)
        get = lambda url: http(url, "/api/peers")[1]
        grant = until(lambda: get(provider)["grants"], bool)[0]
        assert grant["windowId"] == window and grant["status"] == "pending"
        if ui:
            opened = mcp(exe, local, reviewer, "show_external_collaboration_approval", applicationId=grant["id"])
            assert not opened.get("isError"), opened
            browser_check(provider, "application")
            assert get(provider)["grants"][0]["status"] == "pending"
        assert http(provider, "/api/peers/grants/" + grant["id"], {"action": "approve"}, "PUT")[0] == 200
        until(lambda: get(requester)["remotes"], lambda records: records and records[0]["connected"])
        if not relay_only:
            assert observed.is_set(), "the WebRTC workers did not use the STUN server configured in the database"
        else:
            assert len(list(proof.iterdir())) >= 2, "use the worker built with go test -c for --relay-only"
        if "--renew" in sys.argv:
            paired = root/"data"/"signaling-pairings.json"
            def bindings():
                with closing(sqlite3.connect(remote/"workbench.sqlite")) as db:
                    return any(item.get("confirmed") for value, in db.execute("SELECT value FROM settings WHERE key GLOB 'rtc_room.*'")
                               for item in (json.loads(value).get("bindings") or []))
            until(bindings,bool)
            assert paired.is_file() and len(json.loads(paired.read_text()))==1
            room_id=urlsplit(link).path.split("/")[-1]
            with closing(sqlite3.connect(remote/"workbench.sqlite")) as db:
                credential=json.loads(db.execute("SELECT value FROM settings WHERE key=?",("rtc_room."+room_id,)).fetchone()[0])
            workers={int(item.name) for item in proof.iterdir()}
            processes[0].kill()
            processes[0].wait(timeout=10)
            for pid in workers:
                os.kill(pid,signal.SIGTERM)
            until(lambda:get(requester)["remotes"],lambda rows:rows and not rows[0]["connected"])
            with signals(dll,root,key,port=urlsplit(origin).port) as (_,new_pin):
                assert new_pin==pin
                until(lambda:get(requester)["remotes"],lambda rows:rows and rows[0]["connected"] and rows[0]["online"])
                assert {int(item.name) for item in proof.iterdir()}-workers
                assert http(provider,"/api/peers/grants/"+grant["id"],{"action":"revoke"},"PUT")[0]==200
                until(lambda:json.loads(paired.read_text()),lambda rows:any(
                    key.replace("-","")==room_id and data.get("ClosedByHost") is True for key,data in rows.items()))
                with closing(HTTPSConnection("127.0.0.1",urlsplit(origin).port,context=ssl._create_unverified_context())) as probe:
                    probe.request("POST",f"/api/signaling/v1/rooms/{room_id}/renew",
                                  headers={"Authorization":"Bearer "+credential["resumeTicket"]})
                    assert probe.getresponse().status==410
                until(lambda:get(requester)["remotes"],lambda rows:rows and rows[0]["status"]=="revoked")
            print("PASS approved pairing survives signaling restart, obtains fresh tickets and revokes automatically")
            return
        if "--presence" in sys.argv:
            verify_presence(get, provider, requester, local, remote, p, q, window, member, capsule["id"], proof, link)
            return
        message = new_id()
        status, receipt = http(requester, "/api/peers/remotes/" + capsule["id"] + "/messages",
                               {"id": message, "text": "通过后台 WebRTC 收件"}, timeout=20)
        assert status == 200, (status, receipt)
        assert any(row["text"] == "通过后台 WebRTC 收件" for row in get(provider)["mail"]), "provider mailbox did not receive message"
        with closing(sqlite3.connect(local / "workbench.sqlite")) as db:
            task = db.execute("SELECT local FROM fed_incoming WHERE request=?", (message,)).fetchone()[0]
            db.execute("UPDATE inbox SET state='delivered' WHERE id=?", (task,))
            db.commit()
        reply = mcp(exe, local, reviewer, "send_to_agent", target="@reply-" + task,
                    message="通过后台 WebRTC 回信", replyTo=task, finalReply=True)
        assert not reply.get("isError"), reply
        until(lambda: get(requester)["remotes"], lambda rows: rows and rows[0]["lastReply"] == "通过后台 WebRTC 回信")
        print("PASS two workbenches pair behind loopback-only ingress, deliver and reply over background WebRTC")

        sender, permission, other, other_permission = [new_id() for _ in range(4)]
        with closing(sqlite3.connect(remote / "workbench.sqlite")) as db:
            for identity, name, secret in ((sender, "Sender", permission), (other, "Other", other_permission)):
                db.execute("INSERT INTO windows(id,name,engine,project,topic_id,online,grant_hash) VALUES(?,?,'codex',?,?,1,?)",
                           (identity, name, str(work), q, hashlib.sha256(secret.encode()).hexdigest().upper()))
            db.execute("UPDATE topics SET leader_window_id=? WHERE id=?", (sender, q))
            db.commit()
        (work / "intro.txt").write_text("checked text", encoding="utf-8")
        (work / "binary.png").write_bytes(b"\x89PNG\r\n\x1a\nexample image bytes")
        def send(secret, **args):
            result = mcp(exe, remote, secret, "send_peer_files", **args)
            assert not result.get("isError"), result
            return json.loads(result["content"][0]["text"])
        approval = send(permission, target=capsule["id"], message="附件原信", files=["intro.txt"])
        assert approval["status"] == "awaiting_user"
        assert [item["files"] for item in get(requester)["attachmentApprovals"]] == [["intro.txt"]]
        assert not any(row["text"] == "附件原信" for row in get(provider)["mail"])
        approve = lambda pending, action: http(requester, "/api/peers/attachment-approvals/" + pending["id"], {"action": action}, "POST")
        if ui:
            browser_check(requester, "attachments")
        else:
            assert approve(approval, "once")[0] == 204
        def originals():
            return [row for row in get(provider)["mail"] if row["text"] == "附件原信"]
        original = until(originals, lambda rows: len(rows) == 1 and rows[0]["attachmentCount"] == 1)[0]
        def outbox_state():
            with closing(sqlite3.connect(remote / "workbench.sqlite")) as db:
                return db.execute("SELECT state FROM fed_outbox WHERE id=?", (approval["id"],)).fetchone()[0]
        until(outbox_state, lambda state: state == "accepted")
        supplement = send(permission, attachTo=approval["id"], files=["binary.png"])
        assert supplement["status"] == "awaiting_user", supplement
        assert approve(supplement, "always")[0] == 204
        until(originals, lambda rows: len(rows) == 1 and rows[0]["attachmentCount"] == 2)
        attachments = http(provider, f"/api/peers/mail/{original['seq']}/attachments")[1]
        assert {item["name"] for item in attachments} == {"intro.txt", "binary.png"}, attachments
        assert send(permission, target=capsule["id"], message="永久授权", files=["intro.txt"])["status"] == "queued"
        with closing(sqlite3.connect(remote / "workbench.sqlite")) as db, db:
            db.execute("UPDATE topics SET leader_window_id=? WHERE id=?", (other, q))
        assert send(other_permission, target=capsule["id"], message="另一 Agent", files=["intro.txt"])["status"] == "awaiting_user"
        print("PASS one-time and per-Agent permanent approval, inline attachments and late attachment on original mailbox entry")


if __name__ == "__main__":
    with tempfile.TemporaryDirectory(prefix="ff-webrtc-federation-") as folder:
        verify(*(str(Path(item).resolve()) for item in sys.argv[1:4]), Path(folder), "--ui" in sys.argv[4:], "--relay-only" in sys.argv[4:])
