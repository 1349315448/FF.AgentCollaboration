import assert from "node:assert/strict";
import test from "node:test";
import xterm from "../../src/FF.AgentCollaboration/wwwroot/vendor/xterm.js";
import { readFramedPrompt } from "../src/runtime/framedPrompt.js";

const grey = text => "\x1b[38;5;243m" + text + "\x1b[39m";
const label = "Check reply delivery status";

async function inspect(body, {x = 2, y, above = "✓ Worked for 25s", engine = "codebuddy", paste = true,
    sendHint = true, footer = "hy4flash-free high · ← for agents     ⏵⏵ auto mode on (meta+m to cycle)"} = {}) {
  const term = new xterm.Terminal({allowProposedApi:true, cols:110, rows:20});
  try {
    const frame = [above, "─".repeat(110), "> " + body + (sendHint ? "    ↵ send" : ""), "─".repeat(110), footer].join("\r\n");
    const cursorRow = y ?? above.split("\r\n").length + 1;
    await new Promise(done => term.write((paste ? "\x1b[?2004h" : "") + frame + `\x1b[${cursorRow+1};${x+1}H`, done));
    const b = term.buffer.active;
    const lines = Array.from({length:term.rows}, (_,i)=>b.getLine(b.baseY+i).translateToString(true));
    return readFramedPrompt(engine, term, lines);
  } finally { term.dispose(); }
}

test("CodeBuddy palette-243 prediction with a hardware caret allows a receipt", async () => {
  assert.equal((await inspect(grey(label))).empty, true);
  assert.equal((await inspect(label, {sendHint:false})).draftText, label);
  assert.equal((await inspect(grey(label), {sendHint:false})).empty, false);
  assert.equal((await inspect("Type your request, or /usage", {sendHint:false})).empty, false);
  assert.equal((await inspect("我的草稿    ↵ send", {sendHint:false})).draftText, "我的草稿    ↵ send");
  assert.equal((await inspect("C"+grey(label.slice(1)))).empty, false);
  assert.equal((await inspect(grey(label) + "补充")).empty, false);
  assert.equal((await inspect(grey(label), {x:5})).empty, false);
  assert.equal((await inspect(grey(label), {above:"∴ Thinking..."})).empty, false);
  assert.equal((await inspect(grey(label), {above:"等待确认"})).empty, false);
  assert.equal((await inspect(grey(label), {paste:false})).empty, false);
  assert.equal((await inspect(grey(label), {engine:"qwen"})).empty, false);
});

test("CodeBuddy accepts a busy input only at the native framed editor and identifies the steer queue", async () => {
  const run = "✹ Adapting… (11s · waiting for model)";
  const tip = "  └ Tip: Start with small features\r\n    and verify changes";
  const queues = [
    " Messages to be submitted after next tool call (press esc to",
    " interrupt and send immediately)",
    " ↳ MSG_ID: b4b4b4b4 | FROM_AGENT: Relay | MSG: steer",
    " Queued follow-up inputs",
    " ↳ MSG_ID: a3a3a3a3 | FROM_AGENT: Relay | MSG: follow-up",
    " Alt+↑ edit last queued message"
  ].join("\r\n");
  const observe = (body, above, options={}) => inspect(body, {sendHint:false, above, ...options});
  const above = [run, tip, queues].join("\r\n");
  const result = await observe("", above);
  assert.equal(result.busyInput, true);
  assert.equal(result.empty, false);
  assert.equal(result.queuedId, "b4b4b4b4");
  assert.equal((await observe("my actual draft", above, {x:7})).busyEditable, true);
  assert.ok(!(await observe("my actual draft", above, {x:7})).busyInput);
  assert.ok(!(await observe("", above, {y:0})).busyInput);
  assert.ok(!(await observe("draft", above, {y:0})).busyEditable);
  assert.equal((await observe("", above, {footer:"Enter to confirm"})).queuedId, undefined);
  assert.equal((await observe("", above, {paste:false})).queuedId, undefined);
  assert.ok(!(await observe("", run + "\r\n● Model output\r\n" + queues)).busyInput);
  assert.ok(!(await observe("", "    "+run)).busyInput);
  assert.equal((await observe("", run + "\r\n ↳ MSG_ID: abcddcba | FROM_AGENT: X")).queuedId, undefined);
});
