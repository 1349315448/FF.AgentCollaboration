// Drive the local UI of two real instances; neither remote capsule owns a terminal.
const assert=require("node:assert/strict");
const fs=require("node:fs");
const path=require("node:path");
const {execFileSync}=require("node:child_process");
const {chromium}=require("playwright");
const [provider,requester,windowId]=process.argv.slice(2);
const link=require("node:fs").readFileSync(0,"utf8");
(async()=>{
  const browser=await chromium.launch({executablePath:process.env.CHROME_PATH || undefined});
  try {
    const a=await browser.newPage(), b=await browser.newPage();
    const sockets=[];
    b.on("websocket",s=>sockets.push(s.url()));
    await Promise.all([a.goto(provider),b.goto(requester)]);
    await a.locator(".topic-choice .topic-label").filter({hasText:"提供方"}).click();
    await b.locator(".topic-choice .topic-label").filter({hasText:"请求方"}).click();
    await a.locator("#open-settings").click();
    await a.getByLabel("本端名称",{exact:true}).fill("代码服务器");
    await a.getByRole("button",{name:"保存节点设置"}).click();
    await a.getByText("已保存",{exact:true}).waitFor();
    await a.locator("#close-settings").click();
    await a.locator("#new-agent").click();
    await a.getByRole("tab",{name:"外部协作 Agent"}).click();
    await a.getByLabel("分享链接").fill(link);
    await a.getByLabel("申请用途").fill("本机链接检查");
    await a.getByRole("button",{name:"发送申请"}).click();
    await a.locator("#new-dialog [role=alert]").filter({hasText:"本工作台"}).waitFor();
    await a.locator("#new-dialog .dialog-header button").click();
    await b.locator("#new-agent").click();
    await b.getByRole("tab",{name:"外部协作 Agent"}).click();
    await b.getByLabel("分享链接").fill(link);
    await b.getByLabel("申请用途").fill("界面回信验证");
    await b.getByLabel("回信 Agent（对方可 @）").selectOption({label:"@Planner"});
    await b.getByRole("button",{name:"发送申请"}).click();
    await a.getByRole("button",{name:"待审申请",exact:true}).click();
    const pendingRow=a.locator(".fed-application-row").filter({hasText:"界面回信验证"});
    assert.equal(await a.locator(".fed-mail-approvals select,.fed-mail-approvals input").count(),0);
    await pendingRow.getByRole("button",{name:"查看"}).click();
    const incoming=a.locator("#federation-dialog .fed-grant").filter({hasText:"界面回信验证"});
    await incoming.getByLabel("次数",{exact:true}).fill("21");
    await incoming.getByRole("button",{name:"同意",exact:true}).click();
    await a.locator("#federation-dialog").waitFor({state:"hidden"});
    await a.getByRole("button",{name:"全部",exact:true}).click();
    const approved=b.locator(".remote-capsule").filter({hasText:"余 21 次"});
    await approved.waitFor();
    const contact=a.locator('.tree-agent[data-agent="'+windowId+'"] + .tree-return-contacts li').filter({hasText:"@Planner"});
    await contact.waitFor();
    await contact.getByText("在线",{exact:true}).waitFor();
    assert.equal(await contact.locator(".nav-dot").evaluate(dot=>getComputedStyle(dot).backgroundColor),"rgb(54, 229, 121)");
    const left=await contact.locator("svg").boundingBox();
    const parent=await a.locator('.tree-agent[data-agent="'+windowId+'"] .tag-power').boundingBox();
    assert(left.x>parent.x+12,"contact is visibly indented below its owning Agent");
    assert.equal(await a.locator(".topic-agent-tag,.remote-capsule").filter({hasText:"@Planner"}).count(),0);
    const remoteId=await approved.getAttribute("data-remote-id");
    const capsule=b.locator('.remote-capsule[data-remote-id="'+remoteId+'"]');
    const tree=b.locator('.tree-remote[data-remote-id="'+remoteId+'"]');
    const remoteDialog=b.locator("#federation-dialog");
    await capsule.click();
    assert.equal(await remoteDialog.isVisible(),false);
    await tree.dblclick();
    await remoteDialog.getByRole("button",{name:"移除本地胶囊"}).waitFor();
    assert.equal(await remoteDialog.getByRole("textbox").count(),0);
    const sendManual=async text=>{
      const res=await b.request.post(requester+"/api/peers/remotes/"+remoteId+"/messages",{data:{id:crypto.randomUUID().replaceAll("-",""),text,expectsReply:true}});
      assert(res.ok(),await res.text());
    };
    const longMail="请回一条确认"+"完整".repeat(101)+"末尾核对";
    await sendManual(longMail);
    await b.locator(".remote-quota-detail").filter({hasText:"剩余 20 / 21 次"}).waitFor();
    await a.locator('[data-window="'+windowId+'"] .fed-badge').click();
    await incoming.getByRole("button",{name:"保存权限"}).waitFor();
    assert.equal(await incoming.getByText(/最近来信与回信|发送回信/).count(),0);
    assert.equal(await incoming.getByRole("textbox").count(),0);
    assert.equal(await remoteDialog.getByText(/最近回信/).count(),0);
    assert.equal(await b.locator(".remote-capsule .terminal-surface").count(),0);
    assert.equal(await a.locator('[data-window="'+windowId+'"]').count(),1);
    console.log("PASS Chromium node settings, remote capsule, approval and sharing settings without a manual reply form");
    await a.locator("#federation-dialog .dialog-header button").click();
    await b.locator("#federation-dialog .dialog-header button").click();
    assert(!sockets.some(url=>url.includes(remoteId+"/terminal")));
    const shareUrl=provider+"/api/peers/windows/"+windowId;
    assert((await a.request.post(provider+"/api/windows/"+windowId+"/inbox/acknowledge-all")).ok());
    const manage=a.locator('[data-window="'+windowId+'"] .fed-badge'), dialog=a.locator("#federation-dialog");
    const sharing=async()=>await (await a.request.get(shareUrl)).json();
    const agent=async()=>(await (await a.request.get(provider+"/api/state")).json()).windows.find(w=>w.agent.id===windowId);
    await manage.click();
    const modes=dialog.getByLabel("协作模式");
    await modes.selectOption("restricted");
    await dialog.getByText(/下次打开终端时生效/).waitFor();
    assert(!(await sharing()).dedicatedReadOnly);
    await dialog.getByRole("button",{name:"保存设置",exact:true}).click();
    await dialog.getByRole("status").filter({hasText:"已保存"}).waitFor();
    assert((await sharing()).dedicatedReadOnly && !(await agent()).agent.online);
    await dialog.locator(".dialog-header button").click();
    assert((await a.request.put(provider+"/api/windows/"+windowId+"/auto-receive",{data:{enabled:true}})).ok());
    const project=(await (await a.request.get(provider+"/api/state")).json()).windows.find(w=>w.agent.id===windowId).agent.project;
    const requestId=crypto.randomUUID().replaceAll("-","");
    // Capture the real effect in both pages with the dialogs closed.
    for(const page of [a,b]) await page.evaluate(()=>{window.flightCount=0;
      new MutationObserver(records=>{for(const r of records) for(const n of r.addedNodes)
        if(n.classList?.contains("message-flight"))window.flightCount++;}).observe(document.body,{childList:true,subtree:true});});
    fs.writeFileSync(path.join(project,"audit-marker.fixture"),"fixture");
    assert((await b.request.post(requester+"/api/peers/remotes/"+remoteId+"/messages",{data:{id:requestId,text:"联邦离线唤醒验证",expectsReply:true}})).ok());
    await a.waitForFunction(id=>panes.get(id)?.view.agent.online,windowId,{timeout:30000});
    await b.waitForFunction(id=>snapshot.federation.remotes.some(r=>r.id===id && r.online && r.connected),remoteId);
    const colors=await b.locator('[data-remote-id="'+remoteId+'"] .nav-dot').evaluateAll(dots=>dots.map(dot=>getComputedStyle(dot).backgroundColor));
    assert.deepEqual(colors,["rgb(54, 229, 121)","rgb(54, 229, 121)"]);
    assert.equal(await tree.locator("small").innerText(),"在线");
    const targetTopic=(await (await b.request.get(requester+"/api/peers")).json()).remotes.find(r=>r.id===remoteId).topicId;
    const created=await b.request.post(requester+"/api/windows",{data:{topic:targetTopic,name:"联络员",engine:"codex",newSession:true}});
    assert(created.ok(),await created.text());
    const source=(await created.json()).id;
    assert((await b.request.put(requester+"/api/peers/remotes/"+remoteId+"/contact",{data:{topicId:targetTopic,agentId:source}})).ok());
    await a.waitForFunction(id=>snapshot.federation.contacts.some(c=>c.windowId===id && c.name==="联络员" && c.online && !c.canMention),windowId);
    const picker=b.locator("#agents-dialog");
    await b.locator('[data-window="'+source+'"] .insert-agent').click();
    const option=picker.getByRole("option").filter({hasText:"@Reviewer"});
    await option.waitFor();await picker.locator("#peer-search").fill("Reviewer");
    await picker.locator("#peer-search").press("Enter");
    await picker.waitFor({state:"hidden"});
    await b.waitForFunction(id=>{const p=panes.get(id);return p && observePrompt(p).editableText?.includes("@Reviewer");},source);
    try { await a.waitForFunction(()=>snapshot.federation.mail.some(m=>m.text==="联邦离线唤醒验证" && m.deliveryState==="delivered"),undefined,{timeout:30000}); }
    catch(e){
      console.error("isolated wake",await a.evaluate(id=>{const p=panes.get(id);return {inbox:p.view.inbox.map(i=>[i.state,i.text]),
        error:p.element.querySelector(".pane-error")?.textContent,prompt:observePrompt(p),
        rows:Array.from({length:6},(_,i)=>{const l=p.term.buffer.active.getLine(p.term.buffer.active.baseY+i);return [i,l?.isWrapped,l?.translateToString(true)];}),
        sample:snapshot.timeline.slice(0,3).map(i=>[i.state,i.summary])};},windowId));
      throw e;
    }
    assert(fs.readFileSync(path.join(project,"terminal-launch.txt"),"utf8").includes("read-only"));
    assert.equal(fs.readFileSync(path.join(project,"accepted.txt"),"utf8").split("\n").filter(l=>l.includes("联邦离线唤醒验证")).length,1);
    assert.equal(fs.readFileSync(path.join(project,"terminal-pids.txt"),"utf8").trim().split(/\r?\n/).length,1);
    for(const page of [a,b]) await page.waitForFunction(()=>window.flightCount>0);
    const reply=execFileSync("python",["-B","-c",`
import hashlib, json, os, sqlite3, sys, uuid
from pathlib import Path
from contextlib import closing
sys.path.insert(0, str(Path.cwd() / "tests"))
from check_workbench import mcp
window, request = sys.argv[1:]
root = Path(os.environ["FF_TEST_WORKBENCH_STATE"])
grant = uuid.uuid4().hex * 2
with closing(sqlite3.connect(root / "workbench.sqlite")) as db:
    task = db.execute("SELECT local FROM fed_incoming WHERE request=?", (request,)).fetchone()[0]
    previous = db.execute("SELECT grant_hash FROM windows WHERE id=?", (window,)).fetchone()[0]
    db.execute("UPDATE windows SET grant_hash=? WHERE id=?", (hashlib.sha256(grant.encode()).hexdigest().upper(), window))
    db.commit()
    try:
        print(json.dumps(mcp(os.environ["FF_TEST_WORKBENCH_EXE"], root, grant, "send_to_agent",
                             target="@reply-"+task, replyTo=task, finalReply=True, message="核对修改")))
    finally:
        db.execute("UPDATE windows SET grant_hash=? WHERE id=?", (previous, window))
        db.commit()
`,windowId,requestId],{encoding:"utf8"});
    assert(!JSON.parse(reply).isError,reply);
    await b.locator('.fed-mail-row[data-kind="reply-in"]').filter({hasText:"核对修改"}).waitFor({state:"attached"});
    await a.locator(".fed-mail-row[data-kind=audit]").filter({hasText:"audit-marker.txt"}).waitFor();
    await a.getByRole("button",{name:"外部来信",exact:true}).click();
    await a.locator(".fed-mail-row[data-kind=incoming]").filter({hasText:"请回一条确认"}).click();
    await a.locator(".mail-detail").getByText(longMail,{exact:true}).waitFor();
    assert.equal(await a.locator("dialog[open]").count(),0);
    await a.getByRole("button",{name:"全部",exact:true}).click();
    const mailMarks=[];
    for(const kind of ["incoming","reply-out","application","audit"]){
      const mark=await a.locator('.fed-mail-row[data-kind="'+kind+'"] .log-time svg path').first().getAttribute("d");
      assert(mark,"expected a visible status icon before the time for "+kind);
      mailMarks.push(mark);
    }
    assert.equal(new Set(mailMarks).size,mailMarks.length);
    const before=await agent();
    const stale=await a.request.put(shareUrl,{data:{enabled:true,permissionMode:"read_only",restartTerminal:"stale"}});
    assert.equal(stale.status(),409);
    assert((await sharing()).dedicatedReadOnly && (await agent()).terminal===before.terminal);
    await manage.click();
    for(const mode of ["read_only","restricted"]) {
      const old=await agent();
      await modes.selectOption(mode);
      assert.equal((await agent()).terminal,old.terminal);
      await dialog.getByRole("button",{name:"保存并重启此 Agent"}).click();
      await dialog.getByText("已保存，终端已重启。",{exact:true}).waitFor();
      const current=await agent();
      assert(current.agent.online && current.terminal!==old.terminal);
      assert.equal(current.agent.session,before.agent.session);
      assert.equal((await sharing()).dedicatedReadOnly,mode==="restricted");
      await a.waitForFunction(({id,generation})=>panes.get(id)?.generation===generation,{id:windowId,generation:current.terminal});
    }
    a.once("dialog",d=>d.accept());
    await modes.selectOption("read_write");
    await dialog.getByRole("button",{name:"保存并重启此 Agent"}).click();
    await dialog.getByText("已保存，终端已重启。",{exact:true}).waitFor();
    const regular=await agent();
    await modes.selectOption("read_only");
    await dialog.getByRole("button",{name:"保存设置",exact:true}).click();
    await dialog.getByText("已保存。",{exact:true}).waitFor();
    assert.equal((await agent()).terminal,regular.terminal);
    assert.equal((await sharing()).permissionMode,"read_only");
    await dialog.locator(".dialog-header button").click();
    await a.waitForFunction(id=>snapshot.federation.contacts.some(c=>c.windowId===id && c.name==="联络员" && c.online && c.canMention),windowId);
    const providerPicker=a.locator("#agents-dialog");
    await a.locator('[data-window="'+windowId+'"] .insert-agent').click();
    await providerPicker.getByRole("option").filter({hasText:"@联络员"}).click();
    await a.waitForFunction(id=>observePrompt(panes.get(id)).editableText?.includes("@联络员"),windowId);
    const friend=a.locator(".tree-return-contacts button").filter({hasText:"@联络员"});
    await friend.click();
    assert.equal(await dialog.isVisible(),false);
    await friend.dblclick();
    await dialog.getByRole("button",{name:"移除协作",exact:true}).click();
    await friend.waitFor({state:"detached"});
    await tree.getByText("无权限",{exact:true}).waitFor();
    await b.locator('.fed-mail-row[data-kind="permission"]').filter({hasText:"@Reviewer 已移除 @联络员 的协作请求权限"}).waitFor();
    await manage.click();
    console.log("PASS provider can mention the bound requesting Agent in its own terminal");
    console.log("PASS sharing mode is staged, offline stays offline, online restarts the same session, stale confirmation rejected, permission-only changes keep the PTY");
    const enabled=dialog.getByLabel("开启外部协作"), saveSettings=dialog.getByRole("button",{name:"保存设置",exact:true});
    for(const online of [true,false]) {
      await modes.selectOption("restricted");
      const save=online ? dialog.getByRole("button",{name:"保存并重启此 Agent"}) : saveSettings;
      await save.click();
      await dialog.getByText(online?"已保存，终端已重启。":"已保存。",{exact:true}).waitFor();
      const restricted=await agent();
      await a.waitForFunction(({id,gen})=>panes.get(id)?.generation===gen,{id:windowId,gen:restricted.terminal});
      await enabled.uncheck();
      await dialog.getByText(/恢复本机终端原有权限/).waitFor();
      a.once("dialog",d=>d.accept());
      await save.click();
      await dialog.getByText(online?"已保存，终端已重启。":"已保存。",{exact:true}).waitFor();
      const disabled=await sharing(), local=await agent();
      assert(!disabled.enabled && !disabled.dedicatedReadOnly && !disabled.link);
      assert.equal(local.agent.online,online);assert.equal(local.agent.session,before.agent.session);
      if (online) {
        assert.notEqual(local.terminal,restricted.terminal);
        assert((await a.request.post(provider+"/api/windows/"+windowId+"/stop")).ok());
        await a.waitForFunction(id=>!panes.get(id)?.agent.online,windowId);
      }
      await enabled.check(); await saveSettings.click(); await dialog.getByText("已保存。",{exact:true}).waitFor();
    }
    console.log("PASS disabling restricted sharing restores local permissions online and offline");
    assert((await a.request.post(provider+"/api/windows/"+windowId+"/stop")).ok());
    await b.waitForFunction(id=>snapshot.federation.remotes.every(r=>r.id!==id || !r.online),remoteId);
    await b.locator('[data-window="'+source+'"] .insert-agent').click();await picker.waitFor();
    assert.equal(await option.count(),0);
    const sourceTerminal=await b.evaluate(id=>panes.get(id).generation,source);
    const staleMention=await b.request.post(requester+"/api/windows/"+source+"/mention",{data:{sourceTerminal,targetId:remoteId}});
    assert.equal(staleMention.status(),409);
    await b.keyboard.press("Escape");await capsule.dblclick();
    await remoteDialog.getByRole("button",{name:"移除本地胶囊"}).click();await capsule.waitFor({state:"hidden"});
    console.log("PASS same-topic online remote mention, matching navigation lights, offline rejection and double-click details with removal");
    console.log("PASS offline cross-node wake, dedicated native arguments, exactly one submission, mailbox flights, filters and Git dirty alert");
  } finally { await browser.close(); }
})().catch(e=>{console.error(e);process.exitCode=1;});
