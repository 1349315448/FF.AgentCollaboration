import assert from "node:assert/strict";
import test from "node:test";
import xterm from "../../src/FF.AgentCollaboration/wwwroot/vendor/xterm.js";
import { isClaudePlaceholder } from "../src/runtime/claudePrompt.js";

const input = 'Try "fix lint errors"';
const idle = "  ? for shortcuts · ← for agents                  ● high · /effort";
const editing = "                                                   ● high · /effort";
const caret = char => "\x1b[7m" + char + "\x1b[27m";

async function read(value, footer, {x = 2, softCaret = true} = {}) {
  const term = new xterm.Terminal({allowProposedApi:true, cols:80, rows:8});
  try {
    const body = (softCaret ? caret(value[0]) : value[0]) + value.slice(1);
    const screen = ["────────", "❯\u00a0" + body, "────────", footer].join("\r\n");
    await new Promise(done => term.write(screen + "\x1b[2;" + (x+1) + "H", done));
    const b = term.buffer.active;
    const lines = Array.from({length:term.rows}, (_, i) => b.getLine(b.baseY + i).translateToString(true));
    return isClaudePlaceholder(term, lines, 1, 2);
  } finally { term.dispose(); }
}

test("Claude distinguishes its rotating example from an identical draft with the caret at Home", async () => {
  assert.equal(await read(input, idle), true);
  assert.equal(await read('Try "refactor <filepath>"', idle), true);
  assert.equal(await read(input, editing), false);
  assert.equal(await read(input, idle, {x:7}), false);
  assert.equal(await read(input, idle, {softCaret:false}), false);
  assert.equal(await read("Try shipping", idle), false);
});
