"""Verify database-only signaling settings on a private workbench and TLS server."""

from contextlib import closing
import json
import os
from pathlib import Path
import shutil
import sqlite3
import subprocess
import sys
import tempfile
from unittest.mock import patch

from check_workbench import host, http, new_id
from check_webrtc_federation import signals


def verify(dll, exe, root, ui):
    key = new_id() * 2
    turn_url, turn_password = "turn:relay.example.invalid:3478?transport=tcp", new_id() * 2
    turn = {"url": turn_url, "username": "relay-user", "credential": turn_password}
    project, data = root / "project", root / "state"
    project.mkdir()
    settings_path = "/api/peers/signaling/settings"
    bridge = "FF.AgentCollaboration.WebRtc.exe" if os.name == "nt" else "FF.AgentCollaboration.WebRtc"
    bridge_override = "" if Path(exe).with_name(bridge).is_file() else sys.executable
    with signals(dll, root, key) as (origin, pin), patch.dict(
        os.environ, LOCALAPPDATA=str(root / "profile"), Signaling__BridgeExecutable=bridge_override,
        Signaling__PublicOrigin=origin, Signaling__PublicKeySha256=pin, InternalService__Key=key,
        Signaling__IceServers__0__Urls__0="stun:ignored.example.invalid:3478",
    ):
        settings = {"origin": origin, "fingerprint": pin}
        with host(exe, data, project) as url:
            initial = http(url, settings_path)[1]
            assert initial["origin"] is None and not initial["hasSigningKey"], "legacy config must not be read"
            assert initial["iceServers"] == [], "legacy ICE config must not be read"
            assert http(url, settings_path, dict(settings, fingerprint="bad", signingKey=key), "PUT")[0] == 400
            assert http(url, settings_path, dict(settings, signingKey="line\nbreak"), "PUT")[0] == 400
            if ui:
                browser = {"FF_TEST_SIGNAL_ORIGIN": origin, "FF_TEST_SIGNAL_PIN": pin, "FF_TEST_SIGNAL_KEY": key,
                           "FF_TEST_TURN_URL": turn_url, "FF_TEST_TURN_PASSWORD": turn_password}
                script = Path(__file__).with_name("check_review_dialog.cjs")
                subprocess.run(["node", str(script), url, "signaling-settings"], check=True, timeout=45,
                               env=dict(os.environ, **browser))
            else:
                console_pin = "FF_SIGNALING_SPKI_SHA256  " + pin[:32] + "\n" + pin[32:]
                status, saved = http(url, settings_path, dict(settings, fingerprint=console_pin, signingKey=key, iceServers=[turn]), "PUT")
                assert status == 200 and saved["hasSigningKey"] and "signingKey" not in saved
                assert saved["fingerprint"] == pin.upper()
            status, effective = http(url, "/api/peers/signaling")
            assert status == 200 and effective["connected"] and effective["canShare"], effective
            reread = http(url, settings_path)[1]
            assert reread["hasSigningKey"] and key not in json.dumps(reread)
            assert reread["iceServers"] == [{"url": turn_url, "username": "relay-user", "hasCredential": True}]
            assert turn_password not in json.dumps(reread)
            with closing(sqlite3.connect(data / "workbench.sqlite")) as db:
                value = db.execute("SELECT value FROM settings WHERE key='rtc_signaling_settings'").fetchone()[0]
                assert all(raw not in value for raw in (origin, pin, key, turn_url, turn_password)), "settings stored as cleartext"
            print("PASS settings are encrypted in SQLite and immediately usable by the workbench")

        with host(exe, data, project) as url:
            assert http(url, "/api/peers/signaling")[1]["canShare"], "secret lost after process restart"
            saved = http(url, settings_path, settings, "PUT")[1]
            assert saved["hasSigningKey"] and saved["iceServers"][0]["hasCredential"], "partial update erased secrets"
            stored_turn = dict(turn, credential=None)
            assert http(url, settings_path, dict(settings, iceServers=[stored_turn]), "PUT")[0] == 200
            for invalid in (dict(stored_turn, url="turn:other.example.invalid:3478"),
                            dict(stored_turn, username="different-user"), dict(turn, url="https://relay.example.invalid")):
                assert http(url, settings_path, dict(settings, iceServers=[invalid]), "PUT")[0] == 400
            assert http(url, settings_path)[1]["iceServers"] == saved["iceServers"]
            topic = http(url, "/api/topics", {"name": "Signaling", "project": str(project)})[1]["id"]
            window = new_id()
            with closing(sqlite3.connect(data / "workbench.sqlite")) as db:
                db.execute("INSERT INTO windows(id,name,engine,project,topic_id) VALUES(?, 'Signal Agent','codex',?,?)",
                           (window, str(project), topic))
                db.commit()
            share = "/api/peers/windows/" + window
            assert http(url, share, {"enabled": True}, "PUT")[1]["rtc"]
            if ui:
                subprocess.run(["node", str(script), url, "share"], check=True, timeout=45)
            status, invite = http(url, share + "/signaling-invitation", {}, "POST")
            assert status == 200 and "/signal/" in invite["link"], status
            assert not http(url, settings_path, dict(settings, fingerprint="0" * 64), "PUT")[1]["hasSigningKey"]
            assert http(url, "/api/peers/signaling")[1]["connected"] is False
            assert http(url, settings_path, dict(settings, signingKey=key), "PUT")[1]["hasSigningKey"]
            assert not http(url, settings_path, dict(settings, signingKey=""), "PUT")[1]["hasSigningKey"]
            blocked = http(url, share)[1]
            assert not blocked["rtc"] and blocked["rtcBlockReason"] == "signing_key_missing", blocked
            assert http(url, settings_path, dict(settings, iceServers=[]), "PUT")[1]["iceServers"] == []
            print("PASS restart, real invite signing, TURN credential binding and removal")

        missing_bridge = root / bridge
        with patch.dict(os.environ, Signaling__BridgeExecutable=str(missing_bridge)), host(exe, data, project) as url:
            assert http(url, settings_path, dict(settings, signingKey=key), "PUT")[0] == 200
            blocked = http(url, share)[1]
            assert not blocked["rtc"] and blocked["rtcBlockReason"] == "bridge_missing", blocked
            shutil.copyfile(sys.executable, missing_bridge)
            blocked = http(url, share)[1]
            assert not blocked["rtc"] and blocked["rtcBlockReason"] == "restart_required", blocked
            print("PASS configured signaling distinguishes missing WebRTC from a component awaiting restart")


if __name__ == "__main__":
    with tempfile.TemporaryDirectory(prefix="ff-signaling-settings-") as folder:
        verify(*(str(Path(path).resolve()) for path in sys.argv[1:3]), Path(folder), "--ui" in sys.argv[3:])
