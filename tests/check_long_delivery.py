"""Live Windows smoke test. Requires a signed-in CLI, Node Playwright and Chromium.

Run with a Release executable and a trusted project folder. The native test turn asks
only for a unique reply. Pass copilot as the last argument to check manual delivery.
Set CHROME_PATH when using an installed Chromium browser.
"""
from contextlib import closing
import hashlib
import json
from pathlib import Path
import sqlite3
import subprocess
import sys
import tempfile

from check_workbench import host, http, mcp, new_id


# Keep the real workbench page connected so its own xterm processes every ConPTY frame.
BROWSER = """
const { chromium } = require('playwright');
(async () => {
  const [url, reply, topic, engine] = process.argv.slice(1);
  const browser = await chromium.launch({ executablePath: process.env.CHROME_PATH || undefined });
  try {
    const page = await browser.newPage({viewport: {width: 1460, height: 940}});
    await page.goto(url);
    await page.waitForFunction(() => !!snapshot);
    const response = await page.request.post(url + '/api/windows', {
      data: {name: 'LongInput', engine, newSession: true, topic}
    });
    if (!response.ok()) throw Error(await response.text());
    const {id} = await response.json();
    await page.waitForFunction(id => panes.get(id)?.view.inputReady, id, {timeout: 45000});
    await page.reload();
    await page.waitForFunction(id => panes.get(id)?.view.inputReady && !panes.get(id).replaying, id, {timeout: 45000});
    if (engine === 'codex') {
      await page.evaluate(id => panes.get(id).term.focus(), id);
      await page.waitForFunction(id=>panes.get(id).view.activity==='idle',id);
      await page.keyboard.type('/model',{delay:100});
      await page.waitForFunction(id=>observePrompt(panes.get(id)).draftText?.includes('/model'),id);
      await page.waitForTimeout(900);
      await page.keyboard.press('Enter');
      try {
        await page.waitForFunction(id=>panes.get(id).view.activity==='waiting',id,{timeout:12000});
      } catch (error) {
        console.error(await page.evaluate(id=>{
          const p=panes.get(id), b=p.term.buffer.active;
          return [p.view.activity,observePrompt(p),Array.from({length:p.term.rows},(_,i)=>b.getLine(b.baseY+i)?.translateToString(true)).filter(Boolean).slice(-14)];
        },id));
        throw error;
      }
      await page.keyboard.press('Escape');
      await page.waitForFunction(id=>panes.get(id).view.inputReady,id);
      await page.keyboard.type('p3-busy');
      await page.waitForFunction(id => observePrompt(panes.get(id)).editableText?.includes('p3-busy') && panes.get(id).view.activity==='working' && !panes.get(id).view.inputReady, id);
    }
    console.log(id);
    await page.waitForFunction(id => panes.get(id).view.inbox.some(item => item.state === 'pending'), id);
    if (engine === 'codex') {
      if (!await page.evaluate(id => observePrompt(panes.get(id)).editableText?.includes('p3-busy'), id))
        throw Error('The queued message overwrote the native draft');
      await page.keyboard.press('Control+u');
    } else {
      await page.locator('[data-window="' + id + '"] .write-inbox').click();
    }
    try { await page.waitForFunction(({id, reply}) => {
      const term = panes.get(id)?.term;
      if (!term) return false;
      const buffer = term.buffer.active;
      return Array.from({length: buffer.length}, (_, i) =>
        buffer.getLine(i)?.translateToString(true) || '').join('').includes(reply);
    }, {id, reply}, {timeout: 90000}); }
    catch (error) {
      console.error(await page.evaluate(id => {
        const p=panes.get(id), b=p.term.buffer.active;
        return {state:p.view.inbox.map(i=>i.state), prompt:observePrompt(p).empty,
          rows:Array.from({length:p.term.rows},(_,i)=>b.getLine(b.baseY+i)?.translateToString(true)).filter(Boolean).slice(-12)};
      },id));
      throw error;
    }
    await page.waitForFunction(id => !panes.get(id)?.view.inbox.length && panes.get(id).element.querySelector('.inbox').hidden, id);
    if (engine === 'codex') {
      await page.waitForFunction(id=>panes.get(id).view.inputReady,id);
      await page.evaluate(id=>panes.get(id).term.focus(),id);
      await page.keyboard.type('/exit',{delay:100});
      await page.waitForTimeout(900);
      await page.keyboard.press('Enter');
      await page.waitForFunction(id=>panes.get(id).view.activity==='offline',id,{timeout:20000});
    }
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
"""


def verify(executable, project, root, engine):
    grant, source = new_id(), new_id()
    first, last = "LX" + new_id()[:6], new_id()[:8]
    message = f"只回复开头校验码 {first} 拼接上文末校验码的结果，中间不要空格。无需调用工具。\n\n" + "春风拂过山谷，" * 71
    hint = "这里是用于验证长文粘贴完整的测试内容。" * 47 + "文末校验码 " + last
    payload = f"MSG_ID: 00000000 | FROM_AGENT: QA | MSG: {message} | RETURN_HINT: {hint}"
    assert len(payload) >= 1200 and len(message.encode()) <= 2048 and len(hint) <= 1000
    with host(executable, root, project) as base:
        topic = http(base, "/api/topics", {"name": "长消息", "project": str(project)})[1]["id"]
        with closing(sqlite3.connect(root / "workbench.sqlite")) as db:
            db.execute("INSERT INTO windows(id,name,engine,project,grant_hash,online,topic_id) VALUES(?,'QA','codex',?,?,1,?)",
                       (source, str(project), hashlib.sha256(grant.encode()).hexdigest().upper(), topic))
            db.commit()
        with subprocess.Popen(["node", "-e", BROWSER, base, first + last, topic, engine], stdout=subprocess.PIPE, text=True) as browser:
            window_id = browser.stdout.readline().strip()
            assert len(window_id) == 32, "Chromium could not open the isolated " + engine + " pane"
            route = "/api/windows/" + window_id
            try:
                if engine == "codex":
                    assert http(base, route + "/auto-receive", {"enabled": True}, "PUT")[0] == 204
                result = mcp(executable, root, grant, "send_to_agent", target=window_id,
                             message=message, autoReturnHint=hint)
                assert not result.get("isError"), result
                receipt = json.loads(result["content"][0]["text"])
                assert receipt["document"] is None  # The long text itself must reach the CLI.
                assert browser.wait(timeout=95) == 0, "No matching native assistant reply appeared"
                with closing(sqlite3.connect(root / "workbench.sqlite")) as db:
                    state = db.execute("SELECT state FROM inbox WHERE id=?", (receipt["id"],)).fetchone()
                    assert state == ("delivered",), state
            finally:
                if browser.poll() is None:
                    browser.terminate()
                http(base, route, method="DELETE")
    method = "automatic after clearing a busy draft" if engine == "codex" else "manual inbox button"
    print(f"PASS: {engine} after page reload, {method}, {len(payload)} characters ({len(payload.encode())} UTF-8 bytes), native reply with both markers")


if __name__ == "__main__":
    temp_base = Path(tempfile.gettempdir()) / "codex-task"
    temp_base.mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="lx-long-delivery-", dir=temp_base) as work:
        engine = sys.argv[3] if len(sys.argv) > 3 else "codex"
        assert engine in ("codex", "copilot")
        verify(Path(sys.argv[1]).resolve(), Path(sys.argv[2]).resolve(), Path(work).resolve(), engine)
