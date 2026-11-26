"""Isolated Git endpoint verification, with all writes inside a disposable fixture."""
import os
from pathlib import Path
import subprocess
import sys
import tempfile
from urllib.parse import urlencode

from check_workbench import host, http


def git(cwd, *args):
    env = {k: v for k, v in os.environ.items() if not k.startswith("GIT_")}
    env.update(GIT_CONFIG_NOSYSTEM="1", GIT_CONFIG_GLOBAL=os.devnull)
    return subprocess.run(["git", "-c", "user.name=Test", "-c", "user.email=test@example.invalid",
                           "-c", "core.autocrlf=false", *args], cwd=cwd, env=env, capture_output=True,
                          encoding="utf8", check=True).stdout.strip()


def verify(exe, root):
    project = root / "repo"
    project.mkdir()
    plain = root / "plain"
    plain.mkdir()
    git(project, "init", "--initial-branch=main")
    git(project, "config", "core.autocrlf", "false")
    with host(exe, root / "state") as url:
        def query(action, path=None, *, project=project, **extra):
            if path is not None:
                extra["path"] = path
            return http(url, "/api/git/" + action + "?" + urlencode({"project": str(project), **extra}))

        def apply(action, path=None, *, project=project, **kwargs):
            if action in ("push", "pull"):
                kwargs = {"expectedBranch": "main", "expectedUpstream": "origin/main"} | kwargs
            return http(url, "/api/git/" + action, dict(project=str(project), path=path, **kwargs), "POST")

        assert query("status")[0] == 403
        for folder in (plain, project):
            assert http(url, "/api/project", {"path": str(folder)}, "PUT")[0] == 200
        assert query("status", project=plain)[1]["repository"] is False
        first = query("status")
        assert first[0] == 200 and first[1]["branch"] == "main", first
        name = "新 文件.txt"
        tracked = project / name
        tracked.write_bytes(b"first\n")
        status = query("status")[1]
        assert status["changes"][0]["path"] == name, status
        untracked = query("diff", name)
        assert untracked[0] == 200 and "+first" in untracked[1]["text"], untracked
        assert apply("discard", name, expectedRevision=untracked[1]["revision"])[0] == 400
        assert apply("stage", name)[0] == 204
        assert apply("unstage", name)[0] == 204
        assert tracked.read_bytes() == b"first\n"
        assert apply("stage", name)[0] == 204
        git(project, "config", "user.name", "Fixture")
        git(project, "config", "user.email", "fixture@example.invalid")
        assert apply("commit", message="建立基线")[0] == 204
        assert query("log")[1][0]["subject"] == "建立基线"
        tracked.write_bytes(b"second\n")
        assert apply("stage", name)[0] == 204
        tracked.write_bytes(b"third\n")
        groups = query("status")[1]["changes"]
        assert [(i["group"], i["added"], i["deleted"]) for i in groups] == [("staged", 1, 1), ("unstaged", 1, 1)]
        assert "+second" in query("diff", name, staged="true")[1]["text"]
        old_diff = query("diff", name)[1]
        tracked.write_bytes(b"fourth\n")
        assert apply("discard", name, expectedRevision=old_diff["revision"])[0] == 409
        assert tracked.read_bytes() == b"fourth\n"
        assert apply("discard", name, expectedRevision=query("diff", name)[1]["revision"])[0] == 204
        assert tracked.read_bytes() == b"second\n"
        for path in ("../other", ".git/config", str(tracked), "."):
            assert apply("stage", path)[0] == 403, path
        assert apply("stage", "missing")[0] == 409
        wildcard = project / "[draft].txt"
        wildcard.write_text("literal")
        neutral = project / "d.txt"
        neutral.write_text("unchanged")
        assert apply("stage", wildcard.name)[0] == 204
        assert "d.txt" not in [i["path"] for i in query("status")[1]["changes"] if i["group"] == "staged"]
        assert apply("unstage", wildcard.name)[0] == 204
        assert apply("commit", message="second")[0] == 204
        bare = root / "origin.git"
        git(root, "init", "--bare", "--initial-branch=main", str(bare))
        git(project, "remote", "add", "origin", str(bare))
        git(project, "push", "-u", "origin", "main")
        other = root / "peer"
        git(root, "clone", str(bare), str(other))
        (other / "remote.txt").write_text("remote")
        git(other, "add", "remote.txt")
        git(other, "commit", "-m", "remote")
        git(other, "push", "origin", "main")
        git(project, "fetch")
        assert query("status")[1]["behind"] == 1
        assert apply("pull")[0] == 204
        assert (project / "remote.txt").read_text() == "remote"
        git(project, "commit", "--allow-empty", "-m", "ahead")
        assert query("status")[1]["ahead"] == 1
        assert apply("push", expectedBranch="stale")[0] == 409
        assert apply("push", expectedUpstream="origin/another")[0] == 409
        assert apply("push")[0] == 204
        assert query("status")[1]["ahead"] == 0
        git(other, "pull", "--ff-only")
        git(other, "commit", "--allow-empty", "-m", "remote only")
        git(other, "push", "origin", "main")
        git(project, "commit", "--allow-empty", "-m", "local only")
        local_head, remote_head = git(project, "rev-parse", "HEAD"), git(other, "rev-parse", "HEAD")
        git(project, "config", "remote.origin.push", "+HEAD:refs/heads/main")
        git(project, "config", "remote.origin.mirror", "true")
        assert apply("push")[0] == 409
        assert apply("pull")[0] == 409
        assert git(project, "rev-parse", "HEAD") == local_head
        assert git(bare, "rev-parse", "main") == remote_head
        git(project, "config", "filter.fixture.clean", "fixture-filter\n")
        assert query("status")[0] == 503
        git(project, "config", "--unset", "filter.fixture.clean")
        assert query("status")[0] == 200
        print("PASS Git registration and scope, status/diff/log, literal paths, unborn index, staged preservation, stale discard, push/pull and diverged protection")


if __name__ == "__main__":
    temp = Path(tempfile.gettempdir()) / "codex-task"
    temp.mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(dir=temp, prefix="ff-git-regression-") as root:
        verify(Path(sys.argv[1]).resolve(), Path(root).resolve())
