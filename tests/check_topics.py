"""Isolated Chromium / native ConPTY topic and delivery regression. Requires Node Playwright."""
import os
from pathlib import Path
import subprocess
import sys
import tempfile
from unittest.mock import patch

from check_workbench import host


def verify(exe, case):
    fixture = case / "terminal.exe"
    compiler = Path(os.environ["WINDIR"]) / "Microsoft.NET/Framework/v4.0.30319/csc.exe"
    subprocess.run([compiler, "/nologo", "/main:TopicTerminalFixture", "/out:" + str(fixture),
                    Path(__file__).with_name("TopicTerminalFixture.cs"), Path(__file__).with_name("WorkbenchEngineFixture.cs")], check=True)
    state = case / "state"
    for project in ("one", "two", "third"):
        (case / project).mkdir()
    knowledge = case / "knowledge"
    existing = knowledge / "development-tasks" / "existing"
    existing.mkdir(parents=True)
    (existing / "PLAN.md").write_bytes(b"original plan\r\n")
    vault = case / "zcode-test-credentials.json"
    vault.write_text('{"oauth:active_provider":"zai","oauth:zai:access_token":"test"}')
    with patch.dict(os.environ, LX_CODEX_EXE=str(fixture), LX_COPILOT_EXE=str(fixture), FF_CLAUDE_EXE=str(fixture),
                    OPENAI_API_KEY="test", CODEBUDDY_API_KEY="", CODEBUDDY_AUTH_TOKEN="", CODEBUDDY_CONFIG_DIR=str(case / "codebuddy-profile"),
                    ZCODE_CREDENTIALS_FILE=str(vault),
                    FF_QWEN_EXE=str(fixture), FF_CODEBUDDY_EXE=str(fixture), FF_ZCODE_EXE=str(fixture), FF_QODER_EXE=str(fixture),
                    FF_TRAECLI_EXE=str(fixture), CLAUDE_CONFIG_DIR=str(case / "claude"),
                    FF_TEST_WORKBENCH_EXE=str(exe), FF_TEST_WORKBENCH_STATE=str(state)):
        create_test = "--create" in sys.argv
        delivery_test = "--phase13" in sys.argv
        buddy_inbox_test = "--buddy-inbox" in sys.argv
        inspector_test = "--inspector" in sys.argv
        git_test = "--git" in sys.argv
        mcp_test = "--mcps" in sys.argv
        skills_test = "--skills" in sys.argv
        knowledge_test = "--knowledge" in sys.argv
        expanded_test = "--expanded" in sys.argv
        if expanded_test: os.environ["CODEX_HOME"]=str(case / "receipt-home")
        scoped = buddy_inbox_test or expanded_test or create_test or delivery_test or inspector_test or git_test or mcp_test or skills_test or knowledge_test
        with host(exe, state, case / "one" if scoped else None, knowledge=knowledge, initialize=scoped) as url:
            script = Path(__file__).with_name("check_knowledge_ui.cjs" if knowledge_test else "check_managed_skills_ui.cjs" if skills_test else "check_managed_mcps.cjs" if mcp_test else "check_git_panel.cjs" if git_test else
                "check_inspector.cjs" if inspector_test else "check_create.cjs" if create_test else "check_topics.cjs")
            # Individual assertions retain their deadlines. The full suite runs eight engines sequentially,
            # including 16 native restarts and five authenticated round trips between terminals.
            flags = [flag for flag in ("--expanded", "--phase13", "--buddy-inbox") if flag in sys.argv]
            subprocess.run(["node", script, url, exe, case, *flags], check=True, timeout=200 if scoped else 720)


if __name__ == "__main__":
    temp = Path(tempfile.gettempdir()) / "codex-task"
    temp.mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(dir=temp, prefix="lx-topic-ui-") as root:
        verify(Path(sys.argv[1]).resolve(), Path(root).resolve())
