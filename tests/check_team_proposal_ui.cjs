const assert = require("node:assert/strict");
const { execFileSync } = require("node:child_process");
const { chromium } = require("playwright");

const [base, exe, stateDir, topicId, leaderGrant] = process.argv.slice(2);
assert(base && exe && stateDir && topicId && leaderGrant, "expected base, executable, state directory, topic ID and leader grant");

function propose(summary, members) {
  const reply = execFileSync(exe, ["workbench-mcp", stateDir, "--notify-port", new URL(base).port], {
    env:{...process.env,LX_P0_WINDOW_GRANT:leaderGrant}, timeout:10_000, encoding:"utf8",
    input:JSON.stringify({jsonrpc:"2.0",id:1,method:"tools/call",params:{name:"propose_team_agents",arguments:{summary,members}}})+"\n"
  });
  const result = JSON.parse(reply).result;
  assert(!result.isError,result.content?.[0]?.text);
  const proposal = JSON.parse(result.content[0].text);
  assert.equal(proposal.status,"awaiting_user_confirmation");
  return proposal.proposalId;
}

async function verify() {
  const browser = await chromium.launch({executablePath:process.env.CHROME_PATH});
  try {
    const page = await browser.newPage({viewport:{width:1440,height:900}});
    const state = async ()=>await (await page.request.get(base+"/api/state")).json();
    await page.goto(base);
    await page.locator('#connection[data-state="online"]').waitFor();
    await page.locator(".topic-choice[data-topic='"+topicId+"'] .topic-label").click();
    await page.waitForFunction(id=>document.querySelector("#mobile-topic")?.value===id,topicId);

    const topic = (await state()).topics.find(item=>item.id===topicId);
    const startMembers = (await state()).windows.filter(window=>window.agent.topicId===topicId);
    const members = [
      { name:"前端实现", role:"使用现有组件完成实现", taskSummary:"实现视觉与交互", suggestedEngine:"codex" },
      { name:"辅助审阅", role:"核对功能边界", taskSummary:"复核实现结果", suggestedEngine:"claude" }
    ];
    const dialog = page.getByRole("dialog",{name:"团队组建确认"});
    propose("待替换方案",members);
    await dialog.getByText("待替换方案",{exact:true}).waitFor();
    assert.equal((await state()).windows.filter(window=>window.agent.topicId===topicId).length,startMembers.length,"提案本身不能创建成员");
    const proposalId = propose("当前团队按职责分工",members);
    await dialog.getByText("该提案已结束，请关闭后查看当前提案。",{exact:true}).waitFor();
    assert.equal(await dialog.getByRole("button",{name:/确认并创建已选/}).count(),0);
    await dialog.getByRole("button",{name:"关闭",exact:true}).click();
    await dialog.getByText("当前团队按职责分工",{exact:true}).waitFor();
    assert.equal(await dialog.locator(".leader-flag").count(),1);
    assert.equal(await dialog.getByLabel("前端实现的身份提示词",{exact:true}).inputValue(),members[0].role);

    await dialog.getByLabel("前端实现的成员名称",{exact:true}).fill("实现负责人");
    await dialog.getByLabel("前端实现的身份提示词",{exact:true}).fill("实现交互并维护可访问性");
    await dialog.getByLabel("前端实现的引擎",{exact:true}).selectOption("claude");
    await dialog.getByLabel("前端实现的模型",{exact:true}).selectOption("sonnet");
    await dialog.getByLabel("前端实现的推理档位",{exact:true}).selectOption("high");
    await dialog.getByLabel("前端实现的审批权限",{exact:true}).selectOption("ask");
    await dialog.getByRole("checkbox",{name:"创建 辅助审阅"}).uncheck();
    const confirmUrl = base+"/api/topics/"+topicId+"/team-proposals/"+proposalId+"/confirm";
    const pending = page.waitForResponse(response=>response.url()===confirmUrl && response.request().method()==="POST");
    await dialog.getByRole("button",{name:"确认并创建已选 Agent (1)",exact:true}).click();
    const result = await pending;
    assert(result.ok(),await result.text());
    assert.deepEqual(result.request().postDataJSON(),{members:[{
      index:0,name:"实现负责人",role:"实现交互并维护可访问性",engine:"claude",model:"sonnet",effort:"high",mode:null,approval:"ask"
    }]});
    const resultBody = await result.json();
    assert.equal(resultBody.status,"created");
    assert.equal(resultBody.members.length,1);
    assert(resultBody.members[0].windowId && resultBody.members[0].online,JSON.stringify(resultBody));
    await dialog.getByText("在线待命",{exact:true}).waitFor();
    const finished = (await state()).windows.filter(window=>window.agent.topicId===topicId);
    assert.equal(finished.length,startMembers.length+1);
    const created = finished.find(window=>window.agent.id===resultBody.members[0].windowId);
    assert.deepEqual([created.agent.name,created.agent.role,created.agent.engine],["实现负责人","实现交互并维护可访问性","claude"]);
    assert(created.agent.online);
    await dialog.getByRole("button",{name:"关闭",exact:true}).click();

    const params = new URLSearchParams({project:topic.project,topicId,query:"实现负责人"});
    const search = await page.request.get(base+"/api/knowledge/search?"+params);
    assert(search.ok(),await search.text());
    const entry = (await search.json()).find(item=>item.path.endsWith("/TEAM.md"));
    assert(entry,"知识库索引必须找到已创建成员");
    const rosterParams = new URLSearchParams({project:topic.project,path:entry.path});
    const roster = await page.request.get(base+"/api/knowledge/document?"+rosterParams);
    assert(roster.ok(),await roster.text());
    const content = (await roster.json()).content;
    assert.match(content,/实现交互并维护可访问性/);
    assert.match(content,/实现视觉与交互/);
    assert.doesNotMatch(content,/@辅助审阅/);
    console.log("PASS Chromium team proposal receives SSE, invalidates stale draft, edits one member, creates only checked member and refreshes TEAM.md");

    const rejectionId = propose("不采用的方案",[{...members[1],name:"未采用成员"}]);
    await dialog.getByText("不采用的方案",{exact:true}).waitFor();
    const rejectUrl = base+"/api/topics/"+topicId+"/team-proposals/"+rejectionId+"/reject";
    const reject = page.waitForResponse(response=>response.url()===rejectUrl);
    await dialog.getByRole("button",{name:"拒绝提案",exact:true}).click();
    assert.equal((await reject).status(),204);
    await dialog.waitFor({state:"hidden"});
    await page.getByRole("button",{name:"团队提案",exact:true}).waitFor({state:"hidden"});
    assert.equal((await state()).windows.filter(window=>window.agent.topicId===topicId).length,finished.length);
    console.log("PASS Chromium rejecting a team proposal creates no additional terminal");
  } finally { await browser.close(); }
}

verify().catch(error=>{console.error(error);process.exitCode=1;});
