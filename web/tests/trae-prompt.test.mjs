import assert from "node:assert/strict";
import test from "node:test";
import xterm from "../../src/FF.AgentCollaboration/wwwroot/vendor/xterm.js";
import { invalidateTraeDraft, readTraePrompt } from "../src/runtime/traePrompt.js";

const rule = "─".repeat(80);
const suggestion = "Find and fix a bug in @filename";
const dim = text => "\x1b[2m" + text + "\x1b[22m";
const frame = (body, status = "  ? for shortcuts") => [rule, "❯ " + body, rule, status];

async function inspect(lines, {x = 2, y = 1, paste = true} = {}) {
  const term = new xterm.Terminal({allowProposedApi:true, cols:80, rows:14});
  try {
    const content = lines.join("\r\n") + "\x1b[" + (y + 1) + ";" + (x + 1) + "H";
    await new Promise(done => term.write((paste ? "\x1b[?2004h" : "") + content, done));
    const buffer = term.buffer.active;
    const screen = Array.from({length:term.rows}, (_, i) => buffer.getLine(buffer.baseY + i).translateToString(true));
    return readTraePrompt(term, screen);
  } finally { term.dispose(); }
}

test("TRAE accepts its dim rotating suggestion and preserves identical ordinary text at Home", async () => {
  assert.equal((await inspect(frame(dim(suggestion)))).empty, true);
  assert.equal((await inspect(frame(suggestion))).draftText, suggestion);
  assert.equal((await inspect(frame(dim(suggestion) + "!"))).empty, false);
  assert.equal((await inspect(frame(dim(suggestion)), {x:6})).empty, false);
  assert.equal((await inspect(frame(dim(suggestion)), {paste:false})).empty, false);
  assert.equal((await inspect(frame(dim(suggestion), ""))).empty, false);
});

test("TRAE recognizes multiline draft across blank lines and disallows delivery during native setup or selection", async () => {
  const draft = await inspect([rule, "❯ MSG_ID: 7b22f0aa", "", "  审核尾行", rule, "  gpt-test · Context 100% left"], {y:3, x:10});
  assert.match(draft.editableText, /MSG_ID: 7b22f0aa[\s\S]*审核尾行/);
  assert.equal(draft.empty, false);
  for (const lines of [frame(dim("Input disabled until setup completes.")),
    ["Folder access", "Do you trust the contents of this directory?", "❯ 1. Yes, continue", "  enter continue  |  esc quit"],
    ["Update Model Permissions", "❯ 1. Read Only (current)", "  enter confirm  |  esc go back"],
    ["[press any key to continue]"]]) {
    assert.equal((await inspect(lines)).screen, "blocked");
  }
  assert.equal(await inspect(["› Ask Codex to do anything", "? for shortcuts"]), null);
});

test("TRAE confirms draft clearance while working but holds the next message", async () => {
  const screen = ["▍ MSG_ID: 7b22f0aa", "❖ Working… (1s • esc to interrupt)",
    "────────── MSG_ID: 7b22f0aa ─", "❯ " + dim(suggestion), rule, "  ? for shortcuts"];
  const result = await inspect(screen, {y:3});
  assert.equal(result.screen, "active");
  assert.equal(result.empty, false);
  assert.equal(result.clear, true);
  assert.equal(result.draft, false);
  assert.equal(result.busyInput, true);
});

test("TRAE returns a native queue ID only under the live queue header and never treats a user draft as busy input", async () => {
  const status = "◇ Working… (3s • esc to interrupt)";
  const heading = "  Send after tool call · esc to send now · ↑ to edit";
  const entry = "  ❯ MSG_ID: abcdef12 | FROM_AGENT: source";
  const lines = ["▍ MSG_ID: 12345678",status,heading,entry,...frame(dim(suggestion))];
  const queued = await inspect(lines, {y:5});
  assert.equal(queued.queuedId, "abcdef12");
  assert.equal(queued.busyInput, true);
  const typed = await inspect([status, ...frame("editing")], {y:2, x:9});
  assert.equal(typed.busyInput, undefined);
  assert.equal(typed.busyEditable, true);
  assert.equal(queued.busyEditable, undefined);
  assert.equal((await inspect([status, ...frame("editing")], {y:0, x:9})).busyEditable, false);
  assert.equal(typed.empty, false);
  const output = await inspect([heading, entry, "◆ tool output", ...frame(dim(suggestion))], {y:4});
  assert.equal(output.queuedId, undefined);
  assert.equal(output.busyInput, false);
  const blocked = await inspect([status, heading, entry, ...frame(dim(suggestion)), "  enter confirm  |  esc go back"], {y:4});
  assert.equal(blocked.busyInput, undefined);
  assert.equal(blocked.queuedId, undefined);
});

test("TRAE accepts a repainted plain suggestion only after its matching native user echo", async () => {
  const term = new xterm.Terminal({allowProposedApi:true, cols:80, rows:12});
  try {
    async function render(body, history = "", x = 2) {
      const screen = [history, ...frame(body)].join("\r\n");
      const row = history.split("\r\n").length + 2;
      await new Promise(done => term.write("\x1b[?2004h\x1b[2J\x1b[H" + screen + "\x1b[" + row + ";" + (x+1) + "H", done));
      const b = term.buffer.active;
      return readTraePrompt(term, Array.from({length:term.rows}, (_, i) => b.getLine(b.baseY + i).translateToString(true)));
    }
    assert.equal((await render(dim(suggestion))).empty, true);
    assert.equal((await render("MS", "", 4)).draft, true);
    assert.equal((await render("MSG_ID: 7b22f0aa | FROM_AGENT: fixture", "", 38)).draft, true);
    assert.equal((await render(suggestion, "▍ MSG_ID: abcdef00")).empty, false);
    assert.equal((await render(suggestion, "▍ MSG_ID: 7b22f0aa")).empty, true);
    invalidateTraeDraft(term);
    assert.equal((await render(suggestion, "▍ MSG_ID: 7b22f0aa")).empty, false);
    invalidateTraeDraft(term);
    assert.equal((await render("调查终端", "▍ 调查终端", 10)).draft, true);
    assert.equal((await render(suggestion, "▍ 调查终端")).empty, false);
    assert.equal((await render(suggestion, "▍ 调查终端\r\n▍ 调查终端")).empty, true);
    invalidateTraeDraft(term);
    await render("用户发起调查", "", 14);
    const active = await render(suggestion, "▍ 用户发起调查\r\n◇ Working… (0s • esc to interrupt)");
    assert.equal(active.busyInput, true);
  } finally { term.dispose(); }
});
