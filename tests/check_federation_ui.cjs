// Drive the local UI of two real instances; neither remote capsule owns a terminal.
const assert=require("node:assert/strict");
const fs=require("node:fs");
const path=require("node:path");
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
    await b.locator("#new-agent").click();
    await b.getByRole("tab",{name:"外部协作 Agent"}).click();
    await b.getByLabel("分享链接").fill(link);
    await b.getByLabel("申请用途").fill("界面回信验证");
    await b.getByRole("button",{name:"发送申请"}).click();
    await a.getByRole("button",{name:"待审申请",exact:true}).click();
    const incoming=a.locator(".fed-grant").filter({hasText:"界面回信验证"});
    await incoming.getByRole("button",{name:"同意",exact:true}).click();
    await a.getByRole("button",{name:"全部",exact:true}).click();
    const capsule=b.locator(".remote-capsule").last();
    await capsule.locator(".remote-quota").filter({hasText:"余 20 次"}).waitFor();
    await capsule.click();
    const longMail="请回一条确认"+"完整".repeat(101)+"末尾核对";
    await b.getByLabel("发给对端").fill(longMail);
    await b.getByRole("button",{name:"发送消息",exact:true}).click();
    await b.getByText("对端已接收",{exact:true}).waitFor();
    await b.locator(".remote-quota-detail").filter({hasText:"剩余 19 / 20 次"}).waitFor();
    await a.locator('[data-window="'+windowId+'"] .fed-badge').click();
    await incoming.locator("summary").click();
    await incoming.getByText(longMail,{exact:true}).waitFor();
    await incoming.getByLabel("回信",{exact:true}).fill("浏览器已收到");
    await b.getByLabel("发给对端").fill("另一条来信");
    await b.getByRole("button",{name:"发送消息",exact:true}).click();
    await incoming.locator("option").filter({hasText:"另一条来信"}).waitFor({state:"attached"});
    assert.equal(await incoming.getByRole("textbox").inputValue(),"浏览器已收到");
    await incoming.getByRole("button",{name:"发送回信"}).click();
    await b.getByText(/最近回信.*浏览器已收到/).waitFor();
    assert.equal(await b.locator(".remote-capsule .terminal-surface").count(),0);
    assert.equal(await a.locator('[data-window="'+windowId+'"]').count(),1);
    console.log("PASS Chromium node settings, remote capsule, approval, message and SSE reply with no remote PTY");
    await a.locator("#federation-dialog .dialog-header button").click();
    await b.locator("#federation-dialog .dialog-header button").click();
    const remoteId=await capsule.getAttribute("data-remote-id");
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
    assert((await b.request.post(requester+"/api/peers/remotes/"+remoteId+"/messages",{data:{id:requestId,text:"联邦离线唤醒验证"}})).ok());
    await a.waitForFunction(id=>panes.get(id)?.view.agent.online,windowId,{timeout:30000});
    try { await a.waitForFunction(()=>snapshot.timeline.some(i=>i.summary.includes("联邦离线唤醒验证") && i.state==="delivered"),undefined,{timeout:30000}); }
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
    fs.writeFileSync(path.join(project,"audit-marker.txt"),"fixture");
    const grants=(await (await a.request.get(provider+"/api/peers")).json()).grants;
    const grant=grants.find(g=>g.windowId===windowId && g.status==="approved");
    assert((await a.request.post(provider+"/api/peers/grants/"+grant.id+"/replies",{data:{id:crypto.randomUUID().replaceAll("-",""),requestId,text:"核对修改"}})).ok());
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
    console.log("PASS offline cross-node wake, dedicated native arguments, exactly one submission, mailbox flights, filters and Git dirty alert");
  } finally { await browser.close(); }
})().catch(e=>{console.error(e);process.exitCode=1;});
