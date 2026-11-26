import assert from "node:assert/strict";
import test from "node:test";
import xterm from "../../src/FF.AgentCollaboration/wwwroot/vendor/xterm.js";
import { isCopilotPlaceholder } from "../src/runtime/copilotPrompt.js";

const hint = "Type @ to mention files, # for issues/PRs, / for commands, or ? for shortcuts";
const dim = text => "\x1b[2m" + text + "\x1b[22m";

async function read(body, cursorX = 2, thick = false) {
  const terminal = new xterm.Terminal({allowProposedApi:true, cols:100, rows:8});
  try {
    const lines = [thick ? "╻▄▄▄▄" : "────────", (thick ? "┃ " : "❯ ") + body,
      thick ? "╹▀▀▀▀" : "────────", "Interactive · Manual Approval · @ files/sessions · # issues"];
    await new Promise(done => terminal.write(lines.join("\r\n") + "\x1b[2;" + (cursorX+1) + "H", done));
    const b = terminal.buffer.active;
    const visible = lines.map((_, i) => b.getLine(b.baseY + i).translateToString(true));
    return isCopilotPlaceholder(terminal, visible, 1, 2);
  } finally { terminal.dispose(); }
}

test("Copilot preserves ordinary English drafts at Home, including the literal help text", async () => {
  for (const draft of ["Ask me before deploying", "Describe what changed", "Type the release note", hint])
    assert.equal(await read(draft), false);
  assert.equal(await read(dim(hint)), true);
  assert.equal(await read(dim(hint), 2, true), true);
  assert.equal(await read(dim(hint), 7), false);
  assert.equal(await read(dim(hint.slice(0,-1)) + hint.at(-1)), false);
});
