const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const {execFileSync} = require("node:child_process");
const {chromium} = require("playwright");

const [base, , root] = process.argv.slice(2);
const original = path.join(root,"one");
const project = path.join(root,"two");
const vault = folder=>path.join(folder,".ff-acp-knowledge");

async function run() {
  for (const folder of [original,project]) execFileSync("git",["-C",folder,"init","--quiet"]);
  const browser=await chromium.launch({executablePath:process.env.CHROME_PATH || undefined});
  try {
    const page=await browser.newPage({viewport:{width:1520,height:940}});
    page.setDefaultTimeout(14000);
    async function post(route,data) {
      const response=await page.request.post(base+"/api"+route,{data});
      assert(response.ok(),await response.text());
      return response.status()===204 ? null : response.json();
    }
    const state=async()=>await (await page.request.get(base+"/api/state")).json();
    await page.goto(base);
    await page.locator('#connection[data-state="online"]').waitFor();
    const settings=page.getByRole("dialog",{name:"项目设置"});
    const settingsMode=settings.getByLabel("项目知识库");
    async function saveMode(mode) {
      await settingsMode.selectOption(mode);
      const pending=page.waitForResponse(response=>response.url()===base+"/api/knowledge/project" && response.request().method()==="PUT");
      await settings.getByRole("button",{name:"保存",exact:true}).click();
      const result=await pending;
      assert(result.ok(),await result.text());
      await settings.waitFor({state:"hidden"});
    }
    const projectRow=page.locator("#current-project .project-select");
    assert(!fs.existsSync(vault(original)),"legacy registration must leave the project unchanged");
    await projectRow.dblclick();
    assert.equal(await settingsMode.inputValue(),"disabled");
    await saveMode("local_only");
    assert(fs.existsSync(path.join(vault(original),".index","knowledge.db")));
    const ignored = ".ff-acp-knowledge/project/INDEX.md";
    assert.match(execFileSync("git",["-C",original,"check-ignore","--no-index",ignored],{encoding:"utf8"}),/\.ff-acp-knowledge/);
    await projectRow.dblclick();
    assert.equal(await settingsMode.inputValue(),"local_only");
    await saveMode("shared");
    assert.match(fs.readFileSync(path.join(vault(original),".gitignore"),"utf8"),/\.index\//);
    assert.match(execFileSync("git",["-C",original,"status","--short","--",".ff-acp-knowledge"],{encoding:"utf8"}),/\?\?/);
    const index=path.join(vault(original),"project","INDEX.md");
    const preserved=fs.readFileSync(index,"utf8");
    await projectRow.dblclick();
    await saveMode("disabled");
    assert.equal(fs.readFileSync(index,"utf8"),preserved);

    const folder=page.locator("#folder-dialog");
    await page.locator("#add-project").click();
    await folder.getByLabel("文件夹路径").fill(project);
    await folder.getByRole("button",{name:"打开",exact:true}).click();
    await folder.getByRole("button",{name:"选用此文件夹"}).waitFor();
    await page.waitForFunction(()=>!document.getElementById("choose-folder").disabled);
    await folder.getByLabel("项目知识库").selectOption("shared");
    await folder.getByRole("button",{name:"选用此文件夹"}).click();
    await folder.waitFor({state:"hidden"});
    assert(fs.existsSync(path.join(vault(project),"project","INDEX.md")));
    fs.writeFileSync(path.join(vault(project),"project","review.md"),"---\ntheme: KnowledgeShunt\nkeywords: [purplecap]\n---\n# 接口核对\n\n**原始正文**\n");
    console.log("PASS Chromium knowledge modes for old and newly registered projects; Git excludes and Markdown preserved");

    const topic=await post("/topics",{name:"知识库验收",project});
    const first=await post("/windows",{topic:topic.id,name:"领航",engine:"claude",newSession:true});
    const second=await post("/windows",{topic:topic.id,name:"实现",engine:"claude",newSession:true});
    const configure=async payload=>page.request.put(base+"/api/windows/"+first.id+"/config",{data:{
      name:"领航",engine:"claude",...payload
    }});
    const view=async()=> (await state()).windows.find(window=>window.agent.id===first.id);
    const initial=await view();
    assert((await configure({updateModel:true,model:"test-model"})).ok());
    const changed=await view();
    assert.equal(changed.agent.model,"test-model");
    assert.equal(changed.agent.session,initial.agent.session);
    assert.equal(changed.terminal,initial.terminal);
    await post("/windows/"+first.id+"/stop");
    await post("/windows/"+first.id+"/start");
    const reopened=await view();
    assert.equal(reopened.agent.model,"test-model");
    assert.equal(reopened.agent.session,initial.agent.session);
    assert.notEqual(reopened.terminal,initial.terminal);
    assert((await configure({updateModel:true,model:null})).ok());
    assert.equal((await view()).agent.model??null,null);
    await page.locator('.topic-choice[data-topic="'+topic.id+'"] .topic-label').click();
    const confirmation=page.locator("#topic-leader-dialog");
    const flags=id=>[
      page.locator('.tree-agent[data-agent="'+id+'"] .leader-flag'),
      page.locator('.topic-agent-tag[data-agent="'+id+'"] .leader-flag'),
      page.locator('[data-window="'+id+'"] .pane-header .leader-flag')
    ];
    const header=id=>page.locator('[data-window="'+id+'"] .pane-header');
    await header(first.id).getByRole("button",{name:"设为组长",exact:true}).click();
    await confirmation.getByRole("heading",{name:"设置话题组长"}).waitFor();
    assert.equal((await state()).topics.find(t=>t.id===topic.id).leaderWindowId??null,null);
    await confirmation.getByRole("button",{name:"确认",exact:true}).click();
    for (const flag of flags(first.id)) await flag.waitFor();
    await header(second.id).getByRole("button",{name:"设为组长",exact:true}).click();
    await confirmation.getByRole("heading",{name:"移交话题组长"}).waitFor();
    await confirmation.getByRole("button",{name:"确认",exact:true}).click();
    for (const flag of flags(second.id)) await flag.waitFor();
    for (const flag of flags(first.id)) await flag.waitFor({state:"detached"});
    await header(second.id).locator(".rename-agent").click();
    await page.locator("#agent-config-basic").getByRole("button",{name:"取消组长",exact:true}).click();
    await confirmation.getByRole("heading",{name:"取消话题组长"}).waitFor();
    await confirmation.getByRole("button",{name:"确认",exact:true}).click();
    for (const flag of flags(second.id)) await flag.waitFor({state:"detached"});
    const agentSettings=page.locator("#pane-session-dialog");
    if (await agentSettings.isVisible()) await agentSettings.getByRole("button",{name:"关闭会话记录"}).click();
    console.log("PASS Chromium leader confirmation, transfer and removal across the tree, top bar and pane");

    const launchers=page.locator(".resource-launchers");
    assert.deepEqual(await launchers.getByRole("button").allTextContents(),["知识库","MCP 管理","Skills 管理"]);
    await launchers.getByRole("button",{name:"知识库",exact:true}).click();
    await page.getByRole("tab",{name:"Agent 协作文档",selected:true}).waitFor();
    const views=page.getByRole("group",{name:"协作文档来源"});
    await views.getByRole("button",{name:"知识库",exact:true,pressed:true}).waitFor();
    const search=async (term,check=rows=>rows.some(entry=>entry.path==="project/review.md"))=>{
      const result=page.waitForResponse(response=>{
        const url=new URL(response.url());
        return url.pathname==="/api/knowledge/search" && url.searchParams.get("query")===term;
      });
      await page.getByRole("searchbox",{name:"搜索知识库"}).fill(term);
      const response=await result;
      assert(response.ok(),await response.text());
      assert(check(await response.json()));
    };
    await search("KnowledgeShunt");
    const tree=page.getByRole("navigation",{name:"知识库目录"});
    const candidates=tree.locator('button[title^="project/review.md"]');
    await candidates.click();
    const preview=page.getByRole("article",{name:"知识库预览"});
    await preview.getByRole("heading",{name:"接口核对"}).waitFor();
    assert(!((await preview.innerText()).includes("KnowledgeShunt")));
    await page.getByRole("button",{name:"编辑",exact:true}).click();
    const source=page.getByRole("textbox",{name:"知识库 Markdown"});
    const text=await source.inputValue();
    await source.fill(text.replace("原始正文","已编辑正文"));
    await page.getByRole("button",{name:"保存",exact:true}).click();
    await preview.getByText("已编辑正文",{exact:true}).waitFor();
    assert.match(fs.readFileSync(path.join(vault(project),"project","review.md"),"utf8"),/已编辑正文/);
    await search("purplecap");
    await candidates.waitFor();
    await search("话题总计划",rows=>rows.some(entry=>entry.path.endsWith("/PLAN.md")));
    const planButton=tree.getByRole("button",{name:"话题总计划",exact:true});
    const planPath=(await planButton.getAttribute("title")).split(" · ")[0];
    await planButton.click();
    await preview.getByRole("link",{name:"成员任务记录"}).click();
    await tree.getByText("暂无成员任务记录").waitFor();
    const record=await page.request.put(base+"/api/knowledge/document",{data:{
      project,path:planPath.replace(/PLAN\.md$/,"records/实现/验收.md"),content:"# 并发验收\n\n后端与界面均已联调。"
    }});
    assert(record.ok(),await record.text());
    await tree.getByRole("button",{name:"刷新记录"}).click();
    await tree.getByRole("button",{name:"并发验收"}).click();
    await preview.getByRole("heading",{name:"并发验收"}).waitFor();
    await tree.getByRole("button",{name:"返回全部文档"}).click();
    // Older plans can mention the link in a code example without rendering a clickable link.
    fs.writeFileSync(path.join(vault(project),planPath),"# 旧计划\n\n~~~md\n[成员任务记录](records/)\n~~~\n");
    await search("旧计划",rows=>rows.some(entry=>entry.path===planPath));
    await tree.getByRole("button",{name:"旧计划"}).click();
    await preview.getByRole("button",{name:"成员任务记录"}).click();
    await tree.getByRole("button",{name:"并发验收"}).waitFor();
    await views.getByRole("button",{name:"交接文档"}).click();
    await page.getByRole("navigation",{name:"话题文档"}).waitFor();
    console.log("PASS Chromium Markdown browse/search, source editing and handoff document view");
  } finally {
    await browser.close();
  }
}
run().catch(error=>{console.error(error);process.exitCode=1;});
