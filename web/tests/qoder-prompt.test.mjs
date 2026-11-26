import assert from "node:assert/strict";
import test from "node:test";
import xterm from "../../src/FF.AgentCollaboration/wwwroot/vendor/xterm.js";
import { readFramedPrompt } from "../src/runtime/framedPrompt.js";

test("Qoder accepts the trimmed empty input line and blocks the native login selector", () => {
  const term = {modes:{bracketedPasteMode:true}, buffer:{active:{cursorY:1,cursorX:2,baseY:0,getLine:()=>undefined}}};
  const frame = input => ["────────", input, "────────", "? for shortcuts"];
  assert.equal(readFramedPrompt("qoder", term, frame(">")).empty, true);
  assert.equal(readFramedPrompt("qoder", term, frame("> draft")).draft, true);
  assert.equal(readFramedPrompt("qoder", term, frame("❯ Sign in")).empty, false);
});

const muted = "\x1b[38;2;136;144;159m";
async function atHome(engine, input, history = "", progress = "") {
  const term = new xterm.Terminal({allowProposedApi:true, cols:90, rows:12});
  try {
    const frame = engine === "zcode"
      ? [history, "╭─ Build ───────────────╮", "│ " + input + " │", "│                      │",
          "│ glm " + muted + "智谱 | \x1b[39mhigh │", "╰──────────────────────╯", progress || "7%"]
      : [history, "────────────────", "> " + input, "────────────────", progress || "? for shortcuts"];
    await new Promise(done => term.write("\x1b[?2004h" + frame.join("\r\n") + "\x1b[3;3H", done));
    const lines = Array.from({length:term.rows}, (_, y) => term.buffer.active.getLine(y).translateToString(true));
    return readFramedPrompt(engine, term, lines);
  } finally { term.dispose(); }
}

test("only styled placeholders are idle and safe to receive at Home in ZCode and Qoder", async () => {
  for (const [engine, hint, start] of [["zcode", "Type a prompt", muted], ["qoder", "Type your request, or /usage", "\x1b[2m"]]) {
    assert.equal((await atHome(engine, start + hint + "\x1b[0m")).empty, true);
    for (const input of [hint, start + hint.slice(0,4) + "\x1b[0m" + hint.slice(4)]) {
      const draft = await atHome(engine, input, "Working. Escape to cancel.");
      assert.equal(draft.screen, "draft");
      assert.equal(draft.draft, true);
      assert.equal(draft.empty, false);
    }
    assert.equal((await atHome(engine, "", "Generating…")).screen, "clear");
  }
  assert.equal((await atHome("zcode", "正文", "", "⠋ esc to interrupt   7%")).screen, "active");
  assert.equal((await atHome("qoder", "正文", "Generating… (esc to cancel)")).screen, "active");
});
