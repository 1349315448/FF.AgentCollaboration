const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const crypto = require("node:crypto");
const { execFileSync } = require("node:child_process");
const { chromium } = require("playwright");
const [base, exe, root] = process.argv.slice(2);
const db = path.join(root, "state", "workbench.sqlite");
const token = crypto.randomUUID();
const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
const logLines = (folder, file) => {
  const p = path.join(root, folder, file);
  return fs.existsSync(p) ? fs.readFileSync(p, "utf8").trim().split(/\r?\n/).filter(Boolean) : [];
};
function sql(statement, ...args) {
  const code = "import sqlite3,sys,json;db=sqlite3.connect(sys.argv[1]);r=db.execute(sys.argv[2],sys.argv[3:]).fetchall();db.commit();print(json.dumps(r))";
  return JSON.parse(execFileSync("python", ["-c", code, db, statement, ...args], {encoding:"utf8"}));
}
function mcp(name, args) {
  const input = JSON.stringify({jsonrpc:"2.0", id:1, method:"tools/call", params:{name, arguments:args}}) + "\n";
  const res = JSON.parse(execFileSync(exe, ["workbench-mcp", path.dirname(db), "--notify-port", new URL(base).port], {
    input, encoding:"utf8", env:{...process.env, LX_P0_WINDOW_GRANT:token}
  })).result;
  assert(!res.isError, res.content[0].text);
  return JSON.parse(res.content[0].text);
}
async function until(predicate, label, timeout = 14000) {
  const end = Date.now() + timeout;
  do { if (await predicate()) return; await sleep(100); } while (Date.now() < end);
  throw Error("Timed out: " + label);
}
async function checkFoldedDelivery(page,target) {
  const file=path.join(root,"one","mode.txt"), receiveUrl=base+"/api/windows/"+target+"/auto-receive";
  fs.writeFileSync(file,"folded-retry");
  assert((await page.request.put(receiveUrl,{data:{mode:"immediate"}})).ok());
  await page.evaluate(id=>panes.get(id).term.focus(),target);
  await page.keyboard.press("Control+b");
  try { await page.waitForFunction(id=>observePrompt(panes.get(id)).busyInput,target); }
  catch (error) {
    console.error("Codex busy editor",await page.evaluate(id=>{
      const p=panes.get(id), b=p.term.buffer.active;
      return {prompt:observePrompt(p),cols:p.term.cols,x:b.cursorX,y:b.cursorY,rows:Array.from({length:8},(_,i)=>
        [b.getLine(b.baseY+i)?.translateToString(true),b.getLine(b.baseY+i)?.isWrapped])};
    },target));throw error;
  }
  const before=logLines("one","enters.txt").length;
  const body="折叠投递 " + "a".repeat(1030) + "🌿".repeat(41);
  const msg=mcp("send_to_agent",{target,message:body,supersedePrevious:false}).id;
  try {
    await until(()=>sql("SELECT state FROM inbox WHERE id=?",msg)[0][0]==="delivered","folded Codex busy paste and single retry",25000);
    assert.equal(logLines("one","enters.txt").length-before,2);
    assert.equal(logLines("one","accepted.txt").filter(line=>line.includes(msg.slice(0,8)) && line.includes(body)).length,1);
  } catch(error) {
    const capture=path.join(root,"one","folded-editor.txt");
    if (fs.existsSync(capture)) {
      const raw=fs.readFileSync(capture,"utf8");
      console.error("folded source",raw.slice(0,55),[...raw].length,"emoji",raw.split("🌿").length-1,
        "tail code points",[...raw.slice(-10)].map(c=>c.codePointAt(0)));
    }
    console.error("folded delivery",sql("SELECT state FROM inbox WHERE id=?",msg),logLines("one","enters.txt").slice(before),
      await page.evaluate(id=>{const p=panes.get(id);return {prompt:observePrompt(p),inbox:p.view.inbox.map(m=>[m.id.slice(0,8),m.state]),screen:p.term.buffer.active.getLine(p.term.buffer.active.baseY+p.term.buffer.active.cursorY)?.translateToString(true)};},target));
    throw error;
  } finally {
    fs.writeFileSync(file,"submit");
    assert((await page.request.put(receiveUrl,{data:{mode:"queued"}})).ok());
  }
  console.log("PASS Codex folded busy paste, Unicode count, swallowed Enter and exactly one submission");
}

