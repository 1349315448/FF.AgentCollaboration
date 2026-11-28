"""Verify signed Linux releases, rollback and Agent restart delivery against real hosts."""
from contextlib import closing, contextmanager, ExitStack
from functools import partial
import hashlib
import base64
from http.server import SimpleHTTPRequestHandler, ThreadingHTTPServer
import json
import os
from pathlib import Path
import re
import shutil
import signal
import socket
import sqlite3
import ssl
import subprocess
import sys
import tarfile
import tempfile
import threading
import time
from uuid import uuid4

from check_workbench import http, mcp, new_id


def until(read, match, seconds=55):
    end = time.monotonic() + seconds
    while time.monotonic() < end:
        try:
            value = read()
            if match(value):
                return value
        except (OSError, KeyError, StopIteration):
            pass
        time.sleep(.25)
    raise AssertionError("Expected state did not arrive")


def local_port():
    with socket.socket() as listener:
        listener.bind(("127.0.0.1", 0))
        return listener.getsockname()[1]


def openssl(*args):
    subprocess.run(["openssl", *map(str, args)], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=True)


class QuietFiles(SimpleHTTPRequestHandler):
    def log_message(self, *args):
        pass


@contextmanager
def release_feed(root):
    directory = root / "feed"
    directory.mkdir()
    key, cert = root / "https.key", root / "https.pem"
    openssl("req", "-x509", "-newkey", "rsa:2048", "-nodes", "-keyout", key, "-out", cert, "-days", 1,
            "-subj", "/CN=localhost", "-addext", "subjectAltName=DNS:localhost")
    signing, public = root / "release.key", root / "release.pem"
    openssl("ecparam", "-name", "prime256v1", "-genkey", "-noout", "-out", signing)
    openssl("pkey", "-in", signing, "-pubout", "-out", public)
    with ThreadingHTTPServer(("127.0.0.1", 0), partial(QuietFiles, directory=str(directory))) as server:
        context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
        context.load_cert_chain(cert, key)
        server.socket = context.wrap_socket(server.socket, server_side=True)
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        try:
            yield directory, f"https://localhost:{server.server_port}", signing, public, cert
        finally:
            server.shutdown()
            thread.join()


@contextmanager
def supervisor(directory, executable, args, env):
    process = subprocess.Popen([directory / executable, *args], cwd=directory,
                               env=env, start_new_session=True)
    try:
        yield
    finally:
        if process.poll() is None:
            os.killpg(process.pid, signal.SIGINT)
            try:
                process.wait(timeout=12)
            except subprocess.TimeoutExpired:
                os.killpg(process.pid, signal.SIGKILL)
                process.wait()


