"""Verify the local Skills library in a private data directory and CLI home."""
import base64
import json
import os
from pathlib import Path
import shutil
import stat
import subprocess
import sys
import tempfile
import threading
import queue
import sqlite3
from urllib.parse import urlencode
from unittest.mock import patch
from check_workbench import host, http


def verify_native_codex(binary, profile, project, expected):
    env=dict(os.environ, USERPROFILE=str(profile), CODEX_HOME=str(profile/".codex"))
    with subprocess.Popen([binary,"app-server","--listen","stdio://"],cwd=project,env=env,stdin=subprocess.PIPE,
                          stdout=subprocess.PIPE,stderr=subprocess.DEVNULL,encoding="utf8",text=True) as proc:
        messages=queue.Queue()
        def pump():
            for line in proc.stdout:
                messages.put(json.loads(line))
        threading.Thread(target=pump,daemon=True).start()
        def send(method,params,i=None):
            request={"jsonrpc":"2.0","method":method,"params":params}
            if i is not None: request["id"]=i
            proc.stdin.write(json.dumps(request)+"\n")
            proc.stdin.flush()
            if i is None: return
            while True:
                response=messages.get(timeout=15)
                if response.get("id")==i:
                    assert "result" in response, method
                    return response["result"]
        try:
            send("initialize",{"clientInfo":{"name":"isolated-skill-check","version":"1"},"capabilities":{"experimentalApi":True}},1)
            send("initialized",{})
            result=send("skills/list",{"cwds":[str(project)],"forceReload":True},2)
            assert any(s.get("name")==expected for group in result["data"] for s in group["skills"])
            print("PASS installed Codex discovered published skill via app-server skills/list")
        finally:
            proc.terminate()
            proc.wait(timeout=8)


