import assert from "node:assert/strict";
import test from "node:test";
import xterm from "../../src/FF.AgentCollaboration/wwwroot/vendor/xterm.js";
import { readFramedPrompt } from "../src/runtime/framedPrompt.js";

const suggestion = "再测试一下投递 @码农GPT";
const caret = "\x1b[4m再\x1b[24m";
const secondary = "\x1b[38;2;108;112;134m";

async function read(input, above = "", below = "自动模式 (按 Tab 切换)", cursor) {
  const term = new xterm.Terminal({allowProposedApi:true, cols:90, rows:16});
  try {
    const frame = [above, "────────────────", "> " + input, "────────────────", below].join("\r\n");
    const move = cursor ? `\x1b[${(cursor.y ?? above.split("\r\n").length + 1)+1};${cursor.x+1}H` : "";
    await new Promise(done => term.write("\x1b[?2004h" + frame + move, done));
    const buffer = term.buffer.active;
    const lines = Array.from({length:term.rows}, (_, y) => buffer.getLine(y).translateToString(true));
    return readFramedPrompt("qwen", term, lines);
  } finally { term.dispose(); }
}

test("Qwen Windows follow-up is empty until accepted or edited", async () => {
  const suggested = caret + secondary + suggestion.slice(1) + "\x1b[0m";
  assert.equal((await read(suggested)).empty, true);
  const accepted = await read(caret + suggestion.slice(1));
  assert.equal(accepted.empty, false);
  assert.equal(accepted.draftText, suggestion);
  assert.equal((await read(suggested + "补充")).empty, false);
  const wrapped = caret + secondary + "测试一下投递\r\n  @码农GPT\x1b[0m";
  assert.equal((await read(wrapped)).empty, true);
  assert.equal((await read(wrapped + "补充")).empty, false);
  assert.equal((await read(secondary + suggestion + "\x1b[0m")).empty, false);
  assert.equal((await read(suggested, "⠋ Thinking (12s · esc to cancel)")).empty, false);
  assert.equal((await read(suggested, "", "按 Enter 确认")).empty, false);
  const literal = "输入您的消息或 @ 文件路径";
  assert.equal((await read(literal, "", "自动模式 (按 Tab 切换)", {x:2})).draftText, literal);
  assert.equal((await read("  " + literal, "", "自动模式 (按 Tab 切换)", {x:2})).empty, true);
});

test("Qwen distinguishes the steer footer and queued previews from terminal history", async () => {
  const status = "  ⠏ 正在思考...\r\n  (8s · esc to cancel)";
  const steer = "  Enter 追加到当前任务 · Ctrl+Q 排到下一轮 · 自动模式";
  const item = "  \x1b[2mMSG_ID: b4b4b4b4 | FROM_AGENT: Relay | MSG: steer\x1b[22m";
  const editQueue = "  Ctrl+Q 排到下一轮 · ↑ 编辑排队消息";
  const withQueue = [status,item,editQueue].join("\r\n");
  const placeholder = "  " + secondary + "输入您的消息或 @ 文件路径\x1b[0m";
  const busy = await read(placeholder, withQueue, steer, {x:2});
  assert.equal(busy.busyInput, true);
  assert.equal(busy.empty, false);
  assert.equal(busy.queuedId, "b4b4b4b4");
  assert.equal((await read("手写草稿", withQueue, steer, {x:2})).busyEditable, true);
  assert.ok(!(await read("输入您的消息或 @ 文件路径", withQueue, steer, {x:2})).busyInput);
  assert.ok(!(await read(placeholder, withQueue, steer, {x:2,y:0})).busyInput);
  assert.ok(!(await read("", status, "Enter to confirm\r\n"+steer, {x:2})).busyInput);
  assert.equal((await read("", item, steer, {x:2})).queuedId, undefined);
  assert.equal((await read("", withQueue, "自动模式 (按 Tab 切换)", {x:2})).queuedId, undefined);
  assert.equal((await read("", "  > MSG_ID: abcddcba | FROM_AGENT: history\r\n"+editQueue, steer, {x:2})).queuedId, undefined);
  assert.equal((await read("", [status,item].join("\r\n"), "  Enter to steer · Ctrl+Q to queue · ⏳ 1 queued", {x:2})).queuedId, "b4b4b4b4");
});
