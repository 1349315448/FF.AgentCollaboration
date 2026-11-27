"""Verify human-only leader changes across HTTP, snapshots, and an open MCP session."""
from contextlib import closing
import hashlib
from http.cookiejar import CookieJar
import json
import os
import socket
import sqlite3
import subprocess
from urllib.error import HTTPError
from urllib.parse import urlencode
from urllib.request import HTTPCookieProcessor, ProxyHandler, Request, build_opener

from check_workbench import host, http, mcp, new_id


def verify_leader(exe, case):
    project = case / "leader-project"
    root = case / "leader-state"
    project.mkdir(parents=True)
    root.mkdir()
    database = root / "workbench.sqlite"
    topic, outside_topic, first, second, outside = (new_id() for _ in range(5))
    with closing(sqlite3.connect(database)) as db:
        db.execute("""CREATE TABLE topics(
            id TEXT PRIMARY KEY, name TEXT NOT NULL COLLATE NOCASE UNIQUE, project TEXT NOT NULL, goal TEXT,
            status TEXT NOT NULL DEFAULT 'active', folder TEXT, created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
            finished_at TEXT, finish_summary TEXT
        )""")
        db.execute("INSERT INTO topics(id,name,project,goal) VALUES(?,'原有话题',?,'保留目标')", (topic, str(project)))
        db.execute("INSERT INTO topics(id,name,project) VALUES(?,'另一话题',?)", (outside_topic, str(project)))
        db.commit()

    route = f"/api/topics/{topic}/leader"
    with host(exe, root, project) as url, closing(sqlite3.connect(database)) as db:
        for agent, name, current_topic in ((first, "规划", topic), (second, "执行", topic), (outside, "旁观", outside_topic)):
            grant_hash = hashlib.sha256(agent.encode()).hexdigest().upper()
            db.execute(
                "INSERT INTO windows(id,name,engine,project,grant_hash,online,topic_id) VALUES(?,?,'codex',?,?,1,?)",
                (agent, name, str(project), grant_hash, current_topic),
            )
        db.commit()

        def assert_leader(expected):
            state = http(url, "/api/state")[1]
            current = next(row for row in state["topics"] if row["id"] == topic)
            assert current["name"] == "原有话题" and current["goal"] == "保留目标"
            assert current.get("leaderWindowId") == expected
            assert {row["agent"]["id"] for row in state["windows"] if row["agent"]["isLeader"]} == (
                {expected} if expected else set()
            )

        assert_leader(None)
        assert http(url, route, {}, "PUT")[0] == 400
        assert http(url, route, {"windowId": outside}, "PUT")[0] == 400
        assert http(url, route, {"windowId": new_id()}, "PUT")[0] == 404
        assert http(url, route, {"windowId": first}, "PUT", Origin="http://wrong.test")[0] == 403
        assert_leader(None)

        with subprocess.Popen([exe, "workbench-mcp", root], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                              text=True, encoding="utf8", env=dict(os.environ, LX_P0_WINDOW_GRANT=first)) as process:
            try:
                def rpc(method, params=None):
                    process.stdin.write(json.dumps({"id": 1, "jsonrpc": "2.0", "method": method, "params": params or {}}) + "\n")
                    process.stdin.flush()
                    return json.loads(process.stdout.readline())

                initial = rpc("initialize")["result"]["instructions"]
                assert "你是本话题组长" not in initial and "read_team_skill" in initial
                def team_guide(role):
                    result = rpc("tools/call", {"name":"read_team_skill", "arguments":{}})["result"]
                    guide = (exe.parent / ("ff-team-" + role) / "SKILL.md").read_text(encoding="utf8")
                    assert not result.get("isError") and result["content"][0]["text"].splitlines() == guide.splitlines()
                team_guide("member")
                assert not any("leader" in tool["name"] for tool in rpc("tools/list")["result"]["tools"])
                denied = rpc("tools/call", {"name": "set_topic_leader", "arguments": {"windowId": first}})
                assert denied.get("error") or denied["result"]["isError"]
                assert_leader(None)

                assert http(url, route, {"windowId": first, "expectedLeaderWindowId": ""}, "PUT")[0] == 204
                assert_leader(first)
                listing = {"name": "list_windows", "arguments": {}}
                updated = rpc("tools/call", listing)["result"]["content"]
                assert "leader" not in json.loads(updated[0]["text"])[0] and "你是本话题组长" in updated[1]["text"]
                assert "read_team_skill" in updated[1]["text"]
                team_guide("leader")
                def broadcast(agent, **extra):
                    response = mcp(exe, root, agent, "send_to_agent", target="@全部", broadcast=True, message="团队通知", **extra)
                    return response
                assert broadcast(second).get("isError")
                assert broadcast(first, externalTargets=[new_id()]).get("isError")
                prior = mcp(exe, root, first, "send_to_agent", target="@执行", message="独立任务")
                assert not prior.get("isError"), prior
                sent = broadcast(first, supersedePrevious=True)
                assert not sent.get("isError"), sent
                deliveries = json.loads(sent["content"][0]["text"])["deliveries"]
                assert len(deliveries) == 1 and deliveries[0]["target"] == second
                assert db.execute("SELECT count(*) FROM inbox WHERE source=? AND target=? AND acknowledged=0 AND state='pending'",
                                  (first, second)).fetchone()[0] == 2
                invocation = json.dumps({"id": 1, "jsonrpc": "2.0", "method": "initialize"}) + "\n"
                initialized = subprocess.run(
                    [exe, "workbench-mcp", root], input=invocation, text=True, encoding="utf8", capture_output=True,
                    env=dict(os.environ, LX_P0_WINDOW_GRANT=first), check=True, timeout=15
                )
                assert "你是本话题组长" in json.loads(initialized.stdout)["result"]["instructions"]

                assert http(url, route, {"windowId": second, "expectedLeaderWindowId": ""}, "PUT")[0] == 409
                assert_leader(first)
                assert http(url, route, {"windowId": second, "expectedLeaderWindowId": first}, "PUT")[0] == 204
                assert_leader(second)
                updated = rpc("tools/call", listing)["result"]["content"]
                assert json.loads(updated[0]["text"])[0]["leader"] is True and "已卸任本话题组长" in updated[1]["text"]
                assert "read_team_skill" in updated[1]["text"]
                team_guide("member")
                assert broadcast(first).get("isError")
                next_member = mcp(exe, root, second, "read_team_skill")
                assert next_member["content"][0]["text"].splitlines() == (exe.parent / "ff-team-leader/SKILL.md").read_text(encoding="utf8").splitlines()
                assert http(url, route, {"windowId": None, "expectedLeaderWindowId": second}, "PUT")[0] == 204
                assert_leader(None)
            finally:
                process.stdin.close()

        assert http(url, route, {"windowId": second}, "PUT")[0] == 204
        assert http(url, f"/api/windows/{second}/stop", method="POST")[0] == 204
        assert_leader(second)
        assert http(url, f"/api/windows/{second}", method="DELETE")[0] == 204
        assert_leader(None)

    with socket.socket() as available:
        available.bind(("", 0))
        port = available.getsockname()[1]
    with host(exe, root, project, lan_http_port=port) as url:
        pair = http(url, "/api/lan-access/pairing", {}, "POST")[1]
        address = pair["address"]
        browser = build_opener(ProxyHandler({}), HTTPCookieProcessor(CookieJar()))
        with browser.open(Request(
            address + "/lan-login", data=urlencode({"code": pair["code"]}).encode(), headers={"Origin": address}
        ), timeout=10) as paired:
            assert paired.status == 200
        with browser.open(address + "/api/state", timeout=10) as state:
            assert state.status == 200
        try:
            browser.open(Request(address + route, data=json.dumps({"windowId": first}).encode(),
                                 headers={"Origin": address, "Content-Type": "application/json"}, method="PUT"), timeout=10)
            raise AssertionError("paired LAN browser changed topic leader")
        except HTTPError as error:
            assert error.code == 403
        assert next(row for row in http(url, "/api/topics")[1] if row["id"] == topic).get("leaderWindowId") is None
    print("PASS legacy topic migration, manual leader lifecycle, live MCP identity, and paired LAN denial")