def verify(exe, case):
    project, state, home = case/"work", case/"state", case/"cli-user"
    project.mkdir()
    api = "/api/managed-skills"
    name = "review-notes"
    markdown = "---\nname: review-notes\ndescription: Review notes\n---\n# Notes\n"
    document = {"content": markdown, "scope": "project", "projectPath": str(project), "engines": ["qoder"], "enabled": True}
    dest = project/".qoder"/"skills"/name
    library = state/"managed-skills"
    payload = lambda b: base64.b64encode(b).decode()
    # No environment overrides point to real CLI profiles during these checks.
    with patch.dict(os.environ, QODER_CONFIG_DIR=str(home/".qoder"), QWEN_HOME=str(home/".qwen"),
                    CODEBUDDY_CONFIG_DIR=str(home/".codebuddy"), COPILOT_HOME=str(home/".copilot"), TRAE_HOME=str(home/".trae")):
        with host(exe, state, project) as base:
            def call(path="", body=None, method=None, expected=200):
                status, reply = http(base, api+path, body, method)
                assert status == expected, (method, path, status, reply)
                return reply
            assert call()["managed"] == []
            all_item=call("",dict(document,scope="global",engines=[],content=markdown.replace(name,"all-engines")),"POST")
            assert not all_item["issues"], all_item["issues"]
            paths=[home/e/"skills"/"all-engines" for e in (".codex",".copilot",".claude",".codebuddy",".trae",".qwen",".qoder")]
            paths.append(state/"engine-profiles"/"skill-roots"/"zcode"/"skills"/"all-engines")
            assert all((p/".ff-managed").is_file() for p in paths)
            if os.environ.get("FF_TEST_NATIVE_CODEX"):
                verify_native_codex(os.environ["FF_TEST_NATIVE_CODEX"],home,project,"all-engines")
            assert len({json.loads((p/".ff-managed").read_bytes())["Owner"] for p in paths})==8
            s=all_item["skill"]
            call("/"+s["id"]+"?"+urlencode({"revision":s["revision"]}),method="DELETE")
            assert not any(p.exists() for p in paths)
            print("PASS eight engine publication roots, per-target ownership, and removal")
            invalid = dict(document, files=[{"path":"../outside.md","data":payload(b"blocked")}])
            call("/import",invalid,"POST",400)
            call("/import",dict(document,files=[{"path":"SKILL.md","data":payload(markdown.encode())},
                {"path":"ref/A","data":""},{"path":"REF/b","data":""}]),"POST",400)
            item = call("/import",dict(document,files=[{"path":"SKILL.md","data":payload(markdown.encode())},
                {"path":"references/notes.txt","data":payload(b"support")}]),"POST")["skill"]
            identifier=item["id"]
            assert (library/identifier/"SKILL.md").read_text("utf8")==markdown
            assert (dest/"references"/"notes.txt").read_bytes()==b"support"
            assert (dest/".ff-managed").is_file()
            # A partial private stage or backup from a killed host can be reclaimed using its own receipt.
            owner=json.loads((dest/".ff-managed").read_bytes())["Owner"]
            stage=dest.parent/(".ff-new-"+owner+"-"+name)
            stage.mkdir()
            shutil.copyfile(dest/".ff-managed",stage/".ff-managed")
            (stage/".ff-writing").write_bytes(b"incomplete")
            backup=dest.parent/(".ff-old-"+owner+"-"+name)
            shutil.copytree(dest,backup)
            (backup/"SKILL.md").unlink()
            assert not call("/"+identifier+"/sync",{},"POST")["issues"]
            assert not stage.exists() and not backup.exists()
            assert not (project/".qwen"/"skills"/name).exists()
            assert name in call("/"+identifier)["content"]
            call("",document,"POST",409)
            item2=call("/"+identifier,dict(document,revision=item["revision"],content=markdown+"updated\n"),"PUT")["skill"]
            assert (dest/"SKILL.md").read_text("utf8").endswith("updated\n")
            call("/"+identifier,dict(document,revision=item["revision"]),"PUT",409)
            stopped=call("/"+identifier,dict(document,revision=item2["revision"],content=markdown,enabled=False),"PUT")["skill"]
            assert not dest.exists() and (library/identifier/"references"/"notes.txt").exists()
            dest.mkdir()
            (dest/"SKILL.md").write_text(markdown,encoding="utf8")
            kept=call("/"+identifier,dict(document,revision=stopped["revision"]),"PUT")
            assert kept["issues"] and not (dest/".ff-managed").exists()
            call("/"+identifier+"?"+urlencode({"revision":kept["skill"]["revision"]}),method="DELETE")
            assert (dest/"SKILL.md").read_text("utf8")==markdown and not (library/identifier).exists()
            native=next(n for n in call("?"+urlencode({"project":str(project)}))["native"] if n["name"]==name)
            query="?"+urlencode({"project":str(project)})
            view=call("/native/"+native["id"]+query)
            assert view["content"].splitlines()==markdown.splitlines()
            call("/native/"+native["id"]+query,{"confirmName":"different","revision":view["revision"]},"DELETE",400)
            (dest/"notes").write_text("concurrent update")
            call("/native/"+native["id"]+query,{"confirmName":name,"revision":view["revision"]},"DELETE",409)
            view=call("/native/"+native["id"]+query)
            assert dest.exists()
            call("/native/"+native["id"]+query,{"confirmName":name,"revision":view["revision"]},"DELETE",204)
            assert not dest.exists()
            protected=dest.parent/"native-readonly"
            protected.mkdir()
            (protected/"SKILL.md").write_text(markdown,encoding="utf8")
            (protected/"locked.txt").write_bytes(b"read-only")
            (protected/"locked.txt").chmod(stat.S_IREAD)
            n=next(n for n in call(query)["native"] if n["name"]==protected.name)
            version=call("/native/"+n["id"]+query)["revision"]
            call("/native/"+n["id"]+query,{"confirmName":n["name"],"revision":version},"DELETE",409)
            ticket=next(r for r in call()["nativeCleanup"] if r["path"]==str(protected))
            held=Path(ticket["currentPath"])
            assert held!=protected and held.exists()
            (held/"locked.txt").chmod(stat.S_IWRITE|stat.S_IREAD)
            protected.mkdir()
            (protected/"SKILL.md").write_text("replacement",encoding="utf8")
            call("/native-cleanup/"+ticket["id"],{},"POST",204)
            assert not held.exists() and (protected/"SKILL.md").read_text()=="replacement"
            # A directory link at the target or under a published skill never authorizes touching its destination.
            outside=case/"outside"
            outside.mkdir()
            (outside/"SKILL.md").write_text(markdown,encoding="utf8")
            def junction(target):
                subprocess.run(["cmd","/d","/c","mklink","/J",str(target),str(outside)],check=True,capture_output=True)
            junction(dest)
            linked=call("",document,"POST")
            assert linked["issues"] and not (outside/".ff-managed").exists()
            dest.rmdir()
            call("/"+linked["skill"]["id"]+"/sync",{},"POST")
            junction(dest/"linked")
            result=call("/"+linked["skill"]["id"]+"?"+urlencode({"revision":linked["skill"]["revision"]}),method="DELETE")
            assert result["issues"] and (outside/"SKILL.md").exists()
            (dest/"linked").rmdir()
            old_home=home/".qoder"
            global_skill=call("",dict(document,scope="global",engines=["qoder"],content=markdown.replace("review-notes","moving-home")),"POST")["skill"]
            assert (old_home/"skills"/"moving-home"/"SKILL.md").exists()
            offline=call("",dict(document,content=markdown.replace("review-notes","offline-check")),"POST")["skill"]
            parked=case/"project-unavailable"
            project.rename(parked)
            reply=call("/"+offline["id"]+"?"+urlencode({"revision":offline["revision"]}),method="DELETE")
            assert reply["issues"] and any(s["id"]==offline["id"] for s in call()["pendingCleanup"])
            parked.rename(project)
            assert not call("/"+offline["id"]+"/sync",{},"POST")["issues"]
            assert not (dest.parent/"offline-check").exists()
            edited=call("",dict(document,content=markdown.replace(name,"edited-copy")),"POST")["skill"]
            user_file=dest.parent/"edited-copy"/"SKILL.md"
            user_file.write_text("manual changes",encoding="utf8")
            kept=call("/"+edited["id"]+"?"+urlencode({"revision":edited["revision"]}),method="DELETE")
            assert kept["issues"] and user_file.read_text("utf8")=="manual changes"
            migrated=call("/import",dict(document,enabled=False,files=[{"path":"SKILL.md","data":payload(markdown.replace(name,"portable-package").encode())},
                {"path":"assets/picture.bin","data":payload(bytes(range(256)))}]),"POST")["skill"]
            print("PASS local CRUD, import, project/engine scope, markers, supplemental files, native protection, exact delete and link guards")
        new_home=case/"moved-home"
        with patch.dict(os.environ,QODER_CONFIG_DIR=str(new_home)),host(exe,state,project) as base:
            assert http(base,api)[0]==200
            assert (outside/"SKILL.md").read_text("utf8")==markdown
            assert not (old_home/"skills"/"moving-home").exists()
            assert (new_home/"skills"/"moving-home"/"SKILL.md").exists()
            assert http(base,api+"/"+global_skill["id"]+"?"+urlencode({"revision":global_skill["revision"]}),method="DELETE")[0]==200
            assert not (new_home/"skills"/"moving-home").exists()
            print("PASS restart recovery, interrupted staging, moved CLI home and offline project cleanup")
        moved=case/"migration"/"state"
        moved.mkdir(parents=True)
        with sqlite3.connect(state/"workbench.sqlite") as before, sqlite3.connect(moved/"workbench.sqlite") as after:
            before.backup(after)
        with host(exe,moved,project) as new_base:
            assert http(new_base,api+"/"+migrated["id"])[0]==200
            assert (moved/"managed-skills"/migrated["id"]/"assets"/"picture.bin").read_bytes()==bytes(range(256))
            assert (library/migrated["id"]/"SKILL.md").is_file()
            print("PASS SQLite backup rehydrates complete library in a new DataRoot and preserves the old copy")


if __name__=="__main__":
    parent=Path(tempfile.gettempdir())/"codex-task"
    parent.mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(dir=parent,prefix="ff-skills-") as d:
        verify(Path(sys.argv[1]).resolve(),Path(d).resolve())
