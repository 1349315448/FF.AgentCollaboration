"""Exercise signaling, two isolated workbenches and their actual WebRTC workers."""

from contextlib import closing, contextmanager, ExitStack
import hashlib
import json
import os
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
def signals(dll, root, key, *, block_udp=False):
    with socket.socket() as probe:
        probe.bind(("127.0.0.1", 0))
        port = probe.getsockname()[1]
    endpoint = f"https://127.0.0.1:{port}"
    env = dict(os.environ, InternalService__Key=key, Signaling__PublicOrigin=endpoint,
               Signaling__CertificatePath=str(root / "signal.pfx"))
    with ExitStack() as scope:
        if block_udp:
            blocked = scope.enter_context(socket.socket(socket.AF_INET, socket.SOCK_DGRAM))
            blocked.bind(("127.0.0.1", port))
        process = scope.enter_context(subprocess.Popen(["dotnet", dll, "--urls", endpoint], cwd=root, env=env,
                                      stdout=subprocess.PIPE, text=True, encoding="utf-8"))
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


def verify(dll, exe, bridge, root, ui, relay_only):
    key = new_id() * 2
    work = root / "project"
    work.mkdir()
    proof = root / "relay-workers"
    proof.mkdir()
    local, remote = root / "provider", root / "requester"
    with stun_probe() as (ice_url, observed), signals(dll, root, key, block_udp=relay_only) as (origin, pin), patch.dict(os.environ,
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
            db.commit()
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
        assert send(other_permission, target=capsule["id"], message="另一 Agent", files=["intro.txt"])["status"] == "awaiting_user"
        print("PASS one-time and per-Agent permanent approval, inline attachments and late attachment on original mailbox entry")


if __name__ == "__main__":
    with tempfile.TemporaryDirectory(prefix="ff-webrtc-federation-") as folder:
        verify(*(str(Path(item).resolve()) for item in sys.argv[1:4]), Path(folder), "--ui" in sys.argv[4:], "--relay-only" in sys.argv[4:])
