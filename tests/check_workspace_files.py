"""Exercise the inspector's HTTP contract against an isolated loopback host."""
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path
import os
import subprocess
import sys
import tempfile
from urllib.parse import urlencode

from check_workbench import host, http


def verify(exe, case):
    project = case / "project"
    project.mkdir()
    nested = project / "src"
    nested.mkdir()
    target = nested / "Example.cs"
    target.write_bytes(b"\xef\xbb\xbf// old\r\n")
    (project / "dist").mkdir()
    (project / "dist" / "example.js").write_text("generated")
    (project / "raw.bin").write_bytes(b"data\x00data")
    (project / "large.txt").write_bytes(b"x" * 524289)
    external = case / "outside"
    external.mkdir()
    secret = external / "outside.txt"
    secret.write_text("untouched")
    link = project / "linked"
    if os.name == "nt":
        subprocess.run(["cmd", "/d", "/c", "mklink", "/J", str(link), str(external)], check=True, capture_output=True)
    else:
        link.symlink_to(external, target_is_directory=True)
    try:
        with host(exe, case / "data", project) as url:
            assert http(url, "/api/project", {"path": str(project)}, "PUT")[0] == 200
            def get(route, **params):
                return http(url, "/api/workspace/" + route + "?" + urlencode(dict(project=project) | params))

            def save(path, text, version):
                return http(url, "/api/workspace/file", dict(project=str(project), path=path, content=text,
                            expectedMtimeUtc=version), "PUT")

            status, tree = get("tree")
            assert status == 200, (status, tree, str(project), http(url, "/api/state")[1]["projects"])
            assert [x["name"] for x in tree["entries"]] == ["src", "large.txt", "raw.bin"]
            assert "dist" in [x["name"] for x in get("tree", showIgnored="true")[1]["entries"]]
            assert get("search", q="ample")[1]["files"] == ["src/Example.cs"]
            assert get("file", path="large.txt")[1]["kind"] == "large"
            assert get("file", path="raw.bin")[1]["kind"] == "binary"
            for path in ("../outside/outside.txt", str(secret), "linked/outside.txt", "src/../raw.bin", "raw.bin:stream"):
                assert get("file", path=path)[0] in (400, 403), path
                assert save(path, "bad", "2020-01-01T00:00:00Z")[0] in (400, 403), path
            assert get("tree", path="linked")[0] == 403
            assert get("file", project=external, path="outside.txt")[0] == 403
            version = get("file", path="src/Example.cs")[1]["mtimeUtc"]
            with ThreadPoolExecutor(max_workers=2) as pool:
                results = list(pool.map(lambda s: save("src/Example.cs", s, version)[0], ("你好\r\n", "再见\r\n")))
            assert sorted(results) == [200, 409], results
            assert target.read_bytes().startswith(b"\xef\xbb\xbf") and secret.read_text() == "untouched"
            assert save("src/Example.cs", "x" * 524289, version)[0] in (400, 413)
            fresh = get("file", path="src/Example.cs")[1]
            maximum = "中" * 174762
            assert save("src/Example.cs", maximum[:-1], fresh["mtimeUtc"])[0] == 200
            for i in range(105):
                (nested / f"match-{i}.txt").touch()
            limited = get("search", q="match-")[1]
            assert len(limited["files"]) == 100 and limited["truncated"]
            assert get("search", q="outside")[1]["files"] == []
            target.write_bytes(b"\xff\xfe" + "原文".encode("utf-16le"))
            utf16 = get("file", path="src/Example.cs")[1]
            assert utf16["content"] == "原文"
            assert save("src/Example.cs", "保留编码", utf16["mtimeUtc"])[0] == 200
            assert target.read_bytes() == b"\xff\xfe" + "保留编码".encode("utf-16le")
            version = get("file", path="src/Example.cs")[1]["mtimeUtc"]
            assert save("src/Example.cs", "a\x00b", version)[0] == 400
            target.unlink()
            assert save("src/Example.cs", "旧草稿", version)[0] == 409
            os.link(secret, target)
            hard_link = get("file", path="src/Example.cs")[1]
            assert save("src/Example.cs", "本项目", hard_link["mtimeUtc"])[0] == 200
            assert secret.read_text() == "untouched"
            media = project / "large.pdf"
            with media.open("wb") as fp:
                fp.write(b"%PDF-1.4\n")
                fp.truncate(20 * 1024 * 1024)
            assert get("file",path=media.name)[1]["kind"]=="pdf"
            from urllib.request import Request, urlopen
            raw=url+"/api/workspace/file?"+urlencode({"project":str(project),"path":media.name,"raw":"true"})
            with urlopen(Request(raw,headers={"Range":"bytes=0-7"})) as response:
                assert response.status==206 and response.read()==b"%PDF-1.4"
                assert response.headers["X-Content-Type-Options"]=="nosniff"
            with media.open("ab") as fp: fp.write(b"x")
            assert get("file",path=media.name)[1]["kind"]=="large"
            assert get("file",path=media.name,raw="true")[0]==400
            video = project / "preview.mp4"
            with video.open("wb") as fp:
                fp.write(b"\x00\x00\x00\x18ftypisom")
                fp.truncate(24 * 1024 * 1024)
            assert get("file",path=video.name)[1]["kind"]=="video"
            raw = url+"/api/workspace/file?"+urlencode({"project":str(project),"path":video.name,"raw":"true"})
            with urlopen(Request(raw,headers={"Range":"bytes=4-11"})) as response:
                assert response.status==206 and response.read()==b"ftypisom"
                assert response.headers["Content-Type"]=="video/mp4"
            with video.open("r+b") as fp: fp.truncate(100 * 1024 * 1024 + 1)
            assert get("file",path=video.name)[1]["kind"]=="large"
            assert get("file",path="linked/outside.txt",raw="true")[0] in (403,404)
            assert http(url, "/api/project?" + urlencode(dict(path=str(project))), method="DELETE")[0] == 204
            assert get("tree")[0] == 403
            print("PASS tree/search filters, bounded files, registered roots and links, concurrent saves, BOM and near-limit JSON")
    finally:
        if os.name == "nt":
            link.rmdir()
        else:
            link.unlink()


if __name__ == "__main__":
    root = Path(tempfile.gettempdir()) / "codex-task"
    root.mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="ff-workspace-", dir=root) as case:
        verify(Path(sys.argv[1]).resolve(), Path(case))