def verify(root, workbench, next_workbench, signal_host, next_signal):
    work, signal_dir, project = [root / folder for folder in ("workbench", "signal", "project")]
    project.mkdir()
    shutil.copytree(workbench, work)
    shutil.copytree(signal_host, signal_dir)
    for directory in (work, signal_dir):
        for exe in directory.glob("FF.AgentCollaboration*"):
            if exe.is_file():
                exe.chmod(0o755)
    state = root / "db"
    session = str(uuid4())
    home = root / "claude"
    history = home / "projects" / re.sub("[^a-zA-Z0-9]", "-", str(project))
    history.mkdir(parents=True)
    (history / (session + ".jsonl")).write_text(json.dumps(
        {"sessionId":session,"cwd":str(project),"type":"user","message":{"content":"Original session"}})+"\n")
    auth = root / "terminal.json"
    cli = root / "claude-cli"
    cli.write_text("""#!/usr/bin/env python3
import json, os, pathlib, sys
if sys.argv[1:3] == ["auth", "status"]:
    print('{"loggedIn":true}')
elif sys.argv[1:] == ["--version"]:
    print("test-claude 1.0.0")
else:
    pathlib.Path(os.environ["TEST_TERM"]).write_text(json.dumps({"args":sys.argv[1:],"key":os.environ["LX_P0_WINDOW_GRANT"]}))
    sys.stdin.read()
""")
    cli.chmod(0o755)
    with release_feed(root) as (feed, origin, signing, public, cert), ExitStack() as scope:
        app_port, signal_port = local_port(), local_port()
        app_url, signal_url = f"http://127.0.0.1:{app_port}", f"http://127.0.0.1:{signal_port}"
        env = dict(os.environ, SSL_CERT_FILE=str(cert), XDG_DATA_HOME=str(root/"local"),
                   CLAUDE_CONFIG_DIR=str(home), FF_CLAUDE_EXE=str(cli), TEST_TERM=str(auth),
                   Updates__PublicKeyFile=str(public), Updates__ManifestUrl=origin+"/feed.json")
        assets = []
        for product, directory in (("workbench", next_workbench), ("signaling", next_signal)):
            archive = feed / f"{product}.tar.gz"
            with tarfile.open(archive,"w:gz",compresslevel=3) as tar:
                for file in sorted(directory.iterdir()):
                    tar.add(file, arcname=file.name)
            assets.append(dict(product=product,rid="linux-x64",url=origin+"/"+archive.name,file=archive.name))

        def manifest(version, entries):
            spec = feed / "release.json"
            spec.write_text(json.dumps(dict(version=version,notes="isolated update test",assets=entries)))
            target = feed / "feed.json"
            target.unlink(missing_ok=True)
            subprocess.run([sys.executable, Path(__file__).resolve().parents[1]/"scripts"/"sign_update.py",
                            spec, signing, target], check=True)

        manifest("1.0.3",assets)
        admin = new_id()*2
        scope.enter_context(supervisor(signal_dir,"FF.AgentCollaboration.Signaling",["signaling-managed","--urls",signal_url],
            dict(env,InternalService__Key=new_id()*2,Updates__AdminKeyHash=hashlib.sha256(admin.encode()).hexdigest(),
                 Signaling__PublicOrigin="https://127.0.0.1:5390")))
        scope.enter_context(supervisor(work,"FF.AgentCollaboration",["workbench-managed",str(state),"--fixed-data-dir",
            "--loopback-only","--port",str(app_port),"--project",str(project)],env))
        setup = until(lambda:http(app_url,"/api/initialize")[1],lambda value:"initialized" in value)
        assert http(app_url,"/api/initialize",dict(setup,nodeName="Update fixture"),"POST")[0]==204
        topic = http(app_url,"/api/topics",{"name":"Update fixture","project":str(project)})[1]["id"]
        window = new_id()
        with closing(sqlite3.connect(state/"workbench.sqlite")) as db, db:
            db.execute("INSERT INTO windows(id,name,engine,project,topic_id,session,auto_start,auto_receive) VALUES(?,?,'claude',?,?,?,1,0)",
                       (window,"Fixture",str(project),topic,session))
        assert http(app_url,f"/api/windows/{window}/start",method="POST")[0]==204
        before=until(lambda:json.loads(auth.read_text()),bool)
        assert before["args"][:2]==["--resume",session],before["args"][:2]
        release=until(lambda:http(app_url,"/api/updates")[1]["release"],lambda value:value["available"]=="1.0.3")
        assert release["managed"] and release["configured"]
        signed=feed/"feed.json"
        original=signed.read_text()
        forged=json.loads(original)
        forged["signature"]=base64.b64encode(b"invalid signature").decode()
        signed.write_text(json.dumps(forged))
        assert http(app_url,"/api/updates")[1]["release"]["progress"]["phase"]=="check_failed"
        signed.write_text(original)
        executable=work/"FF.AgentCollaboration"
        note=mcp(executable,state,before["key"],"resume_after_restart",message="Continue the original session",notify_port=app_port)
        assert not note.get("isError"),note
        ticket=json.loads(note["content"][0]["text"])["id"]
        def inbox():
            with closing(sqlite3.connect(state/"workbench.sqlite")) as db:
                return db.execute("SELECT text FROM inbox WHERE id=?",(ticket,)).fetchall()
        assert inbox()==[]
        blocked=mcp(executable,state,before["key"],"workbench_update",action="apply",version="1.0.3",mode="now",notify_port=app_port)
        assert blocked.get("isError"),blocked
        def open_page():
            code,page=http(app_url,"/api/page",method="POST",**{"X-FF-Page":"open"})
            assert code==200
            return {"X-FF-View":page}
        update_headers=open_page()
        assert http(app_url,"/api/updates",dict(action="restart",mode="now"))[0]==403
        assert http(app_url,f"/api/windows/{window}/config",{"name":"Fixture","allowPlatformUpdate":True},"PUT")[0]==403
        assert http(app_url,f"/api/windows/{window}/config",{"name":"Fixture","allowPlatformUpdate":True},"PUT",**update_headers)[0]==204
        assert http(app_url,f"/api/windows/{window}/config",{"name":"Fixture","allowPlatformUpdate":False},"PUT",**update_headers)[0]==204
        revoked=mcp(executable,state,before["key"],"workbench_update",action="restart",mode="now",notify_port=app_port)
        assert revoked.get("isError"),revoked
        assert http(app_url,f"/api/windows/{window}/config",{"name":"Fixture","allowPlatformUpdate":True},"PUT",**update_headers)[0]==204
        applied=mcp(executable,state,before["key"],"workbench_update",action="apply",version="1.0.3",mode="now",notify_port=app_port)
        assert not applied.get("isError"),applied
        until(lambda:http(app_url,"/api/updates/progress")[1]["release"],lambda data:
              data["current"]=="1.0.3" and data["last"] and data["last"]["status"]=="applied")
        after=until(lambda:json.loads(auth.read_text()),lambda value:value["key"]!=before["key"])
        assert after["args"][:2]==["--resume",session]
        until(inbox,lambda rows:rows==[("Continue the original session",)])
        assert http(app_url,"/api/messages/"+ticket)[1]["kind"]=="resume_after_restart"
        print("PASS verified update through MCP, opt-in permission, original session, delayed single delivery",flush=True)

        remote = until(lambda:http(signal_url,"/api/signaling/v1/updates",Authorization="Bearer "+admin)[1],
                       lambda value:value["available"]=="1.0.3")
        assert remote["managed"]
        request={"action":"apply","version":"1.0.3"}
        assert http(signal_url,"/api/signaling/v1/updates",request)[0]==401
        assert http(signal_url,"/api/signaling/v1/updates",request,Authorization="Bearer "+admin)[0]==202
        until(lambda:http(signal_url,"/api/signaling/v1/updates?progress=true")[1],lambda data:
              data["current"]=="1.0.3" and data["last"] and data["last"]["status"]=="applied")
        print("PASS signed signaling upgrade and separate administrator authentication",flush=True)

        broken=feed/"broken.tar.gz"
        fake=feed/"not-runnable"
        fake.write_text("Broken executable")
        with tarfile.open(broken,"w:gz") as tar:
            tar.add(fake, arcname="FF.AgentCollaboration")
            tar.add(next_workbench/"FF.AgentCollaboration.runtimeconfig.json",arcname="FF.AgentCollaboration.runtimeconfig.json")
            tar.add(next_workbench/"wwwroot"/"index.html",arcname="wwwroot/index.html")
        manifest("1.0.4",[dict(product="workbench",rid="linux-x64",url=origin+"/"+broken.name,file=broken.name)])
        held=mcp(executable,state,after["key"],"resume_after_restart",message="Do not release after rollback",notify_port=app_port)
        assert not held.get("isError"),held
        held_id=json.loads(held["content"][0]["text"])["id"]
        update_headers=open_page()
        assert http(app_url,"/api/updates",dict(action="apply",version="1.0.4",mode="now",tasks=[held_id]),**update_headers)[0]==202
        until(lambda:http(app_url,"/api/updates/progress")[1],lambda data:
              data["release"]["current"]=="1.0.3" and data["release"]["last"] and data["release"]["last"]["status"]=="failed")
        assert http(app_url,"/api/messages/"+held_id)[0]==404
        assert any(task["id"]==held_id and task["assigned"] for task in http(app_url,"/api/updates/progress")[1]["tasks"])
        assert len(inbox())==1
        update_headers=open_page()
        assert http(app_url,"/api/updates",dict(action="restart",mode="idle"),**update_headers)[0]==202
        until(lambda:http(app_url,"/api/updates/progress")[1]["release"],lambda row:row["progress"]["phase"]=="waiting")
        assert http(app_url,"/api/updates/cancel",method="POST",**update_headers)[0]==204
        until(lambda:http(app_url,"/api/updates/progress")[1]["release"],lambda row:row["progress"]["phase"]=="cancelled")
        print("PASS failed activation rolls back without releasing the held task",flush=True)


if __name__=="__main__":
    with tempfile.TemporaryDirectory(prefix="ff-managed-updates-") as directory:
        verify(Path(directory), *(Path(arg).resolve() for arg in sys.argv[1:5]))
