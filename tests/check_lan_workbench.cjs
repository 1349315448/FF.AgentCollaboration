const assert = require("node:assert/strict");
const {chromium} = require("playwright");
const [local, lan, topic, agent] = process.argv.slice(2);

(async () => {
  const browser = await chromium.launch({executablePath:process.env.CHROME_PATH || undefined});
  try {
    const owner = await browser.newPage(), guest = await browser.newPage();
    const api = guest.request;
    assert.equal((await api.get(lan+"/api/state")).status(),401);
    assert.equal((await api.post(lan+"/api/lan-access/pairing",{headers:{Origin:lan}})).status(),403);
    await owner.goto(local);
    await owner.locator("#open-settings").click();
    await owner.getByRole("button",{name:"生成配对码"}).click();
    const code = await owner.getByLabel(/一次性配对码/).inputValue();
    await guest.goto(lan);
    await guest.getByLabel("配对码").fill(code);
    await guest.getByRole("button",{name:"连接工作台"}).click();
    await guest.locator(".topic-choice[data-topic='"+topic+"'] .topic-label").click();
    assert.equal(await guest.evaluate(()=>window.isSecureContext),false);
    const colors=await guest.locator(".project-states > span").evaluateAll(rows=>rows.map(row=>[
      getComputedStyle(row).color,getComputedStyle(row,"::before").backgroundColor
    ]));
    assert.equal(colors.length,3);
    assert(colors.every(([label,lamp])=>label===lamp) && new Set(colors.map(([label])=>label)).size===3);
    const path = lan+"/api/peers/windows/"+agent;
    const controls = guest.locator('[data-window="'+agent+'"] .fed-controls');
    const share = controls.getByRole("button",{name:"分享",exact:true});
    await controls.getByText("外部协作未开启",{exact:true}).waitFor();
    assert(await share.isDisabled());
    await controls.getByRole("button",{name:"开启外部协作"}).click();
    await controls.getByText("外部协作已开启",{exact:true}).waitFor();
    assert((await (await api.get(path)).json()).enabled);
    await share.click();
    await controls.getByText("已复制分享链接").waitFor();
    const disable=controls.getByRole("button",{name:"关闭外部协作"});
    await Promise.all([guest.waitForEvent("dialog").then(d=>d.dismiss()),disable.click()]);
    assert((await (await api.get(path)).json()).enabled);
    await Promise.all([guest.waitForEvent("dialog").then(d=>d.accept()),disable.click()]);
    await controls.getByText("外部协作未开启",{exact:true}).waitFor();
    assert(!(await (await api.get(path)).json()).link && await share.isDisabled());
    for (const forbidden of ["GET","DELETE"]) {
      const suffix=forbidden==="GET"?"":"/session";
      assert.equal((await api.fetch(lan+"/api/lan-access"+suffix,{method:forbidden,headers:{Origin:lan}})).status(),403);
    }
    assert.equal((await api.post(lan+"/api/topics",{headers:{Origin:"http://other.invalid"},data:{name:"invalid"}})).status(),403);
    const fresh=await browser.newContext();
    try {
      assert.equal((await fresh.request.post(lan+"/lan-login",{headers:{Origin:lan},form:{code}})).status(),401);
    } finally {await fresh.close();}
    const connection=guest.waitForEvent("websocket",{predicate:ws=>ws.url().includes(agent+"/terminal")});
    await guest.locator('[data-window="'+agent+'"] .start-pane').click();
    const socket=await connection;
    await guest.waitForFunction(id=>panes.get(id)?.socket?.readyState===WebSocket.OPEN,agent);
    const disconnected=socket.waitForEvent("close");
    assert.equal((await owner.request.delete(local+"/api/lan-access/session")).status(),204);
    await disconnected;
    assert.equal((await api.get(lan+"/api/state")).status(),401);
    assert.equal((await owner.request.get(local+"/api/state")).status(),200);
    assert.equal((await owner.request.post(local+"/api/windows/"+agent+"/stop")).status(),204);
    console.log("PASS LAN HTTP pairing, status colors, sharing controls and clipboard, one-time code, origin and admin isolation, SSE and revocation of a live ConPTY WebSocket in Chromium");
  } finally {await browser.close();}
})().catch(error=>{console.error(error);process.exitCode=1;});
