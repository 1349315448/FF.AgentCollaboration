import test from "node:test";
import assert from "node:assert/strict";
import { codeViewport, terminalTiles, workspacePreferences } from "../src/runtime/workspaceModes.js";
import { foldedCodexChars } from "../src/runtime/codexPaste.js";

test("legacy topics start in the tiled layout, with independent bounded widths", () => {
  assert.deepEqual(workspacePreferences(null), { workspaceMode:"agent", agentInspectorOpen:false,
    agentInspectorRatio:.30, codeAgentRatio:.35, codeAgentHidden:false, codeActiveWindowId:null });
  const saved = workspacePreferences({workspaceMode:"code", agentInspectorRatio:1, codeAgentRatio:-1, codeAgentHidden:true});
  assert.equal(saved.agentInspectorRatio,.45);
  assert.equal(saved.codeAgentRatio,.24);
  assert.deepEqual(workspacePreferences(JSON.parse(JSON.stringify(saved))),saved);
});
test("selecting a code viewport leaves the original tiled layout intact", () => {
  const tiles = {primaryWindowId:"b", activeWindowId:"a", collapsedIds:["c"]};
  const saved = structuredClone(tiles), ids = ["a","b","c"];
  assert.deepEqual(codeViewport(ids,tiles), {active:"b",folded:["a","c"]});
  assert.deepEqual(codeViewport(ids,{...tiles,codeActiveWindowId:"c"}), {active:"c",folded:["a","b"]});
  assert.deepEqual(codeViewport(["a"],{...tiles,codeActiveWindowId:"removed"}), {active:"a",folded:[]});
  assert.deepEqual(tiles,saved);
});
test("terminal tiles fit the region and keep the selected window reachable", () => {
  const ids = ["1","2","3","4","5","6","7","8","9","10"];
  assert.deepEqual(terminalTiles(ids, 1000, 750), { columns:3, visible:ids.slice(0,9) });
  assert.deepEqual(terminalTiles(ids.slice(0,4), 1000, 750), { columns:2, visible:ids.slice(0,4) });
  assert.deepEqual(terminalTiles(ids.slice(0,5), 340, 420, "5"), { columns:1, visible:["1","5"] });
  assert.deepEqual(terminalTiles(ids.slice(0,1), 520, 420), { columns:1, visible:["1"] });
  assert.deepEqual(terminalTiles(ids.slice(0,2), 520, 420), { columns:1, visible:["1","2"] });
  assert.deepEqual(terminalTiles(ids.slice(0,2), 1000, 220), { columns:2, visible:["1","2"] });
  assert.deepEqual(terminalTiles(ids.slice(0,3), 520, 420), { columns:2, visible:["1","2","3"] });
  assert.deepEqual(terminalTiles(ids.slice(0,3), 1000, 220, "3"), { columns:2, visible:["1","3"] });
});
test("only a claimed Codex message can own a single folded editor block", () => {
  const parse = (value, state="writing") => foldedCodexChars("codex",{state},value);
  assert.equal(parse(" [Pasted Content 1400 chars] "),1400);
  assert.equal(parse("[PastedContent1400chars]"),1400);
  assert.equal(parse("[Pasted Content 1400 chars]","delivery_uncertain"),1400);
  assert.equal(parse("[Pasted Content 1400 chars]","pending"),undefined);
  assert.equal(parse("[Pasted Content 1400 chars] #2"),0);
  assert.equal(parse("人工草稿 [Pasted Content 1400 chars]"),0);
  assert.equal(parse("[Pasted Content 1400 chars] [Pasted Content 1250 chars]"),0);
  assert.equal(foldedCodexChars("copilot",{state:"writing"},"[Pasted Content 1400 chars]"),undefined);
});
