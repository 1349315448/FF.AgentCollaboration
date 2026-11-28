const assert = require("node:assert/strict");
const {execFileSync} = require("node:child_process");
const { chromium } = require("playwright");

const [base, group, member, linked] = process.argv.slice(2);

async function watchFlight(page,reducedMotion="no-preference"){
  await page.emulateMedia({reducedMotion});
  await page.evaluate(()=>{
    window.flightObserver?.disconnect();
    window.chatFlightFrames=[];
    window.flightObserver=new MutationObserver(records=>{
      for(const record of records) for(const node of record.addedNodes){
        if(!node.matches?.(".chat-envelope-flight"))continue;
        const motion=node.getAnimations()[0],frames=motion.effect.getKeyframes();
        const right=Math.max(...frames.map(frame=>Number(frame.transform.match(/translate\((-?\d+)px/)[1])+9));
        const record=[frames[0].transform,frames.at(-1).transform,right,!!document.querySelector(".chat-flight-trail,.chat-flick-hand"),motion.effect.getTiming().duration];
        window.chatFlightFrames.push(record);
        motion.finished.then(()=>{record[1]=motion.effect.getKeyframes().at(-1).transform;});
      }
    });
    window.flightObserver.observe(document.body,{childList:true});
  });
}

function sendAsLeader(args){
  const result=execFileSync(process.env.FF_CHAT_TEST_HOST,
    ["workbench-mcp",process.env.FF_CHAT_TEST_STATE,"--notify-port",new URL(base).port],{
      input:JSON.stringify({jsonrpc:"2.0",id:1,method:"tools/call",params:{name:"send_to_agent",arguments:args}})+"\n",
      encoding:"utf8",timeout:15000,windowsHide:true
    });
  const response=JSON.parse(result).result;
  assert(!response.isError,JSON.stringify(response.content));
}

function frameAt(frame,box){
  const [,x,y]=frame.match(/translate\((-?\d+)px,\s*(-?\d+)px\)/);
  return Math.abs(Number(x)+9-box.x-box.width/2)<2 && Math.abs(Number(y)+6-box.y-box.height/2)<2;
}

async function checkInvite(context, provider, requesterUrl, requesterGroup) {
  const panel = page=>page.getByRole("dialog",{name:"小组合作"});
  async function open(page,url,topic) {
    await page.goto(url);
    if (topic) {
      await page.locator('.topic-choice[data-topic="'+topic+'"] .topic-label').press("Enter");
      await page.waitForFunction(id=>selectedTopicId===id,topic);
    }
    await page.locator(".workbench-menu summary").filter({hasText:/^小组$/}).click();
    await page.locator(".workbench-menu").getByRole("button",{name:"小组合作",exact:true}).click();
    await panel(page).getByRole("button",{name:/外部小组/}).click();
  }
  await provider.addInitScript(()=>{
    Object.defineProperty(navigator,"clipboard",{value:{writeText:async value=>{window.copiedGroupInvite=value;}}});
  });
  await open(provider,base,group);
  const asking = panel(provider);
  await asking.getByLabel("合作用途").fill("常驻接口审阅");
  assert.equal(await asking.getByLabel("本组参与成员范围").inputValue(),"all");
  await asking.getByLabel("本组参与成员范围").selectOption("selected");
  await asking.getByRole("checkbox",{name:"@甲组组员",exact:true}).check();
  await asking.getByText("权限与配额",{exact:true}).click();
  await asking.getByLabel("次数配额").selectOption("total");
  await asking.getByLabel("上限").fill("2");
  await asking.getByRole("button",{name:"复制小组邀请"}).click();
  await asking.getByText(/邀请已复制/).waitFor();
  const invite=await provider.evaluate(()=>window.copiedGroupInvite);
  assert.equal(new URL(invite).searchParams.get("ffGroup"),"甲组");
  assert(new URL(invite).searchParams.get("ffMember"));

  const requester=await context.newPage();
  await open(requester,requesterUrl,requesterGroup);
  const accepting = panel(requester);
  await accepting.getByRole("button",{name:"接受邀请",exact:true}).click();
  await accepting.getByLabel("粘贴收到的邀请").fill(invite);
  await accepting.getByText("邀请来源 · 甲组",{exact:true}).waitFor();
  assert.equal(await accepting.getByLabel("合作用途").inputValue(),"常驻接口审阅");
  await accepting.getByLabel("我的小组").selectOption(requesterGroup);
  assert.equal(await accepting.getByLabel("本组参与成员范围").inputValue(),"all");
  await accepting.getByRole("button",{name:"提交本组名单"}).click();
  await accepting.getByRole("button",{name:"关闭小组合作"}).click();
  const waitingTeam=requester.locator('[data-nav-topic="'+requesterGroup+'"]');
  const expand=waitingTeam.locator(".topic-choice").getByRole("button",{name:/展开|收起/});
  if(await expand.getAttribute("aria-expanded")==="false")await expand.click();
  const waitingLink=waitingTeam.locator('[data-collab-branch="partners"] button.tree-loan-group');
  await waitingLink.waitFor();
  assert((await waitingLink.locator("span").innerText()).length>0);
  assert.match(await waitingLink.locator("small").innerText(),/申请中|待审批/);
  await waitingLink.click();
  await accepting.getByText("常驻接口审阅",{exact:true}).waitFor();
  await asking.getByRole("button",{name:/乙组.*待审批/}).click();
  assert(await asking.getByRole("checkbox",{name:"@甲组组员",exact:true}).isChecked());
  await asking.getByText(/对方参与成员.*@乙组组员/).waitFor();
  const state=await (await provider.request.get(base+"/api/peers")).json();
  assert.equal(state.groups[0].status,"pending");
  assert.deepEqual(state.groups[0].recipients,[]);
  assert.equal((await provider.request.put(base+"/api/peers/grants/"+state.groups[0].id,{data:{action:"approve"}})).status(),404);
  await asking.getByRole("button",{name:"确认合作",exact:true}).click();
  await asking.getByText("已保存",{exact:true}).waitFor();
  console.log("PASS Chromium copy invitation, choose own groups and members, review and confirm");
}

async function checkLayout(page, ids) {
  await page.setViewportSize({width:2048,height:1000});
  await page.emulateMedia({reducedMotion:"reduce"});
  await page.route(url=>url.pathname==="/api/state",async route=>{
    const response=await route.fetch();
    const data=await response.json();
    for(const view of data.windows.filter(view=>ids.includes(view.agent.id))){
      view.agent.online=true;
      view.terminal=view.agent.id;
    }
    await route.fulfill({response,json:data});
  });
  await page.routeWebSocket(/\/api\/windows\/[^/]+\/terminal/,socket=>{
    socket.send(JSON.stringify({cols:80,rows:24,replayThrough:0}));
  });
  await page.goto(base);
  await page.locator('.topic-choice[data-topic="'+group+'"] .topic-label').press("Enter");
  await page.waitForFunction(id=>selectedTopicId===id,group);
  await page.locator("#toggle-inspector").click();
  assert(await page.locator("#inspector-shell").isHidden());
  const agent = id => page.locator('.tree-agent[data-agent="'+id+'"] .member-name');
  const tile = id => page.locator('.pane[data-window="'+id+'"] .terminal-title');
  const visible = () => page.locator(":is(#primary-panes,#panes) > .pane:visible");
  const order = [ids[2],ids[0],ids[1],...ids.slice(3)];
  const openTile = async id => {
    await agent(id).press("Enter");
    await page.waitForFunction(id=>layout.openedIds.includes(id),id);
  };
  await page.setViewportSize({width:1130,height:900});
  await openTile(order[0]);
  await openTile(order[1]);
  const boxes = count => Promise.all(order.slice(0,count).map(id=>tile(id).evaluate(title=>{
    // 测量网格分配的位置，忽略窗格移动动画的临时位移。
    const pane=title.closest(".pane");
    return {x:pane.offsetLeft,y:pane.offsetTop};
  })));
  const [upper,lower] = await boxes(2);
  assert.equal(upper.x,lower.x);
  assert(lower.y>upper.y);
  await openTile(order[2]);
  const [first,second,third] = await boxes(3);
  assert.equal(first.y,second.y);
  assert(second.x>first.x);
  assert(third.y>first.y);
  await page.setViewportSize({width:2048,height:1000});
  for (const id of order.slice(3,9)) await openTile(id);
  await page.waitForFunction(list=>list.every(id=>panes.get(id)?.socket?.readyState===WebSocket.OPEN),ids);
  await page.evaluate(list=>{window.testTerminals=list.map(id=>[id,panes.get(id).term,panes.get(id).socket]);},ids);
  const positions=()=>visible().evaluateAll(nodes=>nodes.map(node=>{
    const rect=node.getBoundingClientRect();
    return [node.dataset.window,rect.x,rect.y,rect.bottom];
  }).sort(([a],[b])=>a.localeCompare(b)));
  const rectangles=await positions();
  assert.equal(rectangles.length,9);
  assert.equal(new Set(rectangles.map(rect=>rect[1])).size,3);
  assert.equal(new Set(rectangles.map(rect=>rect[2])).size,3);
  const workspace = await page.locator("#workspace-layout").boundingBox();
  assert(rectangles.every(rect=>rect[3]<=workspace.y+workspace.height));
  assert.equal(await page.locator(".collaboration-scroll").evaluate(node=>getComputedStyle(node).overflowY),"clip");

  await tile(ids[4]).dblclick();
  assert.equal(await visible().count(),1);
  await tile(ids[4]).dblclick();
  assert.equal(await visible().count(),9);
  assert.deepEqual(await positions(),rectangles);

  for(const accept of [false,true]){
    let prompt;
    page.once("dialog",async dialog=>{
      prompt=dialog.message();
      await (accept?dialog.accept():dialog.dismiss());
    });
    await agent(ids[9]).press("Enter");
    assert.match(prompt,/达到 9 个.*\n收起最早打开的 @窗格 03/);
    assert.equal(await visible().count(),9);
    assert.equal(await tile(ids[2]).isVisible(),!accept);
    assert.equal(await tile(ids[9]).isVisible(),accept);
  }
  assert(await page.evaluate(()=>window.testTerminals.every(([id,term,socket])=>
    panes.get(id).term===term&&panes.get(id).socket===socket&&socket.readyState===WebSocket.OPEN)));

  await page.setViewportSize({width:1070,height:630});
  await page.waitForFunction(()=>document.querySelectorAll('#panes > .pane').length<9);
  const temporarilyHidden=await page.evaluate(list=>list.find(id=>layout.openedIds.includes(id)&&panes.get(id).surface.hidden),ids);
  assert(temporarilyHidden);
  await agent(temporarilyHidden).press("Enter");
  assert(await tile(temporarilyHidden).isVisible());
  await page.setViewportSize({width:2048,height:1000});
  await page.waitForFunction(()=>document.querySelectorAll('#panes > .pane').length===9);
  console.log("PASS adaptive terminal grid, focus and restore, FIFO confirmation, persistent terminal sockets");
}

async function run() {
  const browser = await chromium.launch({ executablePath: process.env.CHROME_PATH || undefined });
  try {
    const context = await browser.newContext({ viewport: { width: 1460, height: 900 } });
    const page = await context.newPage();
    const errors = [];
    page.on("pageerror", error => errors.push(error.message));
    if (member==="--layout") {
      await checkLayout(page,process.argv.slice(5));
      assert.deepEqual(errors,[]);
      return;
    }
    if (member==="--invite") {
      await checkInvite(context,page,linked,process.argv[6]);
      assert.deepEqual(errors,[]);
      return;
    }
    await page.goto(base);
    const chat = page.getByRole("region", { name: "小组群聊" });
    const menu = name => page.locator(".workbench-menu summary").filter({hasText:new RegExp("^"+name+"$")});
    const input = chat.getByRole("textbox", { name: "群聊消息" });
    await page.locator(".brand").getByLabel(/^软件版本 Base v\d+\.\d+\.\d+/).waitFor();
    const markBox=await page.locator("#project-collapse").boundingBox();
    const dotBox=await page.locator(".platform-mark #connection").boundingBox();
    assert(dotBox.x>=markBox.x+markBox.width/2 && dotBox.y>=markBox.y+markBox.height/2);
    assert(dotBox.x+dotBox.width<=markBox.x+markBox.width && dotBox.y+dotBox.height<=markBox.y+markBox.height);
    if (member==="--external-user") {
      const project=page.locator(".project-select").first();
      if (await project.getAttribute("aria-expanded")==="false") await project.click();
      await page.locator('.topic-choice[data-topic="'+group+'"] .topic-label').click();
      const branch=page.locator('.topic-choice[data-topic="'+group+'"]').getByRole("button",{name:/展开|收起/});
      if(await branch.getAttribute("aria-expanded")==="false")await branch.click();
      const topicTree=page.locator('[data-nav-topic="'+group+'"]');
      const ownMembers=topicTree.locator('[data-collab-branch="own"]');
      const contact=ownMembers.locator(".tree-remote").filter({hasText:"@提供组组长"});
      await contact.locator(".remote-origin").waitFor();
      assert.equal(await topicTree.getByText("外部 Agent",{exact:true}).count(),0);
      assert(await contact.locator(":scope > svg").isVisible());
      assert.equal((await contact.boundingBox()).height,28);
      assert((await contact.locator(".remote-origin").boundingBox()).y>(await contact.locator("b").boundingBox()).y);
      const summary=ownMembers.locator(":scope > summary");
      await summary.click();
      assert(!(await contact.isVisible()));
      const textX=await summary.evaluate(node=>{
        const range=document.createRange();
        range.selectNodeContents(node);
        return range.getBoundingClientRect().left;
      });
      assert(textX>(await topicTree.locator(".topic-label").boundingBox()).x+8);
      await summary.click();
      await contact.waitFor();
      await input.fill("@提供");
      await chat.getByRole("option",{name:/@提供组组长/}).click();
      await page.keyboard.insertText("请核对接口");
      await input.press("Enter");
      await chat.locator(".chat-message").filter({hasText:"@提供组组长 请核对接口"}).locator(".chat-receipts").waitFor();
      await page.route(url=>url.pathname==="/api/state",async route=>{
        const response=await route.fetch(), data=await response.json();
        for(const peer of data.federation.remotes) {peer.connected=false;peer.deniedAgentIds=[];}
        await route.fulfill({response,json:data});
      });
      await page.evaluate(()=>refresh());
      await contact.getByText("断联",{exact:true}).waitFor();
      assert.match(await contact.getAttribute("title"),/断联/);
      assert.deepEqual(errors,[]);
      return;
    }
    if (process.argv.includes("--permanent")) {
      const topic=page.locator('.topic-choice[data-topic="'+group+'"]');
      await topic.locator(".topic-label").click();
      await chat.getByRole("button",{name:process.argv.includes("--lender")?"甲组":"乙组",exact:true}).waitFor();
      const toggle=topic.getByRole("button",{name:/展开|收起/});
      if(await toggle.getAttribute("aria-expanded")==="false")await toggle.click();
      const peer=page.locator('.tree-group-person[data-remote-id="'+linked+'"]');
      const railTopic=page.locator('[data-nav-topic="'+group+'"]');
      if(process.argv.includes("--lender")){
        const badge=railTopic.getByRole("button",{name:"@甲组组员 协作中 1"});
        assert.equal(await badge.getAttribute("aria-expanded"),"false");
        assert.equal(await railTopic.getByText("协作小组",{exact:true}).count(),0);
        await badge.click();
        await peer.waitFor();
        assert.equal(await peer.locator("xpath=ancestor::li[@data-cooperation-owner]").getAttribute("data-cooperation-owner"),member);
        await badge.click();
        await watchFlight(page);
        const endpoint=await badge.boundingBox();
        await page.evaluate(({topic,target,source})=>window.dispatchEvent(new CustomEvent("ff:chat-flight",{detail:{
          topic,source,receipts:[{target}]
        }})),{topic:group,source:member,target:linked});
        await page.waitForFunction(()=>window.chatFlightFrames.length>0);
        const [frame]=await page.evaluate(()=>window.chatFlightFrames);
        assert(frameAt(frame[1],endpoint),"folded loan receives its envelope on the owner's badge");
        return;
      }
      await peer.waitFor();
      assert.equal((await peer.boundingBox()).height,28);
      assert((await peer.locator("xpath=ancestor::details[1]/summary").textContent()).startsWith("甲组 · "));
      assert.equal(await railTopic.locator(".tree-group-person").filter({hasText:"@甲组组长"}).count(),0);
      await peer.dblclick();
      const panel=page.getByRole("dialog",{name:"小组合作"});
      await panel.getByText("乙组 ↔ 甲组",{exact:true}).waitFor();
      await panel.getByRole("button",{name:"关闭小组合作"}).click();
      await input.fill("@甲组组员");
      await chat.getByRole("option",{name:/@甲组组员/}).waitFor();
      assert.deepEqual(errors,[]);
      console.log("PASS Chromium permanent group tree, fixed row height, @ selector and Base marker");
      return;
    }
    const post = text => chat.locator(".chat-message").filter({hasText:text});
    const repo = page.locator(".project-select").filter({has:page.locator('strong[data-path$="repo"]')});
    await repo.waitFor();
    if (await repo.getAttribute("aria-expanded")==="false") await repo.click();
    await page.locator('.topic-choice[data-topic="'+group+'"] .topic-label').click();
    await input.waitFor();
    await chat.locator(".current-topic-title").filter({hasText:/^开发$/}).waitFor();
    await post("普通记录，无 Agent 调用").waitFor();
    const fileToggle=page.getByRole("button",{name:"切换右侧文件栏",exact:true});
    assert(await fileToggle.isVisible() && await page.locator("#inspector-shell").isVisible());
    await fileToggle.click();
    const expanded=(await post("普通记录，无 Agent 调用").boundingBox()).width;
    assert(expanded>800);
    await page.keyboard.press("Control+Alt+b");
    assert(await page.locator("#inspector-shell").isVisible());
    assert((await post("普通记录，无 Agent 调用").boundingBox()).width<expanded);
    const note = "浏览器发群记录";
    await input.fill(note);
    await input.press("Shift+Enter");
    await page.keyboard.insertText("第二行");
    assert.equal(await input.inputValue(),note+"\n第二行");
    assert.equal(await post(note).count(),0);
    await input.press("Enter");
    await post(note).waitFor();
    assert.equal(await post(note).locator(".chat-text").textContent(),note+"\n第二行");
    assert.equal(await post(note).locator(".chat-receipts").count(),0);
    assert.equal(await post(note).locator(".chat-destination").getAttribute("title"),"发给 群聊");
    const route=async(text,from,to)=>{
      const head=post(text).locator(".chat-message-body > header");
      assert.equal(await head.locator("strong").innerText(),from);
      assert.equal((await head.locator(".chat-destination").innerText()).trim(),"→ "+to);
    };
    await route("组长多选","@组长","@开发、@测试组长");
    await route("开发完成","@开发","我");

    // 该用例由数据库构造成员，工作台不会为这些成员起真实 CLI 进程。在视图边界给一位成员设置在线状态。
    const stateApi=url=>url.pathname==="/api/state";
    const onlineFixture=async route=>{
      const response=await route.fetch();
      const data=await response.json();
      const windowView=data.windows.find(view=>view.agent.id===member);
      if(windowView){windowView.agent.online=true;windowView.activity="working";}
      await route.fulfill({response,json:data});
    };
    await page.route(stateApi,onlineFixture);
    await page.evaluate(async()=>{await refresh();await refresh();});
    await input.fill("@");
    await chat.getByRole("option", {name:/@离线成员/}).waitFor();
    const options=await chat.getByRole("option").allTextContents();
    const onlineIndex=options.findIndex(item=>item.includes("@开发"));
    assert(onlineIndex>=0 && onlineIndex<options.findIndex(item=>item.includes("@离线成员")),options.join(" / "));
    const animation=lamp=>lamp.evaluate(node=>[getComputedStyle(node).animationName,getComputedStyle(node).animationDuration]);
    assert.deepEqual(await animation(chat.getByRole("option", {name:/@开发/}).locator(".nav-dot")),["agent-breathe","0.65s"]);
    assert.deepEqual(await animation(post("开发完成").locator(".chat-sender-status .nav-dot")),["agent-breathe","0.65s"]);
    await page.unroute(stateApi,onlineFixture);
    await input.fill("请看看接口");
    await input.evaluate(field=>field.setSelectionRange(1,1));
    await page.keyboard.insertText("@开");
    await chat.getByRole("option", {name:/@开发/}).waitFor();
    await input.press("Enter");
    assert.equal(await input.inputValue(),"请@开发 看看接口");
    assert.equal(await chat.getByRole("checkbox",{name:"需要回报结果"}).count(),0);
    await input.press("End");
    const ime=await context.newCDPSession(page);
    await ime.send("Input.imeSetComposition",{text:"ceshi",selectionStart:5,selectionEnd:5});
    assert(await input.evaluate(node=>node.dispatchEvent(new KeyboardEvent("keydown",{
      key:"Enter",isComposing:true,bubbles:true,cancelable:true
    }))));
    await ime.send("Input.imeSetComposition",{text:"测试",selectionStart:2,selectionEnd:2});
    await ime.send("Input.insertText",{text:"测试"});
    await ime.detach();
    await page.keyboard.insertText(" 请找 #离");
    await chat.getByRole("option",{name:/#离线成员/}).waitFor();
    await input.press("Enter");
    await page.keyboard.insertText("核对");
    assert.equal(await chat.locator(".chat-editor .chat-address").textContent(),"@开发");
    assert.equal(await chat.locator(".chat-editor .chat-reference").textContent(),"#离线成员");
    await chat.getByRole("button",{name:"发送",exact:true}).waitFor();
    const localBranch=page.locator('.topic-choice[data-topic="'+group+'"]').getByRole("button",{name:/展开|收起/});
    if(await localBranch.getAttribute("aria-expanded")==="false") await localBranch.click();
    const memberRowInGroup=page.locator('[data-nav-topic="'+group+'"] .tree-agent[data-agent="'+member+'"]');
    const leadRow=page.locator('[data-nav-topic="'+group+'"] .tree-agent').filter({hasText:"@组长"});
    await watchFlight(page);
    await input.press("Enter");
    const inline="请@开发 看看接口测试 请找 #离线成员 核对";
    await post(inline).locator(".chat-receipts").waitFor();
    await route(inline,"我","@开发");
    assert.equal(await post(inline).locator(".chat-text").textContent(),inline);
    assert.equal(await post(inline).locator(".chat-text .chat-address").textContent(),"@开发");
    assert.equal(await post(inline).locator(".chat-text .chat-reference").textContent(),"#离线成员");
    const response = await page.request.get(base+"/api/topics/"+group+"/chat");
    const addressed = (await response.json()).find(item=>item.text===inline);
    assert.deepEqual(addressed.receipts.map(receipt=>receipt.target),[member]);
    await page.waitForFunction(()=>window.chatFlightFrames.length===1);
    await page.waitForFunction(()=>!document.querySelector(".chat-envelope-flight"));
    const [outbound]=await page.evaluate(()=>window.chatFlightFrames);
    const composerBox=await input.boundingBox(),memberBox=await memberRowInGroup.boundingBox();
    assert(frameAt(outbound[0],composerBox)&&frameAt(outbound[1],memberBox),
      "composer to chosen member: "+JSON.stringify({outbound,composerBox,memberBox}));
    assert.equal(outbound[3],false,"only the envelope is painted");
    assert(outbound[4]>=1050 && outbound[4]<=1850,"flight uses the former arc-length timing");
    const roll=Number(outbound[1].match(/rotate\((-?\d+)deg\)/)[1]);
    assert(Math.abs(roll)>=360 && Math.abs(roll)<=1800 && roll%360===0,"envelope lands level");
    await watchFlight(page);
    sendAsLeader({target:member,message:"成员间弧线测试"});
    await post("成员间弧线测试").waitFor();
    await page.waitForFunction(()=>window.chatFlightFrames.length===1);
    const [between]=await page.evaluate(()=>window.chatFlightFrames);
    assert(frameAt(between[0],await leadRow.boundingBox())&&frameAt(between[1],await memberRowInGroup.boundingBox()),"member to member");
    const rail=await page.locator("#project-tabs").boundingBox();
    assert(between[2]>rail.x+rail.width,"path leaves the tree before returning");
    await page.waitForFunction(()=>!document.querySelector(".chat-envelope-flight"));
    await watchFlight(page);
    sendAsLeader({target:"@工作台用户",message:"新交付回复飞行测试",files:["接口说明.md"]});
    const replyHead=post("新交付回复飞行测试").locator(".chat-message-body > header");
    await replyHead.waitFor();
    await page.waitForFunction(()=>window.chatFlightFrames.length===1);
    const [inbound]=await page.evaluate(()=>window.chatFlightFrames);
    const edge=await chat.boundingBox();
    const end=inbound[1].match(/translate\((-?\d+)px,\s*(-?\d+)px\)/);
    assert(frameAt(inbound[0],await leadRow.boundingBox())&&Math.abs(Number(end[1])+9-edge.x)<2&&
      Number(end[2])+6>=edge.y&&Number(end[2])+6<=edge.y+edge.height,"reply lands on the near border of the chat");
    await page.waitForFunction(()=>!document.querySelector(".chat-flight-trail,.chat-envelope-flight,.chat-flick-hand"));
    await watchFlight(page,"reduce");
    await page.evaluate(id=>window.dispatchEvent(new CustomEvent("ff:chat-flight",{detail:{
      topic:selectedTopicId,source:"workbench-user",receipts:[{target:id}]
    }})),member);
    await memberRowInGroup.evaluate(row=>Promise.all(row.getAnimations().map(motion=>motion.finished)));
    assert.equal(await page.evaluate(()=>window.chatFlightFrames.length),0,"reduce motion only flashes the recipient");
    await watchFlight(page);

    await input.fill("请记下 #离");
    await chat.getByRole("option",{name:/#离线成员/}).click();
    await input.press("Enter");
    await post("请记下 #离线成员").waitFor();
    assert.equal(await post("请记下 #离线成员").locator(".chat-receipts").count(),0);
    assert.equal(await page.evaluate(()=>window.chatFlightFrames.length),0,"reference-only post has no flight");

    await input.fill("请@开");
    await chat.getByRole("option", {name:/@开发/}).click();
    await input.evaluate(field=>field.setSelectionRange(0,0));
    await page.keyboard.insertText("仅引用 @开发 ");
    await chat.getByRole("button", {name:"发送",exact:true}).waitFor();
    await input.evaluate(field=>{
      const selected=field.value.lastIndexOf("@开发");
      field.setSelectionRange(selected,selected+"@开发".length);
    });
    await input.press("Backspace");
    await page.keyboard.insertText("已删除点名");
    await chat.getByRole("button", {name:"发送",exact:true}).click();
    await post("仅引用 @开发 请已删除点名").waitFor();
    assert.equal(await post("仅引用 @开发 请已删除点名").locator(".chat-receipts").count(),0);

    await input.fill("截图测试");
    const photo = Buffer.from("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+X8YUAAAAASUVORK5CYII=","base64");
    await input.evaluate((field,bytes)=>{
      const transfer = new DataTransfer();
      transfer.items.add(new File([Uint8Array.from(bytes)],"截图.png",{type:"image/png"}));
      field.dispatchEvent(new ClipboardEvent("paste",{bubbles:true,cancelable:true,clipboardData:transfer}));
    },[...photo]);
    await chat.getByRole("button", {name:/截图.png/}).waitFor();
    await chat.getByRole("button", {name:"发送",exact:true}).click();
    await post("截图测试").getByRole("button", {name:"预览 截图.png",exact:true}).click();
    const inspector=page.locator("#inspector-shell");
    await inspector.getByRole("img",{name:"截图.png",exact:true}).waitFor();
    const delivery=post("成果清单");
    assert.equal(await delivery.locator(".chat-destination").getAttribute("title"),"发给 我");
    await delivery.getByRole("button",{name:"接口说明.md",exact:true}).click();
    await inspector.locator(".markdown-preview").getByText("approved project attachment").waitFor();
    await page.setViewportSize({width:2400,height:900});
    const divider=inspector.getByRole("separator",{name:"文件列表宽度"});
    const bounds=await divider.boundingBox();
    assert(bounds,"wide inspector exposes file tree divider");
    await page.mouse.move(bounds.x+3,bounds.y+10);
    await page.mouse.down();
    await page.mouse.move(bounds.x+65,bounds.y+10,{steps:4});
    await page.mouse.up();
    const dragged=Number(await divider.getAttribute("aria-valuenow"));
    assert(dragged>248 && await page.evaluate(()=>Number(localStorage.getItem("ff-file-tree-width")))===dragged);
    await divider.press("ArrowLeft");
    assert.equal(Number(await divider.getAttribute("aria-valuenow")),dragged-16);
    await page.setViewportSize({width:1460,height:900});
    await delivery.getByRole("button",{name:"发布.zip",exact:true}).click();
    await inspector.locator('.file-tree [aria-current="page"][title="src/成果 文件/发布.zip"]').waitFor();
    assert.equal(await inspector.getByRole("tab",{name:"发布.zip",exact:true}).count(),0);
    const url="https://example.org/releases/v1?q=1#download";
    await context.route("https://example.org/**",route=>route.fulfill({body:"fixture"}));
    const [opened]=await Promise.all([context.waitForEvent("page"),delivery.getByRole("link",{name:url,exact:true}).click()]);
    await opened.waitForURL(url);
    assert.equal(await opened.evaluate(()=>window.opener),null);
    await opened.close();
    await menu("小组").click();
    await page.locator(".workbench-menu").getByRole("button", {name:"小组合作",exact:true}).click();
    const collaboration=page.getByRole("dialog",{name:"小组合作"});
    await collaboration.getByRole("checkbox",{name:"关联 测试",exact:true}).check();
    const peerMembers=collaboration.getByLabel("测试 · 参与成员范围");
    assert.equal(await peerMembers.inputValue(),"all");
    await peerMembers.selectOption("selected");
    await collaboration.getByRole("checkbox",{name:"@测试成员",exact:true}).check();
    await collaboration.getByRole("button",{name:"保存合作设置"}).click();
    await collaboration.waitFor({state:"hidden"});
    await input.fill("@测试成员");
    await chat.getByRole("option",{name:/测试成员/}).waitFor();
    await input.fill("");
    await watchFlight(page);
    const linkedState=await page.request.get(base+"/api/state").then(r=>r.json());
    const target=linkedState.windows.find(w=>w.agent.name==="测试成员").agent.id;
    const partner=page.locator('[data-nav-topic="'+group+'"] [data-remote-id="'+target+'"]');
    await partner.waitFor();
    const origin=await input.boundingBox();
    const endpoint=await partner.boundingBox();
    const linkedStatus=await page.request.post(base+"/api/topics/"+group+"/chat",{
      data:{text:"左侧跨组动画",targets:[member,target]}
    });
    assert.equal(linkedStatus.status(),200);
    await post("左侧跨组动画").waitFor();
    await page.waitForFunction(()=>window.chatFlightFrames.length>=2);
    await page.waitForFunction(()=>!document.querySelector(".chat-envelope-flight"));
    const frames=await page.evaluate(()=>window.chatFlightFrames);
    assert.equal(frames.length,2);
    assert(frames.every(frame=>frameAt(frame[0],origin)));
    assert(frames.some(frame=>frameAt(frame[1],endpoint)));
    const localEnd=await memberRowInGroup.boundingBox();
    assert(frames.some(frame=>frameAt(frame[1],localEnd)));
    const groupRow = page.locator('.topic-choice[data-topic="'+group+'"]');
    const branch = groupRow.getByRole("button",{name:/展开|收起/});
    if (await branch.getAttribute("aria-expanded")==="false") await branch.click();
    await page.locator('#project-tabs .tree-agent[data-agent="'+member+'"] .member-name').click();
    await page.locator("#workspace-layout").waitFor({state:"visible"});
    assert((await chat.boundingBox()).width>=360);
    await menu("查看").click();
    await page.getByRole("button", {name:"代码模式"}).click();
    await chat.waitFor({state:"visible"});
    const fileTree=page.getByRole("navigation",{name:"项目文件"});
    const rootFolder=fileTree.getByRole("button",{name:"src",exact:true});
    if(await rootFolder.getAttribute("aria-expanded")==="false")await rootFolder.click();
    await page.route(url=>url.pathname==="/api/workspace/open-folder",async route=>{
      await route.fulfill({status:204});
    });
    const childFolder=fileTree.getByRole("button",{name:"成果 文件",exact:true});
    const expandedBefore=await childFolder.getAttribute("aria-expanded");
    const openedFolder=page.waitForRequest(url=>new URL(url.url()).pathname==="/api/workspace/open-folder");
    await fileTree.getByRole("button",{name:"在本机打开文件夹 成果 文件",exact:true}).click();
    const opening=await openedFolder;
    assert.equal(opening.method(),"POST");
    assert.equal(new URL(opening.url()).searchParams.get("path"),"src/成果 文件");
    assert.equal(await childFolder.getAttribute("aria-expanded"),expandedBefore);
    const settings = page.getByRole("dialog",{name:"工作台设置"});
    await page.locator("#open-settings").click();
    await settings.getByRole("button",{name:"快捷键",exact:true}).click();
    await settings.locator("#settings-section-shortcuts").waitFor();
    await settings.getByRole("button",{name:"关闭配置"}).click();
    await menu("工具").click();
    await page.getByRole("button",{name:"设置",exact:true}).click();
    await settings.getByRole("button",{name:"常规",exact:true}).waitFor();
    await settings.getByRole("button",{name:"关闭配置"}).click();
    const toggle = page.locator("#inspector-panel-action");
    if (await toggle.getAttribute("aria-label")==="收起 Agent") await toggle.click();
    await page.locator("#workspace-layout").waitFor({state:"hidden"});
    await chat.waitFor({state:"visible"});
    await page.setViewportSize({width:690,height:860});
    await menu("查看").click();
    await page.getByRole("button",{name:"协作工作区"}).click();
    const mark=page.locator("#project-collapse");
    const memberRow=page.locator('#project-tabs .tree-agent[data-agent="'+member+'"] .member-name');
    for(let attempt=0;attempt<2;attempt++) {
      if(await mark.getAttribute("aria-expanded")==="false") await mark.click();
      await memberRow.click();
      await page.locator("#workspace-layout").waitFor({state:"visible"});
      await page.locator("#return-to-chat").click();
      await chat.waitFor({state:"visible"});
    }
    assert.equal(await page.locator("#collab-log").count(),0);
    assert.deepEqual(errors,[]);
  } finally {
    await browser.close();
  }
  console.log("PASS Chromium group notes, explicit mentions, pasted images, preview, and linked leaders");
}

run().catch(error => { console.error(error); process.exitCode=1; });