async function run() {
  const browser = await chromium.launch({executablePath:process.env.CHROME_PATH || undefined});
  try {
    const page = await browser.newPage({viewport:{width:1460,height:940}});
    if(process.argv.includes("--expanded")){
      const project=path.join(root,"one");
      const post=async(route,data)=>{const r=await page.request.post(base+"/api"+route,{data});assert(r.ok(),await r.text());return r.json();};
      const topic=await post("/topics",{name:"无重绘提交",project});
      const target=await post("/windows",{topic:topic.id,name:"Codex",engine:"codex",newSession:true});
      const source=await post("/windows",{topic:topic.id,name:"来源",engine:"claude",newSession:true,project:path.join(root,"two")});
      const home=path.join(root,"receipt-home"),sessions=path.join(home,"sessions");
      fs.mkdirSync(sessions,{recursive:true});
      const rollout=path.join(sessions,"rollout-test-"+target.session+".jsonl");
      fs.writeFileSync(rollout,JSON.stringify({type:"session_meta",payload:{id:target.session}})+"\n");
      execFileSync("python",["-c","import sqlite3,sys;d=sqlite3.connect(sys.argv[1]);d.execute('create table threads(id text,rollout_path text)');d.execute('insert into threads values(?,?)',sys.argv[2:]);d.commit()",path.join(home,"state_5.sqlite"),target.session,rollout]);
      sql("UPDATE windows SET grant_hash=? WHERE id=?",crypto.createHash("sha256").update(token).digest("hex").toUpperCase(),source.id);
      await page.goto(base);await page.waitForFunction(id=>window.panes?.get?.(id)?.view.inputReady,target.id);
      // 模拟浏览器在相同 VT 帧只上报一次完整草稿。首次 CR 被吞且没有新的帧。
      await page.evaluate(()=>{const original=WebSocket.prototype.send;const seen=new Set();WebSocket.prototype.send=function(data){
        if(typeof data==="string"){const c=JSON.parse(data);if(c.type==="prompt"&&c.retryDraft&&c.draftTail){
          const key=c.revision+"/"+c.retryDraft+"/"+c.draftTail;if(seen.has(key))return;seen.add(key);
        }}return original.call(this,data);};});
      fs.writeFileSync(path.join(project,"mode.txt"),"silent");
      const body="无重绘完整草稿验证 "+"A".repeat(24), msg=mcp("send_to_agent",{target:target.id,message:body}).id;
      await until(()=>logLines("one","accepted.txt").some(l=>l.includes(msg.slice(0,8))),"only a confirmed unchanged draft gets one extra Enter",15000);
      const accepted=logLines("one","accepted.txt").filter(l=>l.includes(msg.slice(0,8)));
      assert.equal(accepted.length,1);assert.equal(logLines("one","enters.txt").length,2);
      assert.equal(sql("SELECT state FROM inbox WHERE id=?",msg)[0][0],"writing");
      fs.appendFileSync(rollout,JSON.stringify({type:"response_item",payload:{type:"message",role:"user",content:[{type:"input_text",text:accepted[0]}]}})+"\n");
      await until(()=>sql("SELECT state FROM inbox WHERE id=?",msg)[0][0]==="delivered","native session confirms exact body");
      console.log("PASS Codex native receipt awaiting, complete expanded draft, zero new frames, two Enters, one accepted message");
      await page.waitForFunction(id=>panes.get(id)?.view.inputReady,target.id);
      fs.writeFileSync(path.join(project,"mode.txt"),"review-retry");
      const review=mcp("send_to_agent",{target:target.id,message:"待核对后复检保留的原草稿 "+ "B".repeat(24)}).id;
      const reviewState=()=>sql("SELECT state FROM inbox WHERE id=?",review)[0][0];
      await until(()=>reviewState()==="delivery_uncertain","two Enters lost and outcome uncertain",22000);
      await page.waitForFunction(({target,id})=>panes.get(target)?.view.inbox.some(item=>item.id===id && item.state==="delivery_uncertain"),{target:target.id,id:review});
      fs.writeFileSync(path.join(project,"review-redraw.txt"),"");
      await until(()=>logLines("one","accepted.txt").some(l=>l.includes(review.slice(0,8))),"periodic check submits the same owned draft",12000);
      const acceptedReview=logLines("one","accepted.txt").filter(l=>l.includes(review.slice(0,8)));
      assert.equal(acceptedReview.length,1);
      const reviewAttempts=logLines("one","enters.txt").filter(l=>l==="review-retry").length;
      assert(reviewAttempts>=3 && reviewAttempts<=7);
      fs.appendFileSync(rollout,JSON.stringify({type:"response_item",payload:{type:"message",role:"user",content:[{type:"input_text",text:acceptedReview[0]}]}})+"\n");
      await until(()=>reviewState()==="delivered","periodic check recognizes late native receipt",12000);
      assert.equal(logLines("one","enters.txt").filter(l=>l==="review-retry").length,reviewAttempts);
      console.log("PASS uncertain delivery rechecks a redrawn folded draft and consumes exactly one native receipt");return;
    }
    if (process.argv.includes("--buddy-inbox")) {
      const project = path.join(root,"one");
      const post = async (route,data) => {
        const result = await page.request.post(base+"/api"+route,{data});
        assert(result.ok(),await result.text());return result.json();
      };
      fs.writeFileSync(path.join(project,"native-prompt.fixture"),"codebuddy");
      fs.writeFileSync(path.join(project,"channel.fixture"),"");
      const topic = await post("/topics",{name:"灰字与收件",project});
      const target = await post("/windows",{topic:topic.id,name:"接收",engine:"codebuddy",newSession:true});
      const sender = await post("/windows",{topic:topic.id,name:"发件",engine:"codex",newSession:true,project:path.join(root,"two")});
      sql("UPDATE windows SET grant_hash=? WHERE id=?",crypto.createHash("sha256").update(token).digest("hex").toUpperCase(),sender.id);
      await page.goto(base);
      await page.waitForFunction(id=>window.panes?.get?.(id)?.view.inputReady,target.id);
      const msg = mcp("send_to_agent",{target:target.id,message:"确认灰色建议不阻塞收件"}).id;
      await until(()=>sql("SELECT state FROM inbox WHERE id=?",msg)[0][0]==="delivered","unconfirmed channel falls back on an empty CodeBuddy editor",35000);
      assert.equal(logLines("one","accepted.txt").filter(line=>line.includes(msg.slice(0,8))).length,1);
      assert.equal(logLines("one","enters.txt").length,1);
      assert.equal(sql("SELECT channel_confirmed FROM windows WHERE id=?",target.id)[0][0],0);
      console.log("PASS unconfirmed native channel falls back to CodeBuddy with grey suggestion and submits exactly once");return;
    }
    if (process.argv.includes("--phase13")) {
      const create = async (route,data) => {
        const r=await page.request.post(base+"/api"+route,{data}); assert(r.ok(),await r.text()); return r.json();
      };
      const topic=await create("/topics",{name:"投递复核",project:path.join(root,"one")});
      const target=await create("/windows",{topic:topic.id,name:"Codex",engine:"codex",newSession:true});
      const source=await create("/windows",{topic:topic.id,name:"来源",engine:"claude",newSession:true,project:path.join(root,"two")});
      sql("UPDATE windows SET grant_hash=? WHERE id=?",crypto.createHash("sha256").update(token).digest("hex").toUpperCase(),source.id);
      await page.goto(base);
      await page.waitForFunction(id=>window.panes?.get?.(id)?.view.inputReady,target.id);
      fs.writeFileSync(path.join(root,"one","mode.txt"),"repair-paste");
      const repairText="截断核验 REPAIR END 63829";
      const repaired=mcp("send_to_agent",{target:target.id,message:repairText}).id;
      try {
        await until(()=>sql("SELECT state FROM inbox WHERE id=?",repaired)[0][0]==="delivered","owned draft repaired",25000);
      } catch(error) {
        console.error("repair outcome",sql("SELECT state FROM inbox WHERE id=?",repaired),logLines("one","enters.txt"),logLines("one","clears.txt"),
          await page.evaluate(id=>observePrompt(panes.get(id)),target.id));
        throw error;
      }
      assert.equal(logLines("one","clears.txt").filter(line=>line.includes(repaired.slice(0,8))).length,1);
      assert.equal(logLines("one","enters.txt").filter(mode=>mode==="repair-paste").length,1);
      assert.equal(logLines("one","accepted.txt").filter(line=>line.includes(repairText)).length,1);
      console.log("PASS truncated Codex paste cleared once, repasted, and accepted the intact payload once");
      await checkFoldedDelivery(page,target.id);
      fs.writeFileSync(path.join(root,"one","mode.txt"),"folded-changed");
      await page.request.put(base+"/api/windows/"+target.id+"/auto-receive",{data:{mode:"immediate"}});
      await page.evaluate(id=>panes.get(id).term.focus(),target.id);
      await page.keyboard.press("Control+b");
      const changed=mcp("send_to_agent",{target:target.id,message:"保护草稿 "+"a".repeat(1200)}).id;
      await until(()=>sql("SELECT state FROM inbox WHERE id=?",changed)[0][0]==="delivery_uncertain","changed folded count",22000);
      assert.equal(logLines("one","enters.txt").filter(mode=>mode==="folded-changed").length,1);
      console.log("PASS a changed folded character count prevents a second Enter");
      await page.keyboard.press("Control+u");
      await page.waitForFunction(id=>panes.get(id).view.inputReady,target.id);
      fs.writeFileSync(path.join(root,"one","mode.txt"),"folded-approval");
      const approval=mcp("send_to_agent",{target:target.id,message:"审批保护 "+"a".repeat(1200)}).id;
      await until(()=>sql("SELECT state FROM inbox WHERE id=?",approval)[0][0]==="delivery_uncertain","approval replaced folded draft",26000);
      assert.equal(logLines("one","enters.txt").filter(mode=>mode==="folded-approval").length,0);
      console.log("PASS an approval replacing an observed folded editor receives zero Enter keys");
      const offline=await create("/windows",{topic:topic.id,name:"离线",engine:"claude",newSession:true,project:path.join(root,"third")});
      await page.waitForFunction(id=>panes.get(id)?.view.inputReady,offline.id);
      const targetUrl=base+"/api/windows/"+offline.id;
      const signout=path.join(root,"third","signed-out.fixture");
      fs.writeFileSync(signout,"");
      assert((await page.request.post(targetUrl+"/stop")).ok());
      const failed=mcp("send_to_agent",{target:offline.id,message:"唤醒失败时保留任务",supersedePrevious:false}).id;
      await page.waitForFunction(id=>panes.get(id)?.view.wakeError?.includes("登录"),offline.id);
      assert.equal(sql("SELECT state,wake_requested FROM inbox WHERE id=?",failed)[0].join(","),"pending,0");
      assert.equal(logLines("third","terminal-pids.txt").length,1);
      fs.unlinkSync(signout);
      const retry=mcp("send_to_agent",{resend:failed,supersedePrevious:false}).id;
      await until(()=>[failed,retry].every(id=>sql("SELECT state FROM inbox WHERE id=?",id)[0][0]==="delivered"),"resend wakes once",25000);
      assert.equal(logLines("third","terminal-pids.txt").length,2);
      console.log("PASS signed-out wake failure retains the queue; an authorized resend starts one process and delivers both messages");
      await page.locator('.topic-choice[data-topic="'+topic.id+'"]').click();
      const pin=page.locator('[data-window="'+source.id+'"] .set-primary');
      if (!await pin.isVisible()) await page.locator('[data-window="'+source.id+'"] .pane-more').click();
      await pin.click();
      await page.setViewportSize({width:1280,height:900});
      await page.locator("#toggle-inspector").click();
      const divider=page.locator("#inspector-divider"), bounds=await divider.boundingBox();
      const stage=await page.locator("#workspace-stage").boundingBox();
      await page.mouse.move(bounds.x+3,bounds.y+32);
      await page.mouse.down();
      await page.evaluate(()=>{
        window.dragResizes=0;
        const send=WebSocket.prototype.send;
        WebSocket.prototype.send=function(data){
          if(typeof data==="string" && JSON.parse(data).type==="resize") window.dragResizes++;
          return send.call(this,data);
        };
      });
      await page.mouse.move(stage.x+636,bounds.y+32,{steps:8});
      await page.evaluate(()=>refresh());
      assert.equal(await page.evaluate(()=>window.dragResizes),0);
      await page.mouse.up();
      await page.waitForFunction(()=>byId("pane-divider").hidden && byId("agent-stage").clientWidth<650);
      await divider.press("ArrowRight");
      await page.waitForFunction(()=>!byId("pane-divider").hidden && byId("agent-stage").clientWidth>=650);
      console.log("PASS inspector drag defers all PTY resizes and restores the tiled split across the 650px threshold");
      const late=await create("/windows",{topic:topic.id,name:"迟到回执",engine:"codex",newSession:true,project:path.join(root,"third")});
      const lateUrl=base+"/api/windows/"+late.id;
      assert((await page.request.post(lateUrl+"/stop")).ok());
      const session=crypto.randomUUID(), profile=path.join(root,"cli-user",".codex");
      const nativePath=path.join(profile,"sessions","rollout-fixture-"+session+".jsonl");
      fs.mkdirSync(path.dirname(nativePath),{recursive:true});
      fs.writeFileSync(nativePath,JSON.stringify({type:"session_meta",payload:{id:session}})+"\n");
      execFileSync("python",["-c","import sqlite3,sys; db=sqlite3.connect(sys.argv[1]); db.execute('create table threads(id, rollout_path)'); db.execute('insert into threads values (?,?)',sys.argv[2:]); db.commit()",
        path.join(profile,"state_fixture.sqlite"),session,nativePath]);
      sql("UPDATE windows SET session=? WHERE id=?",session,late.id);
      assert((await page.request.post(lateUrl+"/start")).ok());
      await page.waitForFunction(id=>panes.get(id)?.view.inputReady,late.id);
      fs.writeFileSync(path.join(root,"third","mode.txt"),"foreign");
      const lateText="终端回执稍晚才落盘";
      const lateId=mcp("send_to_agent",{target:late.id,message:lateText,supersedePrevious:false}).id;
      const isState=state=>sql("SELECT state FROM inbox WHERE id=?",lateId)[0][0]===state;
      await until(()=>isState("delivery_uncertain"),"await exact native receipt",26000);
      const enters=logLines("third","enters.txt").length;
      const received="MSG_ID: "+lateId.slice(0,8)+" | FROM_AGENT: 来源 | MSG: "+lateText;
      const record=(role,text)=>JSON.stringify({type:"response_item",payload:{type:"message",role,content:[{type:"input_text",text}]}})+"\n";
      fs.appendFileSync(nativePath,record("assistant",received));
      await sleep(5500);
      assert(isState("delivery_uncertain"));
      fs.appendFileSync(nativePath,record("user",received));
      await until(()=>isState("delivered"),"reconciled late receipt",8500);
      assert.equal(logLines("third","enters.txt").length,enters);
      console.log("PASS a late exact user receipt updates the inbox without submitting again; assistant echo is ignored");
      return;
    }
    const errors = [];
    page.on("pageerror", error => errors.push(error.message));
    page.setDefaultTimeout(10000);
    await page.addInitScript(()=>{
      window.effects=[];
      window.reducedMotionDrawers = [];
      document.addEventListener("animationstart", event => {
        if (event.animationName === "session-drawer-in" && matchMedia("(prefers-reduced-motion: reduce)").matches)
          window.reducedMotionDrawers.push(event.target.id);
      });
      const animate=Element.prototype.animate;
      Element.prototype.animate=function(frames, options) {
        const rect=this.getBoundingClientRect();
        window.effects.push({classes:this.getAttribute("class") || "",path:this.style.offsetPath,frames,center:[rect.x+rect.width/2,rect.y+rect.height/2],
          duration:typeof options==="number"?options:options.duration});
        return animate.call(this,frames,options);
      };
      const send=WebSocket.prototype.send;
      window.sizes=[];
      WebSocket.prototype.send=function(data) {
        if (typeof data==="string" && JSON.parse(data).type==="resize") {
          const p=[...panes.values()].find(p=>p.socket===this);
          window.sizes.push([p?.replaying,p?.element.getBoundingClientRect().width,document.visibilityState,p?.element.hidden || p?.surface.hidden]);
        }
        return send.call(this,data);
      };
    });
    const eventStream = page.waitForResponse(r => new URL(r.url()).pathname === "/api/events");
    await page.goto(base);
    assert.match(await page.locator(".brand-name").textContent(), /Agent Collaboration\s*Platform/);
    assert(await page.locator(".brand-name #connection").count() === 1);
    await page.locator("#setup-dialog[open]").waitFor();
    assert(!fs.existsSync(db));
    assert.equal((await page.request.get(base + "/api/state")).status(), 409);
    await page.keyboard.press("Escape");
    await page.locator("#setup-archive_root").locator("..").getByRole("button").click();
    await page.locator("#folder-dialog[open]").waitFor();
    await page.keyboard.press("Escape");
    assert(await page.locator("#setup-dialog").isVisible());
    assert.equal(await page.locator("#setup-node-name").inputValue(),require("node:os").hostname());
    await page.locator("#setup-node-name").fill("Chromium");
    assert.equal(await page.locator("#setup-form").evaluate(form => form.nodeName), "FORM");
    await page.locator("#setup-submit").click();
    assert.match((await eventStream).headers()["content-type"], /text\/event-stream/);
    await page.waitForFunction(() => !!snapshot);
    await sleep(350);
    let idleReads = 0;
    const countReads = r => { if (new URL(r.url()).pathname === "/api/state") idleReads++; };
    page.on("request", countReads);
    await sleep(1600);
    page.off("request", countReads);
    assert.equal(idleReads, 0, "healthy idle page should not poll /api/state");
    // SSE 连接健康，但一次快照请求失败，空闲期间也应补到快照。
    await page.route("**/api/state*", route => route.fulfill({status:503,json:{error:"temporary failure"}}), {times:1});
    const healthyAgain = page.waitForResponse(r => new URL(r.url()).pathname === "/api/state" && r.ok());
    assert((await page.request.post(base+"/api/events/wake",{headers:{"X-FF-Notify":"1"}})).ok());
    await healthyAgain;
    async function pickFolder(folder, expectedKnowledgeMode) {
      await page.locator("#folder-dialog[open]").waitFor();
      await page.waitForFunction(()=>!!folderListing);
      await page.locator("#folder-path").fill(folder);
      await page.locator("#folder-path").press("Enter");
      await page.waitForFunction(path=>folderListing?.path===path, folder);
      if (expectedKnowledgeMode) assert.equal(await page.locator("#folder-knowledge-mode").inputValue(), expectedKnowledgeMode);
      await page.locator("#choose-folder").click();
      await page.locator("#folder-dialog").waitFor({state:"hidden"});
    }
    assert.deepEqual(await page.evaluate(()=>snapshot.projects), []);
    assert.equal(await page.locator(".project-button").count(),0);
    await page.getByRole("button",{name:"打开项目文件夹",exact:true}).and(page.locator("#empty-create")).click();
    const firstProject = path.join(root,"one");
    await pickFolder(firstProject, "local_only");
    await page.waitForFunction(project=>snapshot.knowledgeModes?.[project]==="local_only", firstProject);
    const post = async (url, body) => {
      const res = await page.request.post(base + "/api" + url, {data:body});
      assert(res.ok(), await res.text());
      return res.status() === 204 ? null : res.json();
    };
    const pane = id => page.locator('[data-window="' + id + '"]');
    async function clickTool(id, selector) {
      const tool = pane(id).locator(selector);
      if (!await tool.isVisible()) await pane(id).locator(".pane-more").click();
      await tool.click();
    }
    const tab = name => page.locator(".project-select").filter({has:page.getByTitle(path.join(root,name),{exact:true})});
    async function choose(id) {
      await page.waitForFunction(id=>snapshot?.topics.some(t=>t.id===id),id);
      if (!await page.locator("#project-rail").isVisible()) await page.locator("#project-collapse").click();
      const topic=await page.evaluate(id=>snapshot.topics.find(t=>t.id===id),id);
      const disclosure=tab(path.basename(topic.project));
      if(await disclosure.getAttribute("aria-expanded")==="false") await disclosure.click();
      const row=page.locator('.topic-choice[data-topic="'+id+'"]');
      if (topic.status==="finished" && !await row.count()) await disclosure.locator("xpath=../..").locator(".tree-archive").click();
      await row.locator(".topic-label").click();
      await page.waitForFunction(id=>selectedTopicId===id,id);
    }
    async function openProject(name) {
      const id=await page.evaluate(p=>localStorage.getItem("lx.agent.workbench.selectedTopic."+projectKey(p)) ?? snapshot.topics.find(t=>t.project===p)?.id,path.join(root,name));
      await choose(id);
    }
    const ready = async id => {
      try { await page.waitForFunction(id => window.panes?.get?.(id)?.view.inputReady, id); }
      catch (error) {
        console.error(await page.evaluate(id => {
          const p=panes.get(id); if (!p) return "Missing pane";
          const b=p.term.buffer.active;
          return {online:p.view.agent.online,revision:p.revision,replay:p.replaying,paste:p.term.modes.bracketedPasteMode,
            prompt:observePrompt(p),cursor:[b.cursorX,b.cursorY],lines:Array.from({length:p.term.rows},(_,i)=>b.getLine(b.baseY+i)?.translateToString(true)).filter(Boolean).slice(-8)};
        },id));
        throw error;
      }
    };
    const send = (target, message) => mcp("send_to_agent", {target, message, supersedePrevious:false}).id;
    const state = id => sql("SELECT state FROM inbox WHERE id=?", id)[0][0];
    const key = async (id, name) => { await page.evaluate(id => panes.get(id).term.focus(), id); await page.keyboard.press(name); };
    const mode = value => fs.writeFileSync(path.join(root, "one", "mode.txt"), value);

    assert.equal(await page.title(),"Agent Collaboration Platform");
    await page.emulateMedia({reducedMotion:"reduce"});
    await page.locator("#open-settings").click();
    await page.waitForFunction(() => window.reducedMotionDrawers.includes("settings-dialog"));
    await page.locator("#path-handoffs").waitFor();
    await page.locator("#settings-engines .health-row").filter({hasText:"Claude Code"}).waitFor();
    await page.locator("#settings-environment .health-row").filter({hasText:"Windows ConPTY"}).waitFor();
    assert.equal(await page.locator("#path-data_dir").inputValue(), path.join(root, "state"));
    assert(await page.evaluate(() => {
      const drawer = byId("settings-dialog").getBoundingClientRect();
      const scroll = document.querySelector(".settings-scroll");
      return Math.abs(drawer.right - innerWidth) < 1 && drawer.height === innerHeight &&
        getComputedStyle(scroll, "::-webkit-scrollbar").width === "6px" &&
        getComputedStyle(scroll).overflowY === "auto";
    }));
    await page.locator("#path-handoffs").click();
    assert(await page.locator("#settings-dialog").isVisible());
    await page.locator("#path-handoffs").locator("..").getByRole("button", {name:"选文件夹"}).click();
    await page.locator("#folder-dialog[open]").waitFor();
    await page.mouse.click(12, 90);
    assert(await page.locator("#folder-dialog").isVisible() && await page.locator("#settings-dialog").isVisible());
    await page.keyboard.press("Escape");
    await page.mouse.click(12, 90);
    await page.locator("#settings-dialog").waitFor({state:"hidden"});
    await page.emulateMedia({reducedMotion:"no-preference"});
    assert(await page.evaluate(() => {
      const name = byId("project-name"), full = name.dataset.path;
      const standard = name.textContent;
      name.style.flex = "0 0 900px";
      fitProjectPath(name);
      const fit = name.textContent === full;
      name.style.removeProperty("flex");
      fitProjectPath(name);
      return fit && standard.includes("....") && full.startsWith(standard.split("....")[0]) &&
        full.endsWith(standard.split("....")[1]) && name.title === full;
    }));
    await page.locator("#empty-create").click();
    await page.locator("#topic-name").fill("开发");
    await page.locator("#topic-goal").fill("实现接口");
    const archive = path.join(root, "knowledge", "development-tasks", "existing");
    await page.locator("#topic-folder").selectOption(archive);
    await page.locator("#topic-submit").click();
    await page.locator("#new-dialog[open]").waitFor();
    const a = await page.locator("#new-dialog").getAttribute("data-topic");
    assert.equal(fs.readFileSync(path.join(archive, "PLAN.md"), "utf8"), "original plan\r\n");
    assert.ok(fs.statSync(path.join(archive, "handoffs")).isDirectory());
    assert.equal(await page.locator("#agent-project-path").textContent(), path.join(root, "one"));
    await page.keyboard.press("Escape");
    const b = (await post("/topics", {name:"设计", project:path.join(root, "two")})).id;
    const another = (await post("/topics", {name:"同项目备忘", project:path.join(root, "one")})).id;
    assert.equal(await page.locator(".topbar #new-agent").count(), 1);
    assert(await page.locator(".topbar #topic-list").isVisible());
    assert(await page.locator("#project-rail #project-tabs").isVisible());
    let installed = false, attempts = 0, catalogs = 0;
    await page.route("**/api/engines", route => route.fulfill({json:{codex:true, copilot:installed}}));
    await page.route("**/api/engines/copilot/install", route => {
      assert.equal(route.request().method(),"POST");
      if (++attempts === 1) return route.fulfill({status:503, json:{error:"安装失败，请重试"}});
      installed = true;
      return route.fulfill({status:204});
    });
    await page.route("**/api/models/copilot?*", route => { catalogs++; return route.fulfill({json:[]}); });
    await choose(another);
    await page.locator("#new-agent").click();
    await page.locator("#new-dialog[open]").waitFor();
    assert.equal(await page.locator("#new-dialog").getAttribute("data-topic"), another);
    await page.getByRole("radio", {name:"GitHub Copilot"}).check();
    const install = page.getByRole("button", {name:"安装 GitHub Copilot"});
    await install.waitFor();
    assert(await page.locator('[data-install="codex"]').isHidden());
    assert(await page.locator("#create-agent").isDisabled());
    assert.equal(catalogs, 0);
    await install.click();
    await page.locator("#create-error").filter({hasText:"安装失败"}).waitFor();
    await install.click();
    await install.waitFor({state:"hidden"});
    await page.waitForFunction(()=>!document.getElementById("create-agent").disabled);
    assert.equal(catalogs, 1);
    assert.equal(await page.locator("#session-source").inputValue(), "latest");
    await page.keyboard.press("Escape");
    await page.unroute("**/api/engines");
    await page.unroute("**/api/engines/copilot/install");
    await page.unroute("**/api/models/copilot?*");
    await choose(another);
    const offlineNames = ["接口开发", "页面开发", "测试开发"];
    const offlineIds = offlineNames.map(name => {
      const id = crypto.randomUUID().replaceAll("-", "");
      sql("INSERT INTO windows(id,name,engine,project,topic_id) VALUES(?,?,'codex',?,?)", id, name, path.join(root,"one"), another);
      return id;
    });
    const settleLayout = () => page.waitForFunction(() => !document.querySelector(".pane-flight") &&
      [...panes.values()].every(p => p.layoutMotion?.playState !== "running"));
    await page.waitForFunction(ids => ids.every(id => panes.has(id)), offlineIds);
    await settleLayout();
    for (const [i,id] of offlineIds.entries()) {
      const overlay = pane(id).locator(".terminal-overlay");
      await overlay.waitFor();
      assert.equal(await overlay.locator(".offline-name").textContent(), "@" + offlineNames[i]);
      const dot = page.locator('#topic-list .topic-agent-tag[data-agent="' + id + '"] .nav-dot');
      assert.deepEqual(await dot.evaluate(el => [getComputedStyle(el).backgroundColor, getComputedStyle(el).animationName]),
        ["rgb(99, 99, 110)", "none"]);
      const button = overlay.getByRole("button", {name:"打开终端", exact:true});
      const nameBox = await overlay.locator(".offline-name").boundingBox(), buttonBox = await button.boundingBox(), surface = await overlay.boundingBox();
      assert(nameBox.y + nameBox.height <= buttonBox.y);
      assert(Math.abs((nameBox.y + buttonBox.y + buttonBox.height) / 2 - surface.y - surface.height / 2) < 2);
      assert(Math.abs(buttonBox.x + buttonBox.width / 2 - surface.x - surface.width / 2) < 2);
    }
    const tag = id => page.locator('.topic-agent-tag[data-agent="'+id+'"]');
    const fold = async id => {
      const button=pane(id).locator(".toggle-collapse");
      if (await button.isVisible()) await button.click();
      else {
        await tag(id).locator(".member-name").click();
        await pane(id).locator('.terminal-surface').waitFor();
      }
    };
    const paneBounds = id => pane(id).boundingBox();
    const byPosition = await Promise.all(offlineIds.map(async id => ({id, box:await paneBounds(id)})));
    byPosition.sort((a,b) => a.box.x - b.box.x);
    const [first, middle, last] = byPosition.map(item => item.id);
    assert(byPosition.every(item => item.box.y === byPosition[0].box.y));
    await fold(middle);
    await settleLayout();
    const gridBox = await page.locator("#panes").boundingBox();
    const [firstBox, foldedBox, lastBox] = await Promise.all([first,middle,last].map(paneBounds));
    assert.equal(foldedBox,null);
    assert(await pane(middle).evaluate(el=>el.parentElement.id==="terminal-parking"));
    assert.equal(await tag(middle).getAttribute("data-expanded"),"false");
    assert.equal(Math.round((await tag(middle).boundingBox()).height),22);
    assert(await page.evaluate(() =>
      window.effects.some(e => e.classes.includes("pane-flight") && e.frames[0].transform?.includes("scale(")) &&
      window.effects.some(e => e.classes === "pane" && e.frames[0].transform?.startsWith("translate(") && !e.frames[0].transform.includes("scale("))
    ));
    assert.equal(firstBox.y, lastBox.y);
    assert(Math.abs(firstBox.height - gridBox.height) < 1);
    assert(Math.abs(firstBox.width - lastBox.width) < 1 && Math.abs(lastBox.x + lastBox.width - gridBox.x - gridBox.width) < 1);
    await fold(first);
    await settleLayout();
    assert(Math.abs((await paneBounds(last)).width - (await page.locator("#panes").boundingBox()).width) < 1);
    await page.emulateMedia({reducedMotion:"reduce"});
    const flightCount = await page.evaluate(() => window.effects.filter(e => e.classes.includes("pane-flight")).length);
    for (const id of [first, middle]) await fold(id);
    await settleLayout();
    assert(await page.evaluate(() => window.effects.filter(e => e.classes.includes("pane-flight")).length) > flightCount);
    assert(await page.locator("#collapsed-panes").isHidden());
    await page.emulateMedia({reducedMotion:"no-preference"});
    const pin = pane(first).locator(".set-primary");
    if (!await pin.isVisible()) await pane(first).locator(".pane-more").click();
    await pin.click();
    const pinnedBox = await paneBounds(first);
    await fold(middle);
    await settleLayout();
    const workspace = await page.locator("#workspace-layout").boundingBox();
    const pinned = await paneBounds(first), rightTile = await paneBounds(last);
    assert(await pane(first).evaluate(el => el.parentElement.id === "primary-panes"));
    assert(await page.locator("#pane-divider").isVisible());
    assert.equal(pinnedBox.width, pinned.width);
    assert.equal(workspace.height, pinned.height);
    assert.equal(pinned.y, rightTile.y);
    assert.equal(pinned.height, rightTile.height);
    await fold(middle);
    await settleLayout();
    assert(await page.evaluate(() => {
      const flights = window.effects.filter(e => e.classes.includes("pane-flight"));
      return new DOMMatrix(flights.at(-2).frames[0].transform).a > 1 && new DOMMatrix(flights.at(-1).frames[0].transform).a < 1;
    }));
    await pane(offlineIds[0]).getByRole("button", {name:"打开终端", exact:true}).click();
    await ready(offlineIds[0]);
    assert(await pane(offlineIds[0]).locator(".terminal-overlay").isHidden());
    assert(await pane(offlineIds[1]).getByRole("button", {name:"打开终端", exact:true}).isVisible());
    // 模拟两次轮询之间已送达的消息，验证同项目跨话题的两个方向的单次交接动画。
    await choose(a);
    const localPeer = (await post("/windows", {name:"回执测试", topic:a, engine:"codex", newSession:true})).id;
    await pane(localPeer).waitFor();
    await ready(localPeer);
    await page.emulateMedia({reducedMotion:"reduce"});
    for (const [source,target] of [[localPeer,first],[first,localPeer]]) {
      await page.evaluate(({source,target}) => {
        window.effects = [];
        const from = messageAnchor(source).getBoundingClientRect();
        window.flightOrigin = [from.left+from.width/2,from.top+from.height/2];
        const before = {...snapshot, timelineTopic:selectedTopicId, timeline:[]};
        animateMessages(before, {...before, timeline:[{id:"already-delivered", source, target, state:"delivered"}]});
      }, {source,target});
      const envelope = page.locator(".message-flight").first();
      await envelope.waitFor({state:"visible"});
      const appearance = await envelope.evaluate(el => {
        const style = getComputedStyle(el), icon = getComputedStyle(el.querySelector("rect"));
        return [style.width,style.height,icon.fill,icon.stroke,style.filter];
      });
      assert.deepEqual(appearance.slice(0,4), ["18px","13px","rgb(2, 132, 199)","rgb(186, 230, 253)"]);
      assert.match(appearance[4], /drop-shadow\(.*rgb\(56, 189, 248\).*6px/);
      await page.waitForFunction(() => window.effects.some(e=>e.classes==="mailbox-flag") &&
        window.effects.some(e=>e.duration===320 && /topic-agent-tag|tree-agent|topic-choice/.test(e.classes)));
      const matched = await page.evaluate(({source,target,first}) => {
        const to = messageAnchor(target).getBoundingClientRect();
        const coords = window.effects.find(e=>e.classes==="message-flight").path.match(/-?\d+(?:\.\d+)?/g).map(Number);
        const landed = document.querySelector(".message-flight")?.style.offsetPath.match(/-?\d+(?:\.\d+)?/g)?.map(Number) || coords;
        return {tag:!!messageAnchor(first,true).closest("#project-tabs"), startDelta:coords[0]-window.flightOrigin[0],
          endDelta:landed.at(-2)-to.x-to.width/2, downward:source !== first || coords[3] > Math.max(coords[1],coords.at(-1))};
      }, {source,target,first});
      assert(matched.tag && matched.downward && Math.abs(matched.startDelta)<2 && Math.abs(matched.endDelta)<2, JSON.stringify(matched));
      const border = page.locator(".receipt-sweep");
      assert.equal(await border.count(), 1);
      assert(await border.evaluate((svg, {target, first}) => {
        const frame = messageAnchor(target,true);
        if (svg.parentElement !== frame) return false;
        const overlay = svg.getBoundingClientRect(), box = frame.getBoundingClientRect();
        const right = svg.querySelector(".receipt-right"), down = svg.querySelector(".receipt-down");
        const outline = svg.querySelector(".receipt-outline");
        const animations = svg.getAnimations({subtree:true});
        const flash = outline.getAnimations().find(a => a.animationName === "receipt-flash");
        if (!right || !down || !flash) return false;
        const {delay:meet, duration} = flash.effect.getTiming();
        const seek = time => { for (const a of animations) a.currentTime = time; };
        function tip(rect) {
          const style = getComputedStyle(rect);
          const dash = parseFloat(style.strokeDasharray), offset = parseFloat(style.strokeDashoffset);
          const percent = ((rect === right ? dash : 0) - offset + 100) % 100;
          return rect.getPointAtLength(rect.getTotalLength() * percent / 100);
        }
        seek(0);
        const start = tip(right), origin = tip(down);
        seek(meet * .12);
        const top = tip(right), left = tip(down);
        const split = top.x > start.x && Math.abs(top.y - start.y) < 1 && left.y > start.y;
        seek(meet);
        const end = tip(right), other = tip(down), bounds = outline.getBBox();
        const joined = Math.hypot(end.x - other.x, end.y - other.y) < 1 &&
          Math.abs(bounds.x + bounds.width - end.x) < 16 && Math.abs(bounds.y + bounds.height - end.y) < 3;
        const litBeforeFlash = parseFloat(getComputedStyle(outline).strokeOpacity);
        seek(meet + duration * .35);
        const lit = getComputedStyle(outline);
        const fullFlash = parseFloat(lit.strokeDasharray) === 100 && lit.stroke === "rgb(185, 237, 255)" &&
          parseFloat(lit.strokeOpacity) > litBeforeFlash && flash.effect.getTiming().iterations === 1;
        return Math.abs(overlay.width - box.width) <= 2 && Math.abs(overlay.height - box.height) <= 2 &&
          Math.abs(overlay.x - box.x) <= 1 && Math.abs(overlay.y - box.y) <= 1 &&
          start.x < 16 && start.y <= 3 && Math.hypot(start.x - origin.x, start.y - origin.y) < 1 &&
          split && joined && fullFlash && getComputedStyle(svg).pointerEvents === "none" &&
          getComputedStyle(right).stroke === "rgb(141, 219, 255)";
      }, {target,first}));
      await border.waitFor({state:"detached"});
      await page.waitForFunction(()=>!document.querySelector(".message-flight"));
      const samples = await page.evaluate(() => window.effects.filter(e=>e.classes==="message-flight"));
      assert.equal(samples.length,1);
      const [startX,startY,,,,,slotX,slotY] = samples[0].path.match(/-?\d+(?:\.\d+)?/g).map(Number);
      const distance = Math.hypot(slotX-startX,slotY-startY);
      const arcLength = Math.hypot(distance,Math.max(82,Math.min(140,distance*.28))*1.15);
      const turns = Math.min(5,Math.max(1,Math.round(arcLength/300)));
      assert.equal(parseFloat(samples[0].frames.at(-1).transform.slice(7)),(slotX>=startX?1:-1)*turns*360);
      assert.equal(samples[0].duration,Math.min(1850,Math.max(1050,Math.round(720+arcLength*.62))));
      assert.equal(await page.locator(".message-mailbox,.message-flick-hand,.message-trail").count(),0);
    }
    await page.emulateMedia({reducedMotion:"no-preference"});
    // 两端都在未选中的话题，消息在下一帧之前已完成，不会出现在当前日志或收件条。
    const instant = crypto.randomUUID().replaceAll("-","");
    await page.evaluate(()=>{window.effects=[];});
    sql("INSERT INTO inbox(id,source,target,sender,text,files,document,state,topic_id) VALUES(?,?,?,'接口开发','后台已送达','[]',0,'delivered',?)",
      instant, first, middle, another);
    await page.evaluate(()=>refresh());
    await page.waitForFunction(()=>window.effects.some(e=>e.classes==="message-flight"));
    assert(await page.evaluate(({a,first,middle,instant})=>{
      const coords = window.effects.find(e=>e.classes==="message-flight").path.match(/-?\d+(?:\.\d+)?/g).map(Number);
      const tag = id => document.querySelector('#project-tabs .tree-agent[data-agent="'+id+'"]').getBoundingClientRect();
      const from = tag(first), to = tag(middle);
      return selectedTopicId === a && !snapshot.timeline.some(e=>e.id===instant) &&
        Math.abs(coords[0]-from.x-from.width/2)<2 && Math.abs(coords.at(-2)-to.x-to.width/2)<2 &&
        Math.abs(coords.at(-1)-to.y-to.height/2)<2 && coords[3]>Math.max(coords[1],coords.at(-1));
    }, {a,first,middle,instant}));
    await page.evaluate(()=>refresh());
    await page.waitForFunction(()=>!document.querySelector(".message-flight"));
    assert.equal(await page.evaluate(()=>window.effects.filter(e=>e.classes==="message-flight").length),1);
    assert((await page.request.delete(base+"/api/windows/"+localPeer)).ok());
    for (const id of offlineIds) assert((await page.request.delete(base+"/api/windows/"+id)).ok());
    await choose(a);
    let remaining = 74.5, credits = null, quotaRefreshes = 0;
    await page.route("**/api/quota/codex*", route => {
      if (new URL(route.request().url()).searchParams.has("refresh")) quotaRefreshes++;
      return route.fulfill({json:{supported:remaining!==null || credits!==null, primary:remaining===null?null:{remainingPercent:remaining, resetsAt: 1800000000}, credits}});
    });
    const left = (await post("/windows", {name:"初始名", topic:a, engine:"codex", newSession:true})).id;
    const helper = (await post("/windows", {name:"测试", topic:a, engine:"codex", newSession:true, project:path.join(root,"third")})).id;
    const right = (await post("/windows", {name:"同名", topic:b, engine:"copilot", newSession:true})).id;
    sql("UPDATE windows SET grant_hash=? WHERE id=?",crypto.createHash("sha256").update(token).digest("hex").toUpperCase(),helper);
    await ready(left);
    await page.evaluate(id => {
      const p = panes.get(id);
      window.disconnectedTerminal = {socket:p.socket, term:p.term, at:performance.now()};
      p.socket.close();
    }, left);
    await page.waitForFunction(id => {
      const p = panes.get(id);
      return p.socket !== window.disconnectedTerminal.socket && p.socket.readyState === WebSocket.OPEN && !p.replaying;
    }, left);
    assert(await page.evaluate(id => panes.get(id).term === window.disconnectedTerminal.term &&
      performance.now() - window.disconnectedTerminal.at >= 1400, left));
    assert(await pane(left).getByRole("switch", {name:"自动接收"}).isChecked());
    await choose(b);
    const receive = pane(right).getByRole("switch", {name:"自动接收"});
    assert(await receive.isChecked());
    await receive.uncheck();
    await page.waitForFunction(id => !panes.get(id).view.agent.autoReceive, right);
    await choose(a);
    await page.evaluate(id => { window.beforeRename = panes.get(id).socket; }, left);
    await pane(left).getByRole("button", {name:"修改 Agent 名称"}).click();
    await page.locator("#rename-name").fill("测试");
    await page.locator("#agent-config-basic").getByRole("button", {name:"保存"}).click();
    await page.locator("#rename-error").filter({hasText:"同名"}).waitFor();
    await page.locator("#rename-name").fill("同名");
    await page.locator("#rename-name").press("Enter");
    await pane(left).locator(".tile-name").filter({hasText:"@同名"}).waitFor();
    assert(await page.evaluate(id => panes.get(id).socket === window.beforeRename && window.beforeRename.readyState === WebSocket.OPEN, left));
    const unreadCounts=await page.evaluate(id=>{
      const view=snapshot.windows.find(w=>w.agent.id===id),before=view.inbox;
      const chip=document.querySelector('#topic-list [data-agent="'+id+'"]');
      const row=document.querySelector('.topic-choice[data-selected=true]');
      try {
        return [0,9,10,100].map(count=>{
          view.inbox=Array.from({length:count},(_,i)=>({state:i===0?"delivery_uncertain":"pending"}));
          renderTopics();
          const mail=chip.querySelector(".agent-mail-count");
          return [row.offsetHeight,chip.isConnected,mail?.textContent || "",mail?.dataset.uncertain || ""];
        });
      } finally {view.inbox=before;renderTopics();}
    },left);
    assert.deepEqual(unreadCounts,[0,9,10,100].map(n=>[32,true,n?String(n):"",n?"true":""]));
    assert.equal(await page.locator("#topic-list > :last-child").getAttribute("id"),"new-agent");
    const balance = id => pane(id).locator(".pane-header .pane-quota");
    const quotaText = id => pane(id).locator(".quota-value");
    // Force a read after injecting the fixture because the create dialog shares the same cache.
    await choose(another);
    assert(await pane(left).isHidden());
    await choose(a);
    await balance(helper).click();
    await quotaText(left).filter({hasText:"74.5%"}).waitFor();
    assert.match(await balance(left).locator(".quota-reset").innerText(), /^\d{4}-\d{2}-\d{2} \d{2}:\d{2} 刷新$/);
    remaining = 0;
    credits = {balance:"12.50"};
    await balance(left).click();
    await quotaText(helper).filter({hasText:"0% · 12 credits"}).waitFor();
    assert.equal(await pane(left).locator(".quota-value").textContent(), "0% · 12 credits");
    assert((await balance(helper).getAttribute("title")).split("\n").includes("剩余 credits：12"));
    assert.equal(await page.evaluate(()=>selectedTopicId), a);
    remaining = null;
    credits = {balance:"0.99"};
    await balance(left).click();
    await quotaText(helper).filter({hasText:"0 credits"}).waitFor();
    credits = {unlimited:true, balance:null};
    await balance(left).click();
    await quotaText(helper).filter({hasText:"∞ credits"}).waitFor();
    credits = null;
    await balance(left).click();
    await quotaText(helper).filter({hasText:"—"}).waitFor();
    assert.equal(quotaRefreshes, 5);
    await choose(a);
    const light = pane(left).locator(".pane-header .agent-light");
    await page.waitForFunction(id => panes.get(id).view.activity === "idle", left);
    const idleColor = await light.evaluate(el => getComputedStyle(el).backgroundColor);
    assert.equal(idleColor, "rgb(54, 229, 121)");
    const member = page.locator('#topic-list .topic-agent-tag[data-agent="' + left + '"]');
    if (!await pane(left).isVisible()) {
      await member.locator(".member-name").click();
      await pane(left).waitFor();
    }
    await ready(left);
    await key(left, "x");
    await page.waitForFunction(id => observePrompt(panes.get(id)).draft && panes.get(id).view.activity === "idle", left);
    await page.evaluate(id => panes.get(id).term.write("\r\n◦ Working (esc to interrupt)" +
      "\r\n工具进度".repeat(10) + "\r\n› ", () => reportPrompt(panes.get(id))), left);
    await page.waitForFunction(id => panes.get(id).view.activity === "working", left);
    assert.equal(await page.locator(".current-topic").getAttribute("data-activity"),"working");
    await page.waitForFunction(id => document.querySelector('#topic-list .topic-agent-tag[data-agent="'+id+'"]')?.dataset.activity === "working", left);
    assert.equal(await member.locator(".member-name").innerText(), "@同名");
    assert.equal(await member.locator(".nav-dot").count(), 1);
    assert.equal(await page.locator("#current-project").getAttribute("data-activity"), "working");
    const statusLabel = pane(left).locator(".pane-header .status-label");
    const workingLabelBox = await statusLabel.boundingBox();
    assert.deepEqual(await statusLabel.evaluate(el => [el.textContent, getComputedStyle(el).color]), ["进行中", "rgb(49, 140, 255)"]);
    const indicators = [light, member.locator(".nav-dot")];
    await page.waitForFunction(id => {
      const host = panes.get(id);
      const inline = document.querySelector('#topic-list .topic-agent-tag[data-agent="' + id + '"] .nav-dot');
      return [host.element.querySelector(".agent-light"), inline].every(el =>
          el?.getAnimations().some(pulse => pulse.animationName === "agent-breathe" && pulse.startTime === 0));
    }, left);
    for (const indicator of indicators) {
      const phases = await indicator.evaluate(el => {
        const pulse = el.getAnimations().find(animation => animation.animationName === "agent-breathe");
        pulse.pause(); pulse.currentTime = 0;
        const gray = getComputedStyle(el).backgroundColor;
        const duration = pulse.effect.getTiming().duration;
        pulse.currentTime = duration / 2;
        const blue = getComputedStyle(el).backgroundColor;
        pulse.play();
        return [gray, blue, el.offsetWidth, duration];
      });
      assert.deepEqual(phases, ["rgb(99, 99, 110)", "rgb(49, 140, 255)", 8, 650]);
    }
    await page.emulateMedia({reducedMotion:"reduce"});
    for (const indicator of indicators) {
      assert.deepEqual(await indicator.evaluate(el => [getComputedStyle(el).animationDuration, getComputedStyle(el).transform]),
        ["1.5s", "none"]);
    }
    await page.emulateMedia({reducedMotion:"no-preference"});
    // 暂停页面的状态轮询，直接读取宿主，确认保持期的定时器能独立重报同一帧。
    let resumePoll, pollStarted;
    const resume = new Promise(resolve => { resumePoll = resolve; });
    const started = new Promise(resolve => { pollStarted = resolve; });
    const pausePoll = async route => { pollStarted(); await resume; await route.continue(); };
    await page.route("**/api/state?*", pausePoll);
    try {
      await page.evaluate(() => { void refresh(); });
      await started;
      await key(left, "Control+u");
      await page.waitForFunction(id => readNativePrompt(panes.get(id)).screen === "clear", left);
      // Codex 保留上一轮的 Tip 和耗时。即使没有后续输出，窗格也应在防抖结束后恢复待命。
      await page.evaluate(id => new Promise(resolve => {
        const p = panes.get(id);
        p.term.write("\x1b[H\x1b[2J  └ Tip: Press ctrl+t to open the full transcript." +
          "\r\nWorked for 15s • 09:52\r\n\r\n› \x1b[2mAsk Codex to do anything\x1b[22m\r\x1b[2C",
          () => { reportPrompt(p); resolve(); });
      }), left);
      assert.equal(await page.evaluate(id => readNativePrompt(panes.get(id)).screen, left), "clear");
      assert.deepEqual(await page.evaluate(id => {
        const p = observePrompt(panes.get(id));
        return [p.screen, p.clear, p.empty];
      }, left), ["running",false,false]);
      assert.equal(await statusLabel.innerText(), "进行中");
      await until(async () => {
        const state = await (await page.request.get(base+"/api/state")).json();
        return state.windows.find(w => w.agent.id === left).inputReady;
      }, "Codex hold expiry reports even while state polling is paused", 6500);
    } finally {
      resumePoll();
      await page.unroute("**/api/state?*", pausePoll);
    }
    await page.waitForFunction(id => panes.get(id).view.activity === "idle", left);
    const idleLabelBox = await statusLabel.boundingBox();
    assert.equal(idleLabelBox.x, workingLabelBox.x);
    assert.equal(idleLabelBox.width, workingLabelBox.width);
    assert.notEqual(await page.locator(".current-topic").getAttribute("data-activity"),"working");
    assert.deepEqual(await light.evaluate(el => [getComputedStyle(el).backgroundColor, getComputedStyle(el).animationName]), [idleColor, "none"]);
    assert.equal(await page.locator("#view-modes, .pane-project").count(), 0);
    const header = pane(left).locator(".pane-header");
    assert.equal(await header.locator(".tile-engine").innerText(), "Codex");
    assert.equal(await header.locator(".tile-name").innerText(), "@同名");
    assert.equal(await statusLabel.innerText(), "待命");
    assert(await pane(left).locator(".terminal-surface").evaluate(el => el.offsetHeight >= 300));
    await page.evaluate(id => {
      const {term, socket} = panes.get(id);
      window.beforeCollapse = {term, socket};
    }, left);
    const toggle = header.locator(".toggle-collapse");
    await toggle.click();
    await settleLayout();
    assert.equal(await toggle.getAttribute("aria-expanded"), "false");
    assert.equal(await tag(left).getAttribute("data-expanded"),"false");
    assert(await pane(left).locator(".terminal-surface").isHidden());
    assert(await header.isHidden() && await pane(left).locator(".terminal-toolbar").isHidden());
    assert(await tag(left).locator(".tag-power").isVisible());
    await choose(another);
    await choose(a);
    assert.equal(await pane(left).getAttribute("data-collapsed"), "true");
    await member.locator(".member-name").click();
    await pane(left).locator('.toggle-collapse[aria-expanded="true"]').waitFor();
    assert(await page.evaluate(id => {
      const p = panes.get(id);
      return p.term === beforeCollapse.term && p.socket === beforeCollapse.socket &&
        p.socket.readyState === WebSocket.OPEN && document.activeElement === p.term.textarea;
    }, left));
    await clickTool(left, ".set-primary");
    assert(await header.locator(".primary-mark").isVisible());
    await page.waitForFunction(id => layout.primaryWindowId === id, left);
    assert(await toggle.isHidden());
    await fold(helper);
    await settleLayout();
    assert(await page.locator("#pane-divider").isHidden() && await page.locator("#collaboration").isHidden());
    const area = await page.locator("#workspace-layout").boundingBox(), main = await paneBounds(left);
    assert(await pane(left).evaluate(el => el.parentElement.id === "primary-panes"));
    assert(Math.abs(main.x - area.x) < 1 && Math.abs(main.height - area.height) < 1 && Math.abs(main.width - area.width) < 1);
    const modeSwitch = page.locator("#workspace-mode-switch");
    await page.evaluate(ids => { window.modeHandles=ids.map(id => [id,panes.get(id).term,panes.get(id).socket]); }, [left,helper]);
    await page.locator("#toggle-inspector").click();
    await page.getByRole("tab",{name:"文件",exact:true}).click();
    await page.locator("#inspector-divider").focus();
    await page.keyboard.press("ArrowLeft");
    const inspectorRatio = await page.evaluate(()=>layout.agentInspectorRatio);
    assert(inspectorRatio > .30);
    await modeSwitch.getByRole("button",{name:"代码模式"}).click();
    assert(await pane(left).evaluate(el=>el.parentElement.id==="primary-panes"));
    await fold(helper);
    await settleLayout();
    assert(await pane(left).evaluate(el=>el.parentElement.id==="terminal-parking"));
    assert(await pane(helper).evaluate(el=>el.parentElement.id==="primary-panes"));
    await page.evaluate(([source,target])=>{void flyMessage({source,target});},[helper,left]);
    await page.locator('.topic-agent-tag .receipt-sweep').waitFor({state:"attached"});
    await page.locator("#inspector-panel-action").click();
    assert(await page.locator("#agent-stage").isHidden() && await page.locator("#inspector-shell").isVisible());
    await page.locator("#inspector-panel-action").click();
    await page.keyboard.press("Control+Shift+E");
    assert(await pane(left).evaluate(el=>el.parentElement.id==="primary-panes"));
    assert(await pane(helper).evaluate(el=>el.parentElement.id==="terminal-parking"));
    assert.equal(await page.evaluate(()=>layout.agentInspectorRatio),inspectorRatio);
    assert(await page.evaluate(()=>window.modeHandles.every(([id,term,socket])=>
      panes.get(id).term===term && panes.get(id).socket===socket && socket.readyState===WebSocket.OPEN)));
    await page.locator("#toggle-inspector").click();
    console.log("PASS workspace mode switch, code capsule, receipt sweep, independent sizes and live sockets");
    await fold(helper);
    await settleLayout();
    assert(await pane(helper).evaluate(el => el.parentElement.id === "panes"));
    assert(await page.locator("#pane-divider").isVisible());
    let historyState = "recent", deleteCalls = 0;
    await page.route("**/api/sessions/codex?*includeBound=true", route => {
      const archived = new URL(route.request().url()).searchParams.get("archived") === "true";
      const rows = archived ? [] : [{id:"bound", name:"使用中的会话", mayBeInUse:true}];
      if (historyState === (archived ? "archived" : "recent")) rows.push({id:"entry",name:"独立测试记录"});
      return route.fulfill({json:rows});
    });
    await page.route("**/api/windows/" + left + "/history/*", route => {
      const action = route.request().url().split("/").pop();
      assert.equal(route.request().postDataJSON().session, "entry");
      if (action === "delete") deleteCalls++;
      historyState = action === "archive" ? "archived" : action === "restore" ? "recent" : "deleted";
      return route.fulfill({status:204});
    });
    await page.emulateMedia({reducedMotion:"reduce"});
    await clickTool(left, ".pane-sessions");
    const drawer = page.locator("#pane-session-dialog[open]");
    await page.waitForFunction(() => window.reducedMotionDrawers.includes("pane-session-dialog"));
    await page.emulateMedia({reducedMotion:"no-preference"});
    await page.mouse.click(12, 90);
    await drawer.waitFor({state:"hidden"});
    await clickTool(left, ".pane-sessions");
    const historyRow = drawer.locator("li").filter({hasText:"独立测试记录"});
    await historyRow.waitFor();
    await drawer.evaluate(el => Promise.all(el.getAnimations().map(animation => animation.finished)));
    const bounds = await drawer.boundingBox();
    assert(Math.abs(bounds.x + bounds.width - page.viewportSize().width) <= 1 && bounds.y === 0, JSON.stringify(bounds));
    const desktop = page.viewportSize();
    await page.setViewportSize({width:390,height:780});
    const mobile = await drawer.boundingBox();
    assert(mobile.x === 0 && mobile.width === 390 && mobile.height === 780, JSON.stringify(mobile));
    await page.setViewportSize(desktop);
    assert(await drawer.locator("li").filter({hasText:"使用中的会话"}).getByRole("button",{name:"删除"}).isDisabled());
    await historyRow.getByRole("button",{name:"归档"}).click();
    await historyRow.waitFor({state:"detached"});
    await drawer.getByRole("button",{name:"已归档",exact:true}).click();
    await historyRow.getByRole("button",{name:"还原"}).click();
    await drawer.getByText("暂无已归档会话").waitFor();
    await drawer.getByRole("button",{name:"最近会话",exact:true}).click();
    await historyRow.getByRole("button",{name:"删除"}).click();
    await drawer.getByRole("button",{name:"取消",exact:true}).click();
    assert.equal(deleteCalls, 0);
    await historyRow.getByRole("button",{name:"删除"}).click();
    await drawer.getByRole("button",{name:"永久删除",exact:true}).click();
    await historyRow.waitFor({state:"detached"});
    assert.equal(deleteCalls, 1);
    const switches = [];
    const switchUrl = "**/api/windows/" + left + "/config";
    await page.route(switchUrl, route => {
      switches.push(route.request().postDataJSON());
      return route.fulfill({status:204});
    });
    await drawer.getByRole("button",{name:"新建会话"}).click();
    await drawer.waitFor({state:"hidden"});
    historyState = "recent";
    await clickTool(left, ".pane-sessions");
    await historyRow.getByRole("button",{name:"切换"}).click();
    await drawer.waitFor({state:"hidden"});
    assert.deepEqual(switches.map(s=>[s.sessionMode,s.sessionId]), [["new",undefined],["resume","entry"]]);
    await page.unroute(switchUrl);
    await page.unroute("**/api/sessions/codex?*includeBound=true");
    await page.unroute("**/api/windows/" + left + "/history/*");
    await page.locator("#pane-divider").focus();
    await page.keyboard.press("ArrowRight");
    const ratio = await page.evaluate(() => layout.splitRatio);
    assert(await pane(left).locator(".pane-more").isVisible());
    assert(await pane(left).locator(".terminal-actions > .pane-sessions").isVisible());
    await page.evaluate(id => { window.original = {term:panes.get(id).term,socket:panes.get(id).socket}; },left);
    const topicARow=page.locator('.topic-choice[data-topic="'+a+'"]');
    if (await topicARow.locator(".tree-arrow").getAttribute("aria-expanded")==="false") await topicARow.locator(".tree-arrow").click();
    const topicAPreview=topicARow.locator("xpath=following-sibling::ul[1]");
    await choose(another);
    assert.deepEqual(await topicAPreview.locator(".member-name").allTextContents(),["@同名","@测试"]);
    assert.equal(await topicAPreview.locator('.tree-agent[data-agent="'+left+'"]').getAttribute("data-activity"),"idle");
    await topicAPreview.locator('.tree-agent[data-agent="'+helper+'"] .member-name').click();
    await page.waitForFunction(({a, helper}) => selectedTopicId === a && layout.activeWindowId === helper &&
      document.activeElement === panes.get(helper).term.textarea, {a,helper});
    assert.equal(await page.locator('.topic-choice[data-topic="' + b + '"]').getAttribute("data-selected"), "false");
    assert.equal(await tag(right).count(), 0);
    assert.equal(await tab("third").count(), 1); // Agent cwd alone contributes a tab, not a duplicate topic.
    const emptyProject = path.join(root,"empty-project");
    fs.mkdirSync(emptyProject);
    await page.locator("#add-project").click();
    await page.locator("#folder-path").fill(emptyProject);
    await page.locator("#folder-path").press("Enter");
    await page.locator("#choose-folder").click();
    await page.waitForFunction(path => snapshot.project === path, emptyProject);
    assert.equal(await page.locator("#topic-list .topic-choice").count(), 0);
    assert.equal(await page.title(),"Agent Collaboration Platform");
    await page.locator("#open-settings").click();
    await page.locator("#path-handoffs").waitFor();
    await page.locator("#settings-engines .health-row").filter({hasText:"Claude Code"}).waitFor();
    await page.locator("#settings-environment .health-row").filter({hasText:"Windows ConPTY"}).waitFor();
    await page.locator("#close-settings").click();
    await page.locator("#empty-create").click();
    assert.equal(await page.locator("#topic-project").textContent(), emptyProject);
    await page.keyboard.press("Escape");
    assert(await page.evaluate(id => panes.get(id).socket === original.socket && original.socket.readyState === WebSocket.OPEN,left));
    await openProject("one");
    assert.equal(await page.evaluate(() => selectedTopicId),a);
    await choose(b);
    await ready(right);
    await key(right, "x");
    await page.waitForFunction(id => observePrompt(panes.get(id)).draft && !panes.get(id).view.inputReady, right);
    await key(right, "Control+x"); // 原生剪切清空行，后端未将该快捷键预判为清空操作。
    await ready(right);
    const quietRevision = await page.evaluate(id => panes.get(id).revision, right);
    await key(right, "ArrowLeft"); // 已在行首，夹具不会产生输出帧。
    await page.waitForFunction(id => !panes.get(id).view.inputReady, right);
    await ready(right);
    assert.equal(await page.evaluate(id => panes.get(id).revision, right), quietRevision);
    await key(right, "Control+l");
    await page.waitForFunction(id => {
      const view = panes.get(id).view;
      return view.activity === "idle" && !view.inputReady;
    }, right);
    for (let i = 0; i < 3; i++) {
      const revision = await page.evaluate(id => panes.get(id).revision, right);
      await page.waitForFunction(([id,prev]) => panes.get(id).revision > prev, [right,revision]);
      const state = await (await page.request.get(base+"/api/state")).json();
      const view = state.windows.find(w => w.agent.id === right);
      assert.deepEqual([view.activity, view.inputReady], ["idle", false]);
    }
    await ready(right);
    assert.equal(await page.locator(".pane:visible").count(),1);
    assert(await pane(left).isHidden());
    assert.equal(await page.evaluate(()=>layout.primaryWindowId),null);
    await choose(a);
    assert.equal(await page.evaluate(()=>layout.splitRatio),ratio);
    assert(await page.evaluate(id=>panes.get(id).term===original.term && panes.get(id).socket===original.socket && layout.primaryWindowId===id,left));
    await ready(left);
    const picker = page.locator("#agents-dialog"), search = page.locator("#peer-search");
    await pane(left).locator(".insert-agent").click();
    await picker.waitFor();
    assert(await search.evaluate(el => el === document.activeElement));
    const options = await picker.getByRole("option").evaluateAll(items=>items.map(i=>i.dataset.id));
    const candidates = await page.evaluate(id => snapshot.windows.filter(w=>w.agent.id!==id && w.agent.online).map(w=>w.agent.id),left);
    assert.deepEqual([...options].sort(),candidates.sort());
    assert(options.indexOf(helper)<options.indexOf(right));
    assert.equal(await picker.locator("#peer-"+helper+" strong").innerText(),"@测试");
    assert.equal(await picker.locator("#peer-"+helper+" small").innerText(),"开发");
    assert.equal(await picker.locator("#peer-"+right+" strong").innerText(),"@同名");
    assert.equal(await picker.locator("#peer-"+right+" small").innerText(),"设计");
    await page.waitForFunction(id=>!panes.get(id).view.inputReady,left);
    const held = send("@同名","选择 Agent 时暂缓自动收件");
    await page.waitForFunction(([id,msg])=>panes.get(id).view.inbox.some(m=>m.id===msg && m.state==="pending"),[left,held]);
    await search.press("ArrowDown");
    const selected = await search.getAttribute("aria-activedescendant");
    await page.evaluate(() => refresh());
    assert.equal(await search.getAttribute("aria-activedescendant"),selected);
    await search.press("Escape");
    await picker.waitFor({state:"hidden"});
    await until(()=>state(held)==="writing","automatic delivery claimed after closing picker");
    await page.evaluate(id=>openPeers(panes.get(id)),left);
    // 粘贴、回车与确认已开始，服务端应先完成这一条，再允许浏览器显示选择器。
    await picker.waitFor();
    assert(logLines("one","accepted.txt").some(line=>line.includes(held.slice(0,8))));
    await search.press("Escape");
    await page.waitForFunction(msg=>snapshot.timeline.some(m=>m.id===msg && m.state==="delivered"),held);
    await ready(left);
    await key(left,"@");
    await key(left,"@");
    await page.waitForFunction(id=>observePrompt(panes.get(id)).editableText?.includes("@@"),left);
    assert(!await picker.isVisible());
    await key(left,"Control+u");
    await ready(left);
    await page.locator("#open-settings").click();
    await page.locator("#mention-record").click();
    await page.locator("#mention-record").press("Alt+Shift+F");
    assert.match(await page.locator("#mention-key-feedback").innerText(),/选择文件/);
    await page.locator("#mention-record").press("Alt+Shift+M");
    await page.locator("#mention-save").click();
    await page.waitForFunction(()=>mentionShortcut==="Alt+Shift+M" && [...panes.values()].every(p=>p.element.querySelector(".mention-key").textContent==="(Alt+Shift+M)"));
    await page.locator("#close-settings").click();
    await key(left,"Alt+Shift+A");
    assert(!await picker.isVisible());
    await key(left,"Alt+Shift+M");
    await picker.waitFor();
    await search.fill("设名"); // 跨话题名与别名的子序列。
    assert.equal(await picker.getByRole("option").count(),1);
    await search.dispatchEvent("keydown",{key:"Enter",isComposing:true});
    assert(await picker.isVisible());
    await search.press("Enter");
    await picker.waitFor({state:"hidden"});
    await page.locator("#open-settings").click();
    await page.locator("#mention-reset").click();
    await page.waitForFunction(()=>mentionShortcut==="Alt+Shift+A");
    await page.locator("#close-settings").click();
    await page.waitForFunction(id=>observePrompt(panes.get(id)).editableText?.includes("@设计/同名"),left);
    await key(left,"Control+u");
    await ready(left);
    await page.evaluate(()=>{window.effects=[];});
    const cross = send("@设计/同名","检查跨话题收件");
    await page.waitForFunction(()=>window.effects.some(e=>e.classes==="message-flight" && e.path.includes(" Q ")));
    assert(await page.evaluate(({helper,b})=>{
      const flight=window.effects.find(e=>e.classes==="message-flight"), coords=flight.path.match(/-?\d+(?:\.\d+)?/g).map(Number);
      const from=messageAnchor(helper).getBoundingClientRect(), to=messageAnchor(snapshot.windows.find(w=>w.agent.topicId===b).agent.id).getBoundingClientRect();
      return Math.abs(flight.center[0]-from.x-from.width/2)<2 && Math.abs(flight.center[1]-from.y-from.height/2)<2 &&
        Math.abs(coords.at(-2)-to.x-to.width/2)<2 && Math.abs(coords.at(-1)-to.y-to.height/2)<2;
    },{helper,b}));
    const recipientTab = page.locator('.topic-choice[data-topic="'+b+'"]');
    if (await tab("two").getAttribute("aria-expanded")==="false") await tab("two").click();
    await recipientTab.locator(".tag-inbox").filter({hasText:"1"}).waitFor();
    await choose(b);
    await pane(right).locator(".inbox-caption").filter({hasText:"跨话题 · 开发"}).waitFor();
    const inboxHeight = () => pane(right).locator(".inbox").evaluate(el => el.getBoundingClientRect().height);
    const pendingHeight = await inboxHeight();
    await pane(right).locator(".write-inbox").click();
    await page.waitForFunction(id => panes.get(id).view.inbox.some(i=>i.state==="writing"),right);
    assert.equal(await inboxHeight(),pendingHeight);
    await until(()=>state(cross)==="delivered","Copilot vertical prompt clears");
    assert(logLines("two","accepted.txt").includes("MSG_ID: "+cross.slice(0,8)+" | FROM_AGENT: 开发/测试 | MSG: 检查跨话题收件"));
    await pane(right).locator(".inbox").waitFor({state:"hidden"});
    const ignoredPending = send("@设计/同名","已人工处理的待交接");
    const pendingBar = pane(right).locator(".inbox");
    await pendingBar.waitFor();
    assert.deepEqual(await pendingBar.locator("button").allTextContents(), ["下一条","预览","忽略","立即投递"]);
    const entered = logLines("two","enters.txt").length;
    await pendingBar.getByRole("button", {name:"忽略",exact:true}).click();
    await pendingBar.waitFor({state:"hidden"});
    await page.locator('.timeline-entry[data-message="'+ignoredPending+'"] .log-state').filter({hasText:"已忽略"}).waitFor();
    assert.equal(logLines("two","enters.txt").length, entered);
    const withdrawn = send("@设计/同名","从日志撤回");
    await page.locator('.timeline-entry[data-message="'+withdrawn+'"]').click();
    await page.locator(".mail-detail").getByRole("button",{name:"撤回"}).click();
    await page.locator('.timeline-entry[data-message="'+withdrawn+'"] .log-state').filter({hasText:"已忽略"}).waitFor();
    await page.locator('.timeline-entry[data-message="'+withdrawn+'"]').click();
    await page.locator(".mail-detail").waitFor();
    assert(await page.locator(".mail-detail").getByRole("button",{name:"撤回"}).isHidden());
    await page.keyboard.press("Escape");
    const writing = send("@设计/同名","正在写入");
    sql("UPDATE inbox SET state='writing' WHERE id=?",writing);
    const queued = [send("@设计/同名","保留任务一"),send("@设计/同名","保留任务二")];
    await page.waitForFunction(({right,queued})=>queued.every(id=>panes.get(right).view.inbox.some(m=>m.id===id)),{right,queued});
    for (let i=0;i<3 && await pendingBar.getAttribute("data-state")!=="writing";i++) await pendingBar.locator(".next-inbox").click();
    assert.equal(await pendingBar.getAttribute("data-state"),"writing");
    const batchAck = page.waitForResponse(r=>r.url().endsWith("/inbox/acknowledge-all"));
    await pendingBar.getByRole("button",{name:"全部忽略",exact:true}).click();
    assert.equal((await batchAck).status(),204);
    assert.deepEqual(sql("SELECT state,acknowledged FROM inbox WHERE id=?",writing),[["writing",0]]);
    assert(queued.every(id=>sql("SELECT acknowledged FROM inbox WHERE id=?",id)[0][0]===1));
    sql("UPDATE inbox SET state='delivered' WHERE id=?",writing);
    assert.equal((await page.request.post(base+"/api/windows/"+right+"/input", {data:{messageId:ignoredPending}})).status(),409);
    await page.waitForFunction(()=>window.effects.some(e=>e.frames[0]?.boxShadow));
    await page.locator('.timeline-entry[data-message="'+cross+'"]').click();
    await page.locator(".mail-detail").filter({hasText:"检查跨话题收件"}).waitFor();
    await page.keyboard.press("Escape");
    await recipientTab.hover();
    await recipientTab.getByRole("button",{name:"归档",exact:true}).click();
    await page.waitForFunction(id=>snapshot.topics.find(t=>t.id===id)?.status==="finished",b);
    assert(await page.locator("#new-agent").isHidden());
    await recipientTab.hover();
    await recipientTab.getByRole("button",{name:"重开",exact:true}).click();
    await choose(a);
    await page.emulateMedia({reducedMotion:"reduce"});
    await page.waitForFunction(() => !document.querySelector(".message-flight"));
    await page.evaluate(()=>{window.effects=[];});
    const reduced = send("@设计/同名","减少动态效果");
    await page.waitForFunction(id=>snapshot.windows.some(w=>w.inbox.some(m=>m.id===id)),reduced);
    // 收件角标先出现，目标项目卡会在信封落地时才高亮。
    await page.waitForFunction(() => window.effects.some(e => e.classes === "message-flight") &&
      window.effects.some(e => /project-button|tree-agent|topic-choice|topic-agent-tag/.test(e.classes) && e.frames[0]?.backgroundColor));
    await page.emulateMedia({reducedMotion:"no-preference"});
    assert(await page.evaluate(()=>window.sizes.length>0 && window.sizes.every(([replay,width,visibility,hidden])=>
      !replay && width>=240 && visibility==="visible" && !hidden)));
    await choose(b);
    await pane(right).locator(".auto-receive input").check();
    await page.evaluate(id=>{window.backgroundSocket=panes.get(id).socket;},right);
    await choose(a);
    const background = send("@设计/同名","后台项目自动接收");
    await until(()=>state(background)==="delivered","automatic delivery while project is hidden",22000);
    assert.deepEqual(sql("SELECT state,acknowledged FROM inbox WHERE id=?",ignoredPending), [["pending",1]]);
    assert(!logLines("two","accepted.txt").some(line=>line.includes(ignoredPending.slice(0,8))));
    assert(await page.evaluate(({id,a})=>selectedTopicId===a && panes.get(id).element.hidden &&
      panes.get(id).socket === window.backgroundSocket && window.backgroundSocket.readyState===WebSocket.OPEN,{id:right,a}));
    await recipientTab.locator(".tag-inbox").waitFor({state:"hidden"});
    const resizeGuard=await page.evaluate(id=>{
      const p=panes.get(id), count=window.sizes.length, old=p.element.style.width;
      p.element.style.width="220px"; p.sentSize=undefined; fitPane(p);
      p.element.style.width=old;
      Object.defineProperty(document,"visibilityState",{configurable:true,get:()=>"hidden"});
      try { fitPane(p); } finally { delete document.visibilityState; }
      return window.sizes.length===count;
    },left);
    assert(resizeGuard);
    await page.locator("#project-collapse").click();
    assert.equal(await page.locator("#topic-shell").getAttribute("data-collapsed"),"true");
    await fold(helper);
    await page.setViewportSize({width:750,height:920});
    await openProject("two");
    await page.locator("#project-rail").waitFor({state:"hidden"});
    await page.locator("#mobile-topic").selectOption(b);
    // 上一期允许同时保存“主对话”和“折叠”，刷新后应自动恢复成可见的主对话。
    await page.evaluate(({a,left}) => {
      const key = layoutKey + "." + a;
      const old = JSON.parse(localStorage.getItem(key));
      old.collapsedIds.push(left);
      localStorage.setItem(key, JSON.stringify(old));
    }, {a,left});
    await page.reload();
    await page.waitForFunction(id=>window.selectedTopicId===id && !!layout,b);
    await openProject("one");
    await page.locator("#mobile-topic").selectOption(a);
    await page.setViewportSize({width:1460,height:940});
    assert.equal(await page.evaluate(()=>layout.primaryWindowId),left);
    assert(await pane(left).evaluate(el => el.parentElement.id === "terminal-parking" && layout.collapsedIds.includes(layout.primaryWindowId)));
    await fold(left);
    assert.equal(await pane(helper).getAttribute("data-collapsed"), "true");
    await fold(helper);
    console.log("PASS topic creation, scoped layouts, preserved terminals, @ picker, qualified receipt, narrow view and reopen");

    await ready(left);
    await page.evaluate(id=>{
      const socket=panes.get(id).socket, send=socket.send.bind(socket);
      window.browserReports=[];
      socket.send=data=>{
        if (data instanceof Uint8Array && new TextDecoder().decode(data).match(replayReports)) window.browserReports.push(data.length);
        send(data);
      };
    },left);
    await key(left,"Control+g");
    await sleep(600);
    const reports = logLines("one","reports.txt").length;
    assert.deepEqual(await page.evaluate(()=>window.browserReports),[]);
    await page.evaluate(id=>{
      const osc="\x1b]10;rgb:dcdc/e6e6/eeee\x1b\\";
      panes.get(id).socket.send(bytes.encode(osc.repeat(3)));
      panes.get(id).socket.send(bytes.encode(osc+"中文"+osc));
    },left);
    await page.waitForFunction(id=>observePrompt(panes.get(id)).editableText?.includes("中文"),left);
    assert.equal(logLines("one","reports.txt").length,reports);
    await key(left,"Control+u");
    await page.reload();
    await ready(left);
    assert.equal(logLines("one","reports.txt").length,reports);
    assert.equal(await page.evaluate(()=>("\x1b[44;3R\x1b[?1;2c\x1b]10;rgb:aaaa/bbbb/cccc\x1b\\keep").replace(replayReports,"")),"keep");
    console.log("PASS frontend/backend live and replay report suppression including mixed UTF-8 input");

    const enterBaseline = logLines("one","enters.txt").length;
    const acceptedBaseline = logLines("one","accepted.txt").length;
    mode("retry");
    assert(await pane(left).locator(".auto-receive input").isChecked());
    await key(left,"x");
    await page.waitForFunction(id=>observePrompt(panes.get(id)).draft,left);
    const msg = send("@同名","交接路径 @index.html");
    await sleep(1800);
    assert.equal(state(msg),"pending");
    // Withhold the first empty report for each revision. Only polling a stable, unchanged frame can resume delivery.
    await page.evaluate(id=>{
      const socket=panes.get(id).socket, originalSend=socket.send.bind(socket);
      let seen;
      window.stableReported=false;
      socket.send=data=>{
        if (typeof data==="string") {
          const r=JSON.parse(data);
          if (r.type==="prompt" && r.empty && !window.stableReported) {
            if (seen!==r.revision) { seen=r.revision; return; }
            window.stableReported=true;
          }
        }
        originalSend(data);
      };
    },left);
    await key(left,"Control+u");
    await until(()=>state(msg)==="delivered","single follow-up Enter on multiline draft");
    assert(await page.evaluate(()=>window.stableReported));
    assert.deepEqual(logLines("one","enters.txt").slice(enterBaseline),["retry","retry"]);
    assert.equal(logLines("one","accepted.txt").length,acceptedBaseline+1);
    assert(logLines("one","accepted.txt")[acceptedBaseline].includes("交接路径 @index.html"));
    const draft = await page.evaluate(() => {
      const read = lines => observePrompt({agent:{engine:"codex"},term:{rows:lines.length,
        modes:{bracketedPasteMode:true},buffer:{active:{baseY:0,cursorX:1,cursorY:lines.length-1,
          getLine:y=>({translateToString:()=>lines[y]})}}}});
      return [read(["› [来自 @测试] 第一段","",""," 第二段"]).editableText,
        ...["────────","• 历史输出","gpt-6 high · 40% context left"].map(separator =>
          read(["› [来自 @测试] 历史消息",separator,""," 第二段"]).draft)];
    });
    assert.match(draft[0], /第一段.*第二段/);
    assert.deepEqual(draft.slice(1),[false,false,false]);
    const repaint = await page.evaluate(() => {
      const lines = ["› MSG_ID: 2803883a | MSG: 上一轮", "  历史消息的第二行", "",
        "› MSG_ID: ff3da04e | MSG: 当前交接", "  当前交接的结尾", "  GPT-6 high · 项目"];
      return [1, 4].map(cursorY => readNativePrompt({agent:{engine:"codex"},
        term:{rows:lines.length, modes:{bracketedPasteMode:true}, buffer:{active:{baseY:0, cursorY, cursorX:8,
          getLine:y => ({translateToString:() => lines[y]})}}}}).editableText);
    });
    assert.equal(repaint[0], null);
    assert.match(repaint[1], /MSG_ID: ff3da04e/);
    const steering = await page.evaluate(async id => {
      const Terminal = panes.get(id).term.constructor;
      async function read(input, status = [], atStart = true, cols = 110, working = "◦ Working (3s • esc to interrupt)") {
        const term = new Terminal({allowProposedApi:true,cols,rows:12});
        const lines = [working, ...status, "› " + input, "",
          "  gpt-5.1-codex default · project", atStart ? "  ? for shortcuts" : "  tab to queue message"];
        const caret = atStart ? 3 : 35;
        await new Promise(done=>term.write("\x1b[?2004h"+lines.join("\r\n")+"\x1b["+ (status.length+1+Math.ceil(working.length/cols)) +";"+caret+"H",done));
        try { return readNativePrompt({agent:{engine:"codex"},term}); } finally { term.dispose(); }
      }
      const hint = "\x1b[2mAsk Codex to do anything\x1b[22m";
      return [await read(hint), await read("Ask Codex to do anything"),
        await read("MSG_ID: 1234abcd | MSG: message",[],false), await read(hint,["Approval request"]),
        await read(hint,["• Queued follow-up inputs","  ↳ MSG_ID: 1234abcd | MSG: message"]),
        ...await Promise.all([46,57,66,120].map(cols=>read(hint,[],true,cols,
          "Reading code (12m 39s • esc to interrupt) · 1 background terminal running · /ps to view · /stop to close")))];
    },left);
    assert.equal(steering[0].busyInput,true);
    assert.equal(steering[1].busyInput,false);
    assert.equal(steering[2].busyEditable,true);
    assert.equal(steering[3].busyInput,false);
    assert.equal(steering[4].queuedId,"1234abcd");
    assert(steering.slice(5).every(frame=>frame.busyInput));
    const narrowFrames = await page.evaluate(() => {
      const clock = Date.now;
      let time = 10000, lines;
      const buffer = {baseY:0, cursorY:2, cursorX:2, getLine:i => ({translateToString:() => lines[i]})};
      const p = {agent:{engine:"codex"},term:{cols:32,rows:3,modes:{bracketedPasteMode:true},buffer:{active:buffer}}};
      const frame = (line, input = "› ") => {
        lines = [line, "", input];
        const prompt = observePrompt(p);
        return [prompt.screen,prompt.empty,prompt.clear];
      };
      try {
        Date.now = () => time;
        const states = ["◦ Working (12s • esc to …", "• Working (5m 04s · es…", "  └ Tip: Use /…"].map(line => frame(line));
        time += 2999;
        states.push(frame(""));
        time++;
        states.push(frame(""));
        frame("• Working (1h 5m 04s • …");
        states.push(frame("Approval request"), frame(""));
        frame("• Working (12s • …");
        states.push(frame("", "› 草稿 (12s •"), frame(""));
        return states;
      } finally { Date.now = clock; }
    });
    assert.deepEqual(narrowFrames.slice(0,4), Array(4).fill(["running",false,false]));
    assert.deepEqual(narrowFrames.slice(4).map(f => f[0]), ["clear","blocked","clear","draft","clear"]);
    const afterCompletion = await page.evaluate(() => {
      const frame = (history, current) => {
        const lines = [history, "─ Worked for 6m 45s ───", current, "› "];
        return readNativePrompt({agent:{engine:"codex"}, term:{rows:lines.length, modes:{bracketedPasteMode:true},
          buffer:{active:{baseY:0, cursorX:2, cursorY:3, getLine:i=>({translateToString:()=>lines[i]})}}}}).screen;
      };
      return [frame("◦ Working (esc to interrupt)", ""), frame("Approval request", ""),
        frame("", "• Working (5m 04s · es…"), frame("", "Approval request")];
    });
    assert.deepEqual(afterCompletion, ["clear","clear","running","blocked"]);
    const adapterScreens = await page.evaluate(() => {
      function read(engine, lines, cursorY = lines.length - 1) {
        const prompt = readNativePrompt({agent:{engine}, term:{cols:80, rows:lines.length, modes:{bracketedPasteMode:true},
          buffer:{active:{baseY:0,cursorX:2,cursorY,getLine:i=>({translateToString:()=>lines[i]})}}}});
        return [prompt.screen, prompt.empty];
      }
      return [
        read("qwen", ["╭────────╮","│ > Type your message or @path/to/file","╰────────╯"],1),
        read("qwen", ["⠋ Thinking (12s · esc to cancel)","╭────────╮","│ >","╰────────╯"],2),
        read("qwen", ["等待确认","按 Enter 确认，Esc 取消"]),
        read("qwen", ["│ > Working. Escape to cancel."]),
        read("qwen", ["──────────────────",">   输入您的消息或 @ 文件路径","──────────────────","  ➜ project · fixture-model","  自动模式 (按 Tab 切换)"],1),
        read("qwen", ["Attachments: [cat.png]","────────","> ","────────","↑ to manage attachments","plan mode (tab to cycle)"],2),
        read("qwen", ["────────","> ","────────","0% used"],1),
        read("qwen", ["Assistant: press Enter to confirm when done.","────────","> ","────────","? for shortcuts"],2),
        read("qwen", ["⠏ 等待用户确认...","────────","> ","────────","? for shortcuts"],2),
        read("qwen", ["────────","> /approval-mode","────────","/approval-mode  View or change the approval mode"],1),
        read("qwen", ["────────","* ","────────","YOLO mode (tab to cycle)"],1),
        read("qwen", ["────────","! ","────────","shell mode"],1),
        read("codebuddy", ["Do you trust the files in this folder?","❯ 1. Trust folder only","Enter to confirm • Esc to exit"]),
        read("codebuddy", ["∴ Thinking...","────────","❯","────────","? for shortcuts"],2),
        read("codebuddy", ["────────","❯ esc to interrupt","────────","? for shortcuts"],1),
        read("zcode", ["╰────────╯","⠙ esc to interrupt          7%"]),
        read("zcode", ["╰────────╯","                             7%"]),
        read("claude", ["✻ Cooked for 34s","────────","❯","────────","? for shortcuts"],2),
        read("claude", ["✻ Thinking…","────────","❯","────────","? for shortcuts"],2),
        read("traecli", ["◦ Working (12s • esc to interrupt)","› "]),
      ];
    });
    assert.deepEqual(adapterScreens, [
      ["idle",false],["active",false],["blocked",false],["idle",false],["clear",true],["draft",false],["clear",true],
      ["clear",true],["blocked",false],["draft",false],["clear",true],["idle",false],
      ["blocked",false],["running",false],["draft",false],["active",false],["idle",false],
      ["clear",true],["running",false],["running",false],
    ]);
    const thick=await page.evaluate(()=>{
      function read(lines){
        return observePrompt({agent:{engine:"copilot"},term:{rows:lines.length,cols:40,modes:{bracketedPasteMode:true},
          buffer:{active:{baseY:0,cursorX:2,cursorY:1,getLine:i=>({translateToString:()=>lines[i]})}}}});
      }
      return [read(["╻▄▄▄▄","┃","╹▀▀▀▀","Autopilot · Allow All · Shift+Tab"]).empty,
        read(["╻▄▄▄▄","┃ MSG_ID: 0000","┃ abcd | MSG: 文档","╹▀▀▀▀","Shift+Tab"]).draftText,
        read(["╻▄▄▄▄","┃","错边框","Shift+Tab"]).empty];
    });
    assert.equal(thick[0],true);
    assert(thick[1].replace(/\s/g,"").includes("MSG_ID:0000abcd"));
    assert.equal(thick[2],false);
    const asciiPrompts = await page.evaluate(() => {
      function empty(lines, bracketedPasteMode=true) {
        const row = lines.findIndex(line => line.startsWith(">"));
        return readNativePrompt({agent:{engine:"copilot"}, term:{rows:lines.length, cols:60, modes:{bracketedPasteMode},
          buffer:{active:{baseY:0, cursorX:2, cursorY:row, getLine:i=>({translateToString:()=>lines[i]})}}}}).empty;
      }
      return [
        empty(["Session: 24.00 AIC used","───","> ","───","model info"]),
        empty(["───","> ","───","",""]),
        empty(["───","> 草稿","───"]),
        empty(["───","> ","缺少下边框"]),
        empty(["Approval request","───","> ","───"]),
        empty(["Working (esc to interrupt)","───","> ","───"]),
        empty(["───","> ","───","unknown footer"]),
        empty(["───","> ","───"], false)
      ];
    });
    assert.deepEqual(asciiPrompts,[true,true,false,false,false,false,false,false]);

    for (const [behavior, expected] of [["submit",1],["silent",2],["delayed",1],["foreign",1],["afterkey",1],["newtext",1],["running",1],["approval",1],["hold",2],["typing",1],["ticker-retry",2]]) {
      const before=logLines("one","enters.txt").length;
      mode(behavior==="typing" ? "hold" : behavior);
      await ready(left);
      const id=send("@同名","审核 "+behavior);
      await until(()=>logLines("one","enters.txt").length>before,behavior+" first enter");
      if (behavior==="typing" || behavior==="newtext") await key(left,"z");
      if (behavior==="afterkey") await key(left,"Control+u");
      if (behavior==="approval") {
        await page.waitForFunction(id=>panes.get(id).view.activity==="waiting",left);
        assert.equal(await page.evaluate(()=>selectedTopic().activity),"waiting");
      }
      const submitted = ["submit","silent","delayed","foreign","afterkey","newtext","running","ticker-retry"].includes(behavior);
      await until(()=>state(id)===(submitted?"delivered":"delivery_uncertain"),behavior+" result");
      assert.equal(logLines("one","enters.txt").length-before,expected,behavior);
      if (!submitted || ["foreign","newtext","running"].includes(behavior)) await key(left,"Control+u");
    }
    await checkFoldedDelivery(page,left);
    const stale="已发送回车，未观察到原生输入行清空，请核对终端";
    await key(left,"x");
    await page.waitForFunction(id=>!!panes.get(id).draftRevision,left);
    await page.evaluate(({id,stale})=>feedback(panes.get(id).element.querySelector(".pane-error"),stale),{id:left,stale});
    await key(left,"Control+u");
    await pane(left).locator(".pane-error").waitFor({state:"hidden"});
    // An empty inbox also clears a stale submit warning. Other terminal errors stay visible.
    const beforeEnter = "终端仍在重绘粘贴内容，尚未发送回车，请核对终端";
    for (const [message,expected] of [[stale,""],[beforeEnter,""],["终端连接异常","终端连接异常"]]) {
      const actual=await page.evaluate(({id,message})=>{
        const p=panes.get(id), error=p.element.querySelector(".pane-error");
        feedback(error,message);
        updatePane(p,p.view);
        return error.textContent;
      },{id:right,message});
      assert.equal(actual,expected);
    }
    const ignored = await page.evaluate(({id, beforeEnter}) => {
      const p = panes.get(id);
      p.receiptErrorId = p.message.id;
      feedback(p.element.querySelector(".pane-error"), beforeEnter);
      return p.message.id;
    }, {id:left,beforeEnter});
    const entersBeforeIgnore = logLines("one","enters.txt").length;
    const batch = sql("SELECT id FROM inbox WHERE target=? AND state='delivery_uncertain' AND acknowledged=0",left).flat();
    assert(batch.length > 1 && batch.includes(ignored));
    await page.waitForFunction(({left,batch}) => batch.every(id=>panes.get(left).view.inbox.some(m=>m.id===id && canIgnore(m))),{left,batch});
    await pane(left).getByRole("button", {name:"全部忽略",exact:true}).click();
    await pane(left).locator(".pane-error").waitFor({state:"hidden"});
    await page.waitForFunction(({left,batch}) => !panes.get(left).view.inbox.some(m=>batch.includes(m.id)),{left,batch});
    const acknowledged = sql("SELECT id FROM inbox WHERE target=? AND state='delivery_uncertain' AND acknowledged=1",left).flat();
    assert(batch.every(id=>acknowledged.includes(id)));
    await page.locator('.timeline-entry[data-message="'+ignored+'"] .log-state').filter({hasText:"已忽略"}).waitFor();
    assert.equal(logLines("one","enters.txt").length, entersBeforeIgnore);
    const finished=mcp("finish_topic",{summary:"验收完成"});
    assert.equal(finished.id,a);
    assert.equal(finished.summary,"验收完成");
    await page.waitForFunction(()=>selectedTopic().activity==="finished");
    assert(await page.evaluate(()=>window.effects.every(e=>e.duration<=400 || (e.classes==="message-flick-hand" && e.duration===760) || (e.classes==="message-flight" && e.duration>=1050 && e.duration<=1850))));
    const log = page.locator("#topic-timeline");
    const row = log.locator(".timeline-entry").first();
    await page.waitForFunction(() => document.querySelectorAll(".timeline-entry").length > 6);
    assert.equal(await log.evaluate(el => el.clientHeight / el.firstElementChild.offsetHeight), 5);
    assert(await log.evaluate(el => el.getBoundingClientRect().top >= byId("workspace-layout").getBoundingClientRect().bottom));
    assert.match(await row.locator(".log-time").textContent(), /^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}$/);
    await log.evaluate(el => { el.scrollTop = el.scrollHeight; });
    const newest = send("@设计/同名","接口文档已更新，请核对字段");
    await page.waitForFunction(id => {
      const el = byId("topic-timeline");
      return el.querySelector(".timeline-entry")?.dataset.message === id && el.scrollTop <= 1;
    }, newest);
    await row.click();
    await page.locator(".mail-detail").filter({hasText:"接口文档已更新"}).waitFor();
    await page.locator("#inspector-panel-action").click();
    await page.locator("#inspector-shell").waitFor({state:"hidden"});
    await page.locator("#log-divider").focus();
    await page.keyboard.press("ArrowUp");
    assert.equal(await log.evaluate(el => el.clientHeight / el.firstElementChild.offsetHeight), 6);
    await page.keyboard.press("ArrowDown");
    await page.locator("#log-toggle").click();
    assert(await log.isHidden() && await page.locator("#log-latest").isVisible());
    await page.locator("#log-toggle").click();
    assert(await log.evaluate(el => el.scrollTop <= 1));
    await page.setViewportSize({width:390,height:780});
    await log.waitFor({state:"hidden"});
    assert(await page.evaluate(() => document.documentElement.scrollWidth === innerWidth));
    await page.setViewportSize({width:1000,height:760});
    sql("WITH n(i) AS (VALUES(1) UNION ALL SELECT i+1 FROM n WHERE i<8) INSERT INTO windows(id,name,engine,project,topic_id) SELECT lower(hex(randomblob(16))),'前端协作窗口编号'||i,'codex',?,? FROM n", path.join(root,"one"), a);
    await page.waitForFunction(() => document.querySelectorAll('#topic-list .topic-agent-tag').length >= 10);
    const overflowId = await page.locator('#topic-list .topic-agent-tag').last().getAttribute("data-agent");
    await page.locator('#topic-list .topic-agent-tag[data-agent="' + overflowId + '"] .member-name').click();
    await page.waitForFunction(id => layout.activeWindowId === id, overflowId);
    await page.setViewportSize({width:390,height:780});
    assert(await page.evaluate(() => document.documentElement.scrollWidth === innerWidth));
    const footer = pane(overflowId).locator(".terminal-toolbar");
    const more = footer.locator(".pane-more"), menu = footer.locator(".pane-more-menu");
    await more.waitFor();
    assert(await footer.evaluate(el => {
      const strip = el.querySelector(".terminal-actions");
      const rows = Array.from(strip.children).filter(item => item.offsetWidth && item.matches("button, label"))
        .map(item => item.getBoundingClientRect().top);
      return Math.max(...rows) - Math.min(...rows) < 2 && strip.scrollWidth <= strip.clientWidth;
    }));
    assert(await pane(overflowId).locator(".pane-header .tile-name").isVisible());
    assert(await page.evaluate(() => getComputedStyle(byId("panes")).gridTemplateColumns.split(" ").length === 1 &&
      byId("pane-divider").hidden && byId("primary-panes").hidden));
    await more.click();
    assert(await menu.locator(".close-pane").isVisible());
    await page.keyboard.press("Escape");
    assert(await menu.isHidden());
    // 独立测量比手机屏宽更窄的分栏，确保被收纳的开关仍操作该窗格。
    await pane(overflowId).evaluate(el => { el.style.width = "180px"; });
    await more.click();
    const receiveOverflow = menu.locator(".auto-receive input");
    const previousReceive = await receiveOverflow.isChecked();
    await receiveOverflow.click();
    await page.waitForFunction(([id,previous]) => panes.get(id).view.agent.autoReceive !== previous, [overflowId,previousReceive]);
    assert(await menu.isHidden());
    await pane(overflowId).locator(".pane-header .rename-agent").click();
    await page.locator("#pane-session-dialog[open] #rename-name").waitFor();
    await page.keyboard.press("Escape");
    await pane(overflowId).evaluate(el => { el.style.removeProperty("width"); });
    await page.mouse.move(0,0); // 检查默认轨道，排除指针恰好悬停的高亮状态。
    const scrollStyles = await page.evaluate(() => {
      const outer = document.querySelector(".collaboration-scroll");
      const inner = document.querySelector(".xterm-viewport");
      return [inner, outer].map(el => [getComputedStyle(el, "::-webkit-scrollbar").width,
        getComputedStyle(el, "::-webkit-scrollbar-thumb").backgroundColor]);
    });
    assert.deepEqual(scrollStyles, [["6px", "rgba(255, 255, 255, 0.24)"], ["6px", "rgba(255, 255, 255, 0.16)"]]);
    await page.setViewportSize({width:719,height:780});
    await footer.locator(".terminal-actions > .set-primary").waitFor();
    assert.equal(await menu.locator("button,label").count(), 1);
    assert(await menu.isHidden());
    assert(await footer.evaluate(el => {
      const auto = el.querySelector(".auto-receive").getBoundingClientRect();
      const sessions = el.querySelector(".pane-sessions").getBoundingClientRect();
      const last = el.querySelector(".pane-more").getBoundingClientRect();
      return sessions.left > auto.right + 20 && Math.abs(el.getBoundingClientRect().right - last.right) < 12;
    }));
    await page.setViewportSize({width:1000,height:760});
    const shortTopic = (await post("/topics", {name:"短", project:path.join(root,"one")})).id;
    let lastId;
    for (let i = 0; i < 12; i++)
      lastId = (await post("/topics", {name:"导航回归 " + i + " 长话题名称", project:path.join(root,"one")})).id;
    await page.waitForFunction(id=>snapshot.topics.some(t=>t.id===id),lastId);
    await choose(shortTopic);
    assert.equal(await page.locator(".current-topic-title").textContent(),"短");
    await page.setViewportSize({width:690,height:760});
    await page.locator("#mobile-topic").selectOption(lastId);
    await page.waitForFunction(id=>selectedTopicId===id,lastId);
    await page.setViewportSize({width:1180,height:760});
    if (!await page.locator("#project-rail").isVisible()) await page.locator("#project-collapse").click();
    const card=page.locator("#current-project");
    assert(await page.locator(".project-states").evaluate(el=>{
      const cells=[...el.querySelectorAll("span")], all=snapshot.windows.filter(w=>snapshot.projects.includes(snapshot.topics.find(t=>t.id===w.agent.topicId)?.project));
      return cells.length===3 && cells.every((cell,i)=>Number(cell.querySelector("b").textContent)===all.filter(w=>i===0?["working","waiting"].includes(w.activity):w.activity===(i===1?"idle":"offline")).length) &&
        cells.every(c=>c.offsetTop===cells[0].offsetTop && getComputedStyle(c,"::before").animationName==="none") &&
        new Set(cells.map(c=>getComputedStyle(c,"::before").backgroundColor)).size===3;
    }));
    const claudeTopic = await post("/topics",{name:"Claude 实测",project:path.join(root,"third")});
    const claude = await post("/windows",{name:"Claude审阅",engine:"claude",topic:claudeTopic.id,newSession:true});
    await choose(claudeTopic.id);
    await ready(claude.id);
    const receipt = send("@Claude 实测/Claude审阅","验证 Claude 收件");
    await page.waitForFunction(id=>panes.get(id)?.view.inputReady,claude.id);
    await page.waitForFunction(id=>snapshot.timeline.some(m=>m.id===id && m.state==="delivered"),receipt);
    assert(fs.readFileSync(path.join(root,"third","accepted.txt"),"utf8").includes(receipt.slice(0,8)));
    const sequential = [send("@Claude 实测/Claude审阅","连续投递一"), send("@Claude 实测/Claude审阅","连续投递二")];
    await until(() => sequential.every(id => state(id) === "delivered"), "wake drains both queued messages", 24000);
    const accepted = logLines("third","accepted.txt").filter(line => sequential.some(id => line.includes(id.slice(0,8))));
    assert.equal(accepted.length, 2);
    sequential.forEach((id,i) => assert(accepted[i].includes(id.slice(0,8))));
    await page.locator("#open-settings").click();
    await page.locator("#log-count").filter({hasText:"条协作日志记录"}).waitFor();
    for (const days of [30,15,7]) {
      const response = page.waitForResponse(r=>r.url().endsWith("/settings/logs/cleanup") && r.request().postDataJSON().days===days);
      await page.locator('#log-cleanup [data-days="'+days+'"]').click();
      assert.equal((await response).status(),200);
    }
    const logCountBefore = await page.locator("#log-count").textContent();
    page.once("dialog", dialog=>dialog.dismiss());
    await page.locator('#log-cleanup [data-days="0"]').click();
    assert.equal(await page.locator("#log-count").textContent(),logCountBefore);
    page.once("dialog", dialog=>dialog.accept());
    await page.locator('#log-cleanup [data-days="0"]').click();
    await page.locator("#log-count").filter({hasText:"共 0 条"}).waitFor();
    await page.locator("#cleanup-result").filter({hasText:"已清理"}).waitFor();
    await page.keyboard.press("Escape");
    await page.waitForFunction(()=>snapshot.timeline.length===0);
    console.log("PASS timeline withdrawal and all four cleanup ranges with full-clean confirmation");
    await page.evaluate(id=>{window.liveProjectSocket=panes.get(id).socket;},claude.id);
    await card.hover();
    page.once("dialog", dialog=>dialog.dismiss());
    await page.getByRole("button",{name:"移除当前项目"}).click();
    assert.equal(await card.locator("strong").getAttribute("title"),path.join(root,"third"));
    page.once("dialog", dialog=>dialog.accept());
    await page.getByRole("button",{name:"移除当前项目"}).click();
    await page.waitForFunction(path=>!snapshot.projects.includes(path),path.join(root,"third"));
    await page.locator("#add-project").click();
    await pickFolder(path.join(root,"third"));
    await page.waitForFunction(id=>selectedTopicId===id,claudeTopic.id);
    assert(await page.evaluate(id=>panes.get(id).socket===window.liveProjectSocket && window.liveProjectSocket.readyState===WebSocket.OPEN,claude.id));
    console.log("PASS empty startup, manual project add, remove and reopen with same topic and live terminal");
    console.log("PASS settings, three-row project cards with static working/idle/offline lights, Claude ConPTY delivery");
    const terminal = pane(claude.id);
    async function toolbarAction(selector) {
      const button = terminal.locator(selector);
      if (!await button.isVisible()) await terminal.locator(".pane-more").click();
      await button.click();
    }
    const meta = await page.evaluate(id => {
      const {online, ...configuration} = panes.get(id).agent;
      return configuration;
    }, claude.id);
    const power = terminal.locator(".pane-header .pane-power");
    assert(await power.evaluate(button => button.nextElementSibling.matches(".agent-light") &&
      button.getAttribute("aria-pressed") === "true" && getComputedStyle(button).color === "rgb(54, 229, 121)"));
    await power.click();
    const launcher = terminal.locator(".start-pane");
    await launcher.waitFor();
    assert.equal(await power.getAttribute("aria-pressed"),"false");
    assert(await power.isHidden());
    assert(await launcher.evaluate(button => button.textContent.trim() === "" &&
      !!button.querySelector("svg") && getComputedStyle(button).color === getComputedStyle(button.closest(".pane").querySelector(".pane-power")).color));
    assert(await page.evaluate(({meta,id}) => {
      const runtime = panes.get(id);
      return !runtime.agent.online && Object.entries(meta).every(([k,v]) => runtime.agent[k] === v);
    }, {meta,id:claude.id}));
    const startRoute = "**/api/windows/" + claude.id + "/start";
    await page.route(startRoute, route => route.fulfill({status:503, json:{error:"模拟启动失败"}}), {times:1});
    await launcher.click();
    await terminal.getByRole("alert").filter({hasText:"模拟启动失败"}).waitFor();
    await terminal.getByRole("button", {name:"打开终端",exact:true}).waitFor();
    assert(await launcher.isEnabled() && await power.isHidden());
    let releaseStart;
    const holdStart = new Promise(resolve => { releaseStart = resolve; });
    await page.route(startRoute, async route => { await holdStart; await route.continue(); }, {times:1});
    await launcher.click();
    await terminal.getByRole("status").filter({hasText:"正在启动"}).waitFor();
    assert(await launcher.isDisabled() && await power.isHidden());
    const origin = await launcher.boundingBox();
    releaseStart();
    await page.waitForFunction(id => panes.get(id)?.agent.online && panes.get(id).socket?.readyState === WebSocket.OPEN, claude.id);
    await power.waitFor();
    const flight = await page.evaluate(() => window.effects.filter(e => e.classes.includes("pane-power")).at(-1));
    const delta = await page.evaluate(frame => { const m = new DOMMatrix(frame.transform); return [m.e,m.f]; }, flight.frames[0]);
    assert(Math.abs(flight.center[0] + delta[0] - origin.x - origin.width/2) < 2);
    assert(Math.abs(flight.center[1] + delta[1] - origin.y - origin.height/2) < 2);
    assert(flight.frames.some(frame => frame.transform === "scale(1.22)"));
    await power.evaluate(button => Promise.all(button.getAnimations().map(animation => animation.finished)));
    assert(await launcher.isHidden() && await power.isEnabled());
    assert.equal(await power.evaluate(button => getComputedStyle(button).color), "rgb(54, 229, 121)");
    assert(await page.evaluate(({id,meta}) => Object.entries(meta).every(([k,v]) => panes.get(id).agent[k] === v), {id:claude.id,meta}));
    await terminal.locator(".toggle-collapse").click();
    const tagPower=tag(claude.id).locator(".tag-power");
    await tagPower.click(); await page.waitForFunction(id=>!panes.get(id).agent.online,claude.id);
    assert(await tagPower.isVisible() && await launcher.isHidden());
    await tagPower.click();await page.waitForFunction(id=>panes.get(id).agent.online && !panes.get(id).presentation.powerAction,claude.id);
    assert.equal(await terminal.getAttribute("data-collapsed"),"true"); await fold(claude.id);
    const wakeUrl = base + "/api/windows/" + claude.id + "/auto-receive";
    assert((await page.request.put(wakeUrl,{data:{enabled:false}})).ok());
    await post("/windows/" + claude.id + "/stop",{});
    await page.waitForFunction(id=>!panes.get(id).agent.online,claude.id);
    const heldOffline=send(claude.id,"关机，保留收件");
    await page.waitForFunction(({id,msg})=>panes.get(id).view.inbox.some(i=>i.id===msg),{id:claude.id,msg:heldOffline});
    assert.equal(sql("SELECT wake_requested FROM inbox WHERE id=?",heldOffline)[0][0],0);
    assert.equal(state(heldOffline),"pending");
    assert((await page.request.put(wakeUrl,{data:{enabled:true}})).ok());
    const firstWake=send(claude.id,"唤醒同一会话 1"), nextWake=send(claude.id,"唤醒同一会话 2");
    await page.waitForFunction(id=>panes.get(id).agent.online && panes.get(id).socket?.readyState===WebSocket.OPEN,claude.id);
    const wakeGeneration = await page.evaluate(id=>panes.get(id).generation,claude.id);
    await until(()=>[heldOffline,firstWake,nextWake].every(msg=>state(msg)==="delivered"),"offline native wake and FIFO",30000);
    assert(await page.evaluate(({id,gen,meta})=>panes.get(id).generation===gen &&
      Object.entries(meta).every(([k,v])=>panes.get(id).agent[k]===v),{id:claude.id,gen:wakeGeneration,meta}));
    console.log("PASS offline delivery auto-wakes the same Agent and session once, opt-out stays offline");
    await toolbarAction(".close-pane");
    await page.waitForFunction(id => !panes.has(id) && !snapshot.windows.some(w => w.agent.id === id), claude.id);
    const inactive = page.locator(".project-button").filter({has:page.locator('[aria-label="移除项目"]')}).first();
    await inactive.hover();
    const inactivePath = await inactive.locator("strong").getAttribute("title");
    page.once("dialog", dialog => dialog.accept());
    await inactive.getByRole("button",{name:"移除项目",exact:true}).click();
    await page.waitForFunction(path => !snapshot.projects.includes(path), inactivePath);
    assert.equal(await card.locator("strong").getAttribute("title"),path.join(root,"third"));
    console.log("PASS stop/reopen retains agent metadata, remove disposes runtime, inactive project removal keeps selection");
    assert.deepEqual(errors,[]);
    console.log("PASS pending-to-idle without new VT, multiline retry once, first-submit success, approval, typing, retry cap and stale error clearing");
    const peerProject = path.join(root, "reply-peer");
    fs.mkdirSync(peerProject);
    const peerTopic = await post("/topics", {name:"回传验证", project:peerProject});
    const replyPeer = await post("/windows", {name:"接收", topic:peerTopic.id, project:peerProject, engine:"codex", newSession:true});
    await choose(peerTopic.id);
    await ready(replyPeer.id);
    const receiveState = async id => (await (await page.request.get(base+"/api/messages/"+id)).json()).state;
    for (const engine of ["qwen","codebuddy","zcode","claude","traecli","copilot","codex","qoder"]) {
      const project = path.join(root,engine);
      fs.mkdirSync(project);
      fs.writeFileSync(path.join(project,"native-prompt.fixture"),engine);
      const topic = await post("/topics",{name:engine,project});
      const agent = await post("/windows",{name:engine,topic:topic.id,project,engine,newSession:true});
      await choose(topic.id);
      await ready(agent.id);
      await key(agent.id,"x");
      await page.waitForFunction(id=>{
        const p=panes.get(id);
        return observePrompt(p).draft && !p.view.inputReady && p.view.activity==="idle";
      },agent.id);
      await key(agent.id,"Control+u");
      await ready(agent.id);
      if (engine === "codebuddy") {
        await pane(agent.id).locator(".pane-power").click();
        const launcher = pane(agent.id).locator(".start-pane");
        await launcher.waitFor();
        fs.writeFileSync(path.join(project,"account-unavailable.fixture"),"");
        const account = await page.request.get(base+"/api/engines/codebuddy/account?"+new URLSearchParams({project}));
        assert.equal((await account.json()).status,"unknown");
        const loginSockets = [];
        const trackLogin = ws => { if (ws.url().includes("/engines/codebuddy/login")) loginSockets.push(ws.url()); };
        page.on("websocket", trackLogin);
        const started = page.waitForResponse(r=>r.url()===base+"/api/windows/"+agent.id+"/start");
        await launcher.click();
        assert.equal((await started).status(),204,"power resumes a bound session even when the account probe is unavailable");
        await ready(agent.id);
        assert.equal(await page.evaluate(id=>panes.get(id).agent.session,agent.id),agent.session);
        assert(logLines(engine,"terminal-launch.txt").includes("--resume"));
        assert(await page.locator("#engine-login-dialog").isHidden());
        assert.deepEqual(loginSockets,[]);
        page.off("websocket",trackLogin);
        fs.unlinkSync(path.join(project,"account-unavailable.fixture"));
        console.log("PASS CodeBuddy power resumes the same native session inside its own pane when the account probe fails");
      }
      if (["qwen","codebuddy","qoder"].includes(engine)) {
        const box = await pane(agent.id).locator(".xterm-screen").boundingBox();
        const rowHeight = box.height / await page.evaluate(id=>panes.get(id).term.rows,agent.id);
        await page.mouse.click(box.x+12,box.y+rowHeight/2);
        assert(await pane(agent.id).locator(".terminal-surface").evaluate(el=>el===document.activeElement),engine+" click output to read");
        await page.keyboard.press("PageUp");
        await page.mouse.click(box.x+12,box.y+2.5*rowHeight);
        assert(await pane(agent.id).locator(".xterm-helper-textarea").evaluate(el=>el===document.activeElement),engine+" click input to type");
        await page.keyboard.press("F6");
        assert(await pane(agent.id).locator(".terminal-surface").evaluate(el=>el===document.activeElement));
        await page.keyboard.press("Enter");
        await pane(agent.id).locator(".tile-engine").click();
        assert(!await pane(agent.id).locator(".xterm-helper-textarea").evaluate(el=>el===document.activeElement),engine+" click away releases focus");
      }
      const generation = await page.evaluate(id=>panes.get(id).generation,agent.id);
      const approvalUrl = base+"/api/windows/"+agent.id+"/approval";
      assert.equal((await page.request.post(approvalUrl,{data:{terminal:"stale"}})).status(),409);
      const approvalButton = pane(agent.id).getByRole("button",{name:"切换审批"});
      if (!await approvalButton.isVisible()) await pane(agent.id).locator(".pane-more").click();
      await approvalButton.click();
      await page.locator("#approval-choice").selectOption("auto");
      const applied = page.waitForResponse(r=>r.url()===approvalUrl && r.request().method()==="PUT");
      await page.locator("#apply-approval").click();
      assert.equal((await applied).status(),204,engine);
      await page.waitForFunction(([id,old])=>panes.get(id).generation!==old && panes.get(id).agent.approval==="auto",[agent.id,generation]);
      assert.equal(await page.evaluate(id=>panes.get(id).agent.session,agent.id),agent.session);
      const expected = {qwen:"auto-edit",codebuddy:"auto",qoder:"auto",zcode:"yolo",claude:"auto",copilot:"--allow-all-tools",
        codex:'approval_policy="never"',traecli:'approval_policy="never"'};
      await until(()=>logLines(engine,"terminal-launch.txt").includes(expected[engine]),engine+" approval launch args");
      assert.equal((await page.request.put(approvalUrl,{data:{terminal:generation,approval:"ask"}})).status(),409);
      const current = await page.evaluate(id=>panes.get(id).generation,agent.id);
      assert.equal((await page.request.put(approvalUrl,{data:{terminal:current,approval:"invalid"}})).status(),400);
      assert.equal((await page.request.put(approvalUrl,{data:{terminal:current,approval:"ask"}})).status(),204);
      await page.waitForFunction(id=>panes.get(id).agent.approval==="ask",agent.id);
      const askArgs = {qwen:"default",codebuddy:"default",qoder:"default",claude:"default",zcode:"build",
        codex:'approval_policy="on-request"',traecli:'approval_policy="on-request"'};
      await until(()=>{
        const args = logLines(engine,"terminal-launch.txt");
        return engine === "copilot" ? args.includes("--resume="+agent.session) && !args.includes("--allow-all-tools") :
          args.includes(askArgs[engine]);
      },engine+" ask launch args");
      if (!["qwen","codebuddy","zcode","qoder","traecli"].includes(engine)) continue;
      await ready(agent.id);
      assert(await pane(agent.id).locator(".auto-receive input").isChecked());
      await key(agent.id,"Control+b");
      await page.waitForFunction(id=>panes.get(id).view.activity==="working" && !panes.get(id).view.inputReady,agent.id);
      const message = send("@"+engine+"/"+engine,"请验证收件");
      await sleep(1650);
      assert.equal(state(message),"pending");
      await key(agent.id,"Control+u");
      await key(agent.id,"x");
      await sleep(1700);
      assert.equal(state(message),"pending");
      assert.equal((await page.request.post(approvalUrl,{data:{terminal:generation}})).status(),409);
      const reply = {jsonrpc:"2.0",id:1,method:"tools/call",params:{name:"send_to_agent",
        arguments:{target:replyPeer.id,message:engine+" received "+message.slice(0,8),supersedePrevious:false}}};
      fs.writeFileSync(path.join(project,"mcp-reply.fixture"), new URL(base).port+"\n"+JSON.stringify(reply));
      await key(agent.id,"Control+u");
      try { await until(async ()=>(await receiveState(message))==="delivered",engine+" automatic receipt"); }
      catch (error) {
        console.error(engine, state(message), await page.evaluate(id => {
          const p=panes.get(id), b=p.term.buffer.active;
          return {prompt:observePrompt(p), cursor:[b.cursorX,b.cursorY], rows:Array.from({length:p.term.rows},
            (_,i)=>b.getLine(b.baseY+i)?.translateToString(true)).filter(Boolean).slice(-8)};
        },agent.id), logLines(engine,"enters.txt"), logLines(engine,"accepted.txt"));
        throw error;
      }
      assert.equal(logLines(engine,"accepted.txt").filter(line=>line.includes(message.slice(0,8))).length,1);
      const responsePath = path.join(project,"mcp-reply.json");
      await until(()=>fs.existsSync(responsePath),engine+" native MCP response");
      const response = JSON.parse(fs.readFileSync(responsePath,"utf8")).result;
      assert(!response.isError, response.content[0].text);
      const replyId = JSON.parse(response.content[0].text).id;
      await until(async ()=>(await receiveState(replyId))==="delivered",engine+" MCP reply");
      assert.deepEqual(sql("SELECT source,target FROM inbox WHERE id=?",replyId),[[agent.id,replyPeer.id]]);
      assert.equal(logLines("reply-peer","accepted.txt").filter(line=>line.includes(replyId.slice(0,8))).length,1);
      if (engine === "traecli") {
        const receiveMode = pane(agent.id).getByRole("combobox",{name:"收件方式"});
        assert.equal(await receiveMode.inputValue(),"queued");
        await receiveMode.selectOption("immediate");
        await page.waitForFunction(id=>panes.get(id).view.agent.receiveMode==="immediate",agent.id);
        const optionUrl = base+"/api/windows/"+agent.id+"/auto-receive";
        assert.equal((await page.request.put(optionUrl,{data:{mode:"invalid"}})).status(),400);
        assert.equal(sql("SELECT receive_mode FROM windows WHERE id=?",agent.id)[0][0],"immediate");
        assert.equal(sql("SELECT receive_mode FROM windows WHERE id=?",replyPeer.id)[0][0],"queued");
        await key(agent.id,"Control+b");
        await key(agent.id,"x");
        const immediate = send(agent.id,"忙时接收，勿覆盖人工草稿");
        await sleep(1750);
        assert.equal(state(immediate),"pending");
        await key(agent.id,"Control+u");
        await key(agent.id,"Control+b");
        await page.waitForFunction(id=>panes.get(id).view.activity==="working", agent.id);
        await until(async ()=>(await receiveState(immediate))==="delivered","submit to native CLI while work continues");
        assert.equal(logLines(engine,"accepted.txt").filter(line=>line.includes(immediate.slice(0,8))).length,1);
        assert.equal(await page.evaluate(id=>panes.get(id).view.activity,agent.id),"working");
        await key(agent.id,"Control+u");
        console.log("PASS per-window queued/immediate selection, native busy input and protection of human drafts");
      }
    }
    console.log("PASS five CLI editors held through work and draft, submitted once on idle, and returned a window-authenticated MCP reply to a peer");
    console.log("PASS an unsent user draft remains idle and protected across all eight engines");
    console.log("PASS same-session restarts in both approval modes for eight engines; stale terminals rejected");
  } finally { await browser.close(); }
}
run().catch(error=>{console.error(error);process.exitCode=1;});
