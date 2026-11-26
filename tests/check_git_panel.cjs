const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const {execFileSync} = require("node:child_process");
const {chromium} = require("playwright");

const [base,,root] = process.argv.slice(2);
const project=path.join(root,"one");
function git(...args) { return execFileSync("git",["-C",project,...args],{encoding:"utf8"}); }
(async()=>{
  git("init","--initial-branch=main");
  git("config","core.autocrlf","false");
  git("config","user.name","Fixture");
  git("config","user.email","fixture@example.invalid");
  fs.appendFileSync(path.join(project,".git","info","exclude"),"\nterminal-*.txt\n");
  fs.writeFileSync(path.join(project,"App.cs"),"before\n");
  fs.writeFileSync(path.join(project,"Removed.cs"),"removed line\n");
  git("add","App.cs","Removed.cs");
  git("commit","-m","Baseline");
  fs.writeFileSync(path.join(project,"App.cs"),"after\n");
  fs.unlinkSync(path.join(project,"Removed.cs"));
  fs.writeFileSync(path.join(project,"New.cs"),"new line\n");
  const browser=await chromium.launch({executablePath:process.env.CHROME_PATH});
  try {
    const page=await browser.newPage({viewport:{width:1530,height:940}});
    page.setDefaultTimeout(14000);
    const errors=[];page.on("pageerror",e=>errors.push(String(e)));
    let statusRequests=0;
    page.on("request",r=>{if(new URL(r.url()).pathname==="/api/git/status")statusRequests++;});
    const post=async(route,data)=>{
      const res=await page.request.post(base+"/api"+route,{data});assert(res.ok(),await res.text());return res.json();
    };
    assert((await page.request.put(base+"/api/project",{data:{path:project}})).ok());
    const topic=await post("/topics",{name:"Git 审阅",project});
    const agent=await post("/windows",{topic:topic.id,name:"终端",engine:"codex",newSession:true});
    await page.goto(base);
    await page.waitForFunction(id=>window.panes?.get?.(id)?.view.inputReady,agent.id);
    await page.locator("#git-status-slot").click();
    assert.equal(await page.locator("#workspace-stage").getAttribute("data-mode"),"code");
    const rail=page.getByRole("region",{name:"Git 变更列表"}), diff=page.locator(".diff-viewer");
    const unstaged=rail.getByRole("region",{name:"未暂存"});
    const marks=[["added","+"],["modified","M"],["deleted","−"]];
    await page.locator("#inspector-tab-files").click();
    const tree=page.getByRole("navigation",{name:"项目文件"});
    const colors=new Set();
    for(const [kind,symbol] of marks) {
      const mark=tree.locator('[data-git-kind="'+kind+'"] .git-file-mark').first();
      await mark.waitFor();
      assert.equal(await mark.textContent(),symbol);
      colors.add(await mark.evaluate(el=>getComputedStyle(el).color));
    }
    assert.equal(colors.size,3);
    await tree.getByRole("searchbox").fill("Removed");
    await tree.getByRole("button",{name:/Removed.cs/}).click();
    await diff.locator(".diff-line.deleted").filter({hasText:"-removed line"}).waitFor();
    for(const [kind,symbol] of marks) assert.equal(await rail.locator('[data-git-kind="'+kind+'"] .git-file-mark').first().textContent(),symbol);
    fs.unlinkSync(path.join(project,"New.cs"));
    fs.writeFileSync(path.join(project,"Removed.cs"),"removed line\n");
    console.log("PASS Git added/modified/deleted symbols and distinct colors, deleted search opens old Diff");
    await unstaged.getByRole("button",{name:/App.cs/}).first().click();
    await diff.locator(".diff-line.deleted").filter({hasText:"-before"}).waitFor();
    await diff.locator(".diff-line.added").filter({hasText:"+after"}).waitFor();
    assert.equal(await diff.locator(".diff-line.added span").nth(1).textContent(),"1");
    if(process.env.FF_GIT_SCREENSHOT)
      await page.locator("#workspace-stage").screenshot({path:process.env.FF_GIT_SCREENSHOT});
    await diff.getByRole("button",{name:"原文件"}).click();
    await diff.getByText("after",{exact:true}).waitFor();
    page.once("dialog",d=>d.dismiss());
    await unstaged.getByRole("button",{name:"放弃未暂存改动 App.cs"}).click();
    assert.equal(fs.readFileSync(path.join(project,"App.cs"),"utf8"),"after\n");
    await unstaged.getByRole("button",{name:"暂存 App.cs",exact:true}).click();
    const staged=rail.getByRole("region",{name:"已暂存"});
    await staged.getByRole("button",{name:"取消暂存 App.cs"}).waitFor();
    await rail.getByRole("textbox",{name:"提交说明"}).fill("Chromium 提交");
    await rail.getByRole("button",{name:"提交",exact:true}).click();
    await rail.getByText("工作区干净").waitFor();
    await rail.getByRole("button",{name:"历史",exact:true}).click();
    await page.getByRole("region",{name:"最近提交"}).getByText("Chromium 提交").waitFor();
    await page.evaluate(id=>panes.get(id).term.focus(),agent.id);
    await page.keyboard.press("Control+b");
    await page.waitForFunction(id=>panes.get(id).view.activity==="working",agent.id);
    fs.writeFileSync(path.join(project,"App.cs"),"after automation\n");
    const requestsBefore=statusRequests;
    await page.keyboard.press("Control+u");
    await page.waitForFunction(id=>panes.get(id).view.activity==="idle",agent.id);
    assert.equal(statusRequests,requestsBefore);
    await unstaged.getByRole("button",{name:/App.cs/}).first().waitFor();
    assert.equal(statusRequests,requestsBefore+1);
    await page.setViewportSize({width:390,height:825});
    assert(await page.locator(".git-workbench").evaluate(e=>e.getBoundingClientRect().width<=390));
    assert.deepEqual(errors,[]);
    console.log("PASS Chromium capsule, two-number Unified, original file, cancel discard, commit and history, 2-second completion refresh and mobile");
  } finally { await browser.close(); }
})().catch(e=>{console.error(e);process.exitCode=1;});
