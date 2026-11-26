const assert = require("node:assert/strict");
const path = require("node:path");
const {chromium} = require("playwright");
const [base, , root] = process.argv.slice(2);
(async () => {
  const browser = await chromium.launch({executablePath:process.env.CHROME_PATH});
  try {
    const page = await browser.newPage({viewport:{width:1440,height:900}});
    const post = async (route, data) => {
      const res = await page.request.post(base + "/api" + route, {data});
      assert(res.ok(), await res.text());
      return res.status() === 204 ? null : res.json();
    };
    const topic = await post("/topics", {name:"建窗响应",project:path.join(root,"one")});
    for (let i=0; i<4; i++) await post("/windows", {topic:topic.id, name:"终端"+i, engine:"claude",newSession:true});
    await page.goto(base);
    await page.waitForFunction(() => window.panes?.size === 4 && [...panes.values()].every(p => p.view.inputReady));
    const [source, target] = await page.evaluate(()=>[...panes.values()].slice(0,2).map(p=>({id:p.agent.id,gen:p.generation})));
    const pick = page.locator("#agents-dialog"), search = page.locator("#peer-search");
    await page.evaluate(id=>panes.get(id).term.focus(),source.id);
    await page.keyboard.press("Alt+Shift+A");
    await pick.waitFor();
    const hierarchy = await page.evaluate(() => {
      const dialog = getComputedStyle(byId("agents-dialog")), input = getComputedStyle(byId("peer-search"));
      const active = document.querySelector('.mention-choice[aria-selected="true"]');
      const idle = document.querySelector('.mention-choice[aria-selected="false"]');
      const marker = getComputedStyle(active, "::before");
      return {
        hasIcon: !!document.querySelector(".peer-search-box svg"),
        blackPanel: dialog.backgroundColor === "rgb(9, 9, 11)" && dialog.backgroundImage === "none",
        distinctSearch: input.backgroundColor !== dialog.backgroundColor,
        distinctSelected: getComputedStyle(active).backgroundColor !== getComputedStyle(idle).backgroundColor,
        selectedChevron: marker.visibility === "visible" && marker.backgroundImage.includes("chevron-right") &&
          marker.filter.includes("drop-shadow") && getComputedStyle(idle, "::before").visibility === "hidden"
      };
    });
    assert.deepEqual(hierarchy, {hasIcon:true, blackPanel:true, distinctSearch:true, distinctSelected:true, selectedChevron:true});
    const initialPeer = await search.getAttribute("aria-activedescendant");
    await search.press("ArrowDown");
    assert.notEqual(await search.getAttribute("aria-activedescendant"), initialPeer);
    assert(await pick.locator('[aria-selected="true"]').evaluate(el => getComputedStyle(el, "::before").visibility === "visible"));
    await search.fill("终端1");
    assert.equal(await search.getAttribute("aria-activedescendant"),"peer-"+target.id);
    const before = await search.boundingBox();
    assert(before.x >= 0 && before.y >= 0 && before.x+before.width <= 1440);
    await page.setViewportSize({width:535,height:760});
    await page.waitForFunction(()=>{const r=byId("agents-dialog").getBoundingClientRect();return r.left>=0 && r.right<=innerWidth && r.bottom<=innerHeight;});
    await post("/windows/"+target.id+"/session", {newSession:true});
    await page.locator("#peer-status").filter({hasText:"重新选择"}).waitFor();
    assert.equal(await search.getAttribute("aria-activedescendant"),null);
    await search.press("Enter");
    assert(await pick.isVisible());
    const stale = await page.request.post(base+"/api/windows/"+source.id+"/mention", {data:{
      sourceTerminal:source.gen,targetId:target.id,targetTerminal:target.gen}});
    assert.equal(stale.status(),409);
    await search.press("Escape");
    await pick.waitFor({state:"hidden"});
    await page.setViewportSize({width:1440,height:900});
    await page.waitForFunction(()=>[...panes.values()].every(p=>p.view.inputReady));
    await page.evaluate(() => history.pushState({}, "", "#manual-tab"));
    const other = await browser.newPage({viewport:{width:1440,height:900}});
    await other.goto(base);
    await other.waitForFunction(id=>window.panes?.get?.(id)?.view.inputReady,source.id);
    await page.waitForURL("**/page-inactive.html");
    assert.equal(await page.locator(".xterm, script").count(),0);
    assert.equal(await other.evaluate(id=>panes.get(id).generation,source.id),source.gen);
    const popupReady = other.waitForEvent("popup");
    await other.evaluate(() => window.open("/", "_blank"));
    const popup = await popupReady;
    await popup.waitForFunction(id=>window.panes?.get?.(id)?.view.inputReady,source.id);
    const popupClosed = popup.waitForEvent("close");
    await page.goto(base);
    await popupClosed;
    await page.bringToFront();
    await page.waitForFunction(id=>window.panes?.get?.(id)?.view.inputReady,source.id);
    assert.equal(await page.evaluate(id=>panes.get(id).generation,source.id),source.gen);
    const spare = await post("/windows",{topic:topic.id,name:"临时来源",engine:"claude",newSession:true});
    await page.waitForFunction(id=>panes.get(id)?.view.inputReady,spare.id);
    await page.evaluate(id=>openPeers(panes.get(id)),spare.id);
    await pick.waitFor();
    assert((await page.request.delete(base+"/api/windows/"+spare.id)).ok());
    await pick.waitFor({state:"hidden"});
    await page.locator("#mention-notice").filter({hasText:"@临时来源"}).waitFor();
    await page.waitForFunction(()=>panes.size===4);
    const typeChineseOnFocusedTerminal = async (first, second) => {
      const state = await page.evaluate(async ([id, a, b]) => {
        const pane = panes.get(id), textarea = pane.term.textarea;
        if (document.activeElement !== textarea) return {focused:false, keyDownSeen:pane.term._core?._keyDownSeen};
        textarea.dispatchEvent(new CompositionEvent("compositionstart", {bubbles:true, data:""}));
        textarea.value += a;
        textarea.dispatchEvent(new CompositionEvent("compositionupdate", {bubbles:true, data:a}));
        textarea.dispatchEvent(new CompositionEvent("compositionend", {bubbles:true, data:a}));
        await new Promise(resolve => setTimeout(resolve, 10));
        textarea.dispatchEvent(new InputEvent("input", {bubbles:true, composed:true, inputType:"insertText", data:b}));
        return {focused:true, keyDownSeen:Boolean(pane.term._core?._keyDownSeen), inert:Boolean(textarea.closest("[inert]"))};
      }, [source.id, first, second]);
      assert.deepEqual(state, {focused:true, keyDownSeen:false, inert:false});
    };
    const sourcePane = page.locator(`.pane[data-window="${source.id}"]`);
    await page.evaluate(id => panes.get(id).term.focus(), source.id);
    await page.keyboard.insertText("草稿A ");
    await page.waitForFunction(id => observePrompt(panes.get(id)).editableText?.includes("草稿A"), source.id);
    await sourcePane.locator(".insert-agent").click();
    await pick.waitFor();
    await page.locator("#peer-" + target.id).click();
    await pick.waitFor({state:"hidden"});
    await page.waitForFunction(id => observePrompt(panes.get(id)).editableText?.includes("草稿A @终端1"), source.id);
    await typeChineseOnFocusedTerminal("请核对", "鼠标选人");
    await page.waitForFunction(id => observePrompt(panes.get(id)).editableText?.includes("草稿A @终端1 请核对鼠标选人"), source.id);
    await page.keyboard.press("Control+u");
    await page.waitForFunction(id => !observePrompt(panes.get(id)).editableText, source.id);
    await page.keyboard.insertText("草稿B ");
    await page.waitForFunction(id => observePrompt(panes.get(id)).editableText?.includes("草稿B"), source.id);
    await page.keyboard.press("Alt+Shift+A");
    await pick.waitFor();
    await search.press("ArrowDown");
    await search.press("ArrowUp");
    await search.press("Enter");
    await pick.waitFor({state:"hidden"});
    await page.waitForFunction(id => observePrompt(panes.get(id)).editableText?.includes("草稿B @终端1"), source.id);
    await typeChineseOnFocusedTerminal("连续输入", "回车选人");
    await page.waitForFunction(id => observePrompt(panes.get(id)).editableText?.includes("草稿B @终端1 连续输入回车选人"), source.id);
    await page.keyboard.press("Control+u");
    await page.waitForFunction(id => !observePrompt(panes.get(id)).editableText && panes.get(id).view.inputReady, source.id);
    const animation = await page.evaluate(async () => {
      const [source, target] = [...panes.values()];
      const flight = flyMessage({source:source.agent.id,target:target.agent.id});
      const hand = document.querySelector(".message-flick-hand");
      const envelope = document.querySelector(".message-flight");
      const mailbox = document.querySelector(".message-mailbox");
      const slot = mailbox.querySelector(".mailbox-slot"), flag = mailbox.querySelector(".mailbox-flag");
      const figures = [hand,envelope,mailbox].every(el=>el instanceof SVGSVGElement);
      const coordinates = () => envelope.style.offsetPath.match(/-?\d+(?:\.\d+)?/g).map(Number);
      const close = (a,b) => Math.abs(a-b)<1;
      const arrived = () => {
        const [x,y] = coordinates().slice(-2), r = slot.getBoundingClientRect();
        return close(x,r.left+r.width/2) && close(y,r.top+r.height/2);
      };
      const handFirst = hand.getAnimations().length===1 && !envelope.getAnimations().length && getComputedStyle(mailbox).opacity==="0";
      const preparation = hand.getAnimations()[0];
      const gripFrames = preparation.effect.getKeyframes();
      const deliberateGrip = preparation.effect.getTiming().duration >= 700 &&
        gripFrames[1].offset >= .3 && gripFrames[0].transform === gripFrames[1].transform &&
        getComputedStyle(hand.querySelector(".flick-thumb")).fill === "rgb(56, 189, 248)";
      await preparation.finished;
      const motion = envelope.getAnimations()[0];
      const frames = motion.effect.getKeyframes();
      const [x,y,,,entryX,entryY,sx,sy] = coordinates();
      const distance = Math.hypot(sx-x,sy-y), lift = Math.min(140,Math.max(56,distance*.28));
      const length = Math.hypot(distance,lift*1.15), degrees = (sx>=x?1:-1)*Math.min(5,Math.max(1,Math.round(length/300)))*360;
      const time = Math.max(1050,Math.min(1850,Math.round(720+length*.62)));
      const rotate = parseFloat(frames.at(-1).transform.match(/rotate\(([-.\d]+)/)[1]);
      const flightOK = arrived() && rotate===degrees && frames.at(-1).opacity==="0" &&
        frames.at(-1).transform.includes("scale(0.42)") && getComputedStyle(envelope).offsetRotate==="0deg" &&
        time===motion.effect.getTiming().duration && close(entryY,sy) && entryX!==sx;
      const reveal = mailbox.getAnimations()[0];
      const welcome = close(reveal.effect.getTiming().delay, time*.35);
      target.element.querySelector(".toggle-collapse").click();
      await motion.finished;
      const anchor = messageAnchor(target.agent.id), r = anchor.getBoundingClientRect(), [endX,endY] = coordinates().slice(-2);
      const tab = anchor.closest(".topic-agent-tag");
      const followed = anchor.dataset.agent === target.agent.id && !!tab && close(endX,r.left+r.width/2) && close(endY,r.top+r.height/2);
      const sweep = tab?.querySelector(":scope > .receipt-sweep");
      const box = tab?.getBoundingClientRect(), outline = sweep?.getBoundingClientRect();
      const outlinedCard = !!outline && target.element.dataset.collapsed === "true" &&
        Math.abs(outline.width - box.width) <= 2 && Math.abs(outline.height - box.height) <= 2 &&
        sweep.querySelectorAll(".receipt-highlight").length === 2 &&
        sweep.querySelector(".receipt-outline").getAnimations().some(a => a.animationName === "receipt-flash");
      const flagFrames = flag.getAnimations()[0]?.effect.getKeyframes(), catchFrames = mailbox.getAnimations().at(-1)?.effect.getKeyframes();
      const caught = flagFrames?.at(-1).transform==="rotate(0deg)" && catchFrames.some(f=>f.transform==="scale(1.14, 0.9)");
      const noTrail = !document.querySelector(".message-trail");
      await flight;
      target.element.querySelector(".toggle-collapse").click();
      const remaining = document.querySelectorAll(".message-flick-hand,.message-flight,.message-mailbox,.receipt-sweep").length;
      return {figures,handFirst,deliberateGrip,flightOK,welcome,followed,outlinedCard,caught,noTrail,remaining,time,rotate};
    });
    console.log("Handoff animation",animation);
    for (const name of ["figures","handFirst","deliberateGrip","flightOK","welcome","followed","outlinedCard","caught","noTrail"]) assert.equal(animation[name],true,name);
    assert.equal(animation.remaining,0);
    await page.setViewportSize({width:2200,height:900});
    await page.waitForFunction(()=>[...panes.values()].every(p=>!p.layoutFlight &&
      p.element.getAnimations().every(a=>a.playState!=="running")));
    const spans = await page.evaluate(async () => {
      const [a,b] = [...panes.values()].map(p=>p.agent.id), points = [messageAnchor(a),messageAnchor(b)];
      const original = points.map(el=>el.style.cssText);
      const readings = [];
      try {
        for (const [left,right] of [[100,760],[2090,90]]) {
          // 同一真实收发窗格，分别布置中距与超长反向路径检验转速和上下限。
          for (const [i,el] of points.entries()) {
            Object.assign(el.style,{position:"fixed",left:"0px",top:"0px",width:"28px"});
            // 窗格的 contain 建立独立坐标系。测量原点，将本轮指定的屏幕坐标换算到真实锚点上。
            const origin=el.getBoundingClientRect();
            el.style.left=((i?right:left)-origin.left)+"px";
            el.style.top=(320-origin.top)+"px";
          }
          const dx=points[1].getBoundingClientRect().left-points[0].getBoundingClientRect().left;
          if(Math.abs(dx-(right-left))>2) throw new Error("投递测试锚点尚未定位到指定距离");
          const flight = flyMessage({source:a,target:b});
          await document.querySelector(".message-flick-hand").getAnimations()[0].finished;
          const movement = document.querySelector(".message-flight").getAnimations()[0];
          const angle = movement.effect.getKeyframes().at(-1).transform.match(/rotate\(([-.\d]+)/)[1];
          readings.push([Number(angle),movement.effect.getTiming().duration]);
          await flight;
        }
      } finally { points.forEach((el,i)=>{el.style.cssText=original[i];}); }
      return readings;
    });
    console.log("Mid-range and long reverse flights",spans);
    assert.equal(spans[0][0],720);
    assert(spans[0][1]>1050 && spans[0][1]<1850);
    assert.deepEqual(spans[1],[-1800,1850]);
    await page.setViewportSize({width:1440,height:900});
    await page.route("**/api/models/codex?*", async route => {
      await new Promise(resolve=>setTimeout(resolve,4000));
      await route.fulfill({json:[]}).catch(()=>{});
    });
    const marker = path.join(root, "one", "signed-out.fixture");
    const fs = require("node:fs");
    fs.writeFileSync(marker, "");
    await page.locator("#new-agent").click();
    await page.getByRole("radio", {name:"TRAE CLI"}).check();
    const login = page.locator("#open-engine-login");
    await login.waitFor();
    assert.match(await page.locator("#trae-create-notice").innerText(), /企业旗舰版/);
    assert.equal(await page.locator("#mode-label").innerText(), "审批权限");
    assert(await page.locator("#create-agent").isDisabled() && await page.locator("#session-source").isDisabled());
    const count = async () => (await (await page.request.get(base+"/api/state")).json()).windows.length;
    const beforeLogin = await count();
    await login.click();
    await page.locator("#engine-login-dialog .xterm").waitFor();
    await page.locator("#engine-login-dialog [data-close]").click();
    await page.locator("#engine-login-dialog").waitFor({state:"hidden"});
    assert.equal(await count(),beforeLogin);
    assert(await page.locator("#session-source").isDisabled());
    await login.click();
    await page.locator("#engine-login-terminal textarea").focus();
    await page.waitForFunction(() => document.querySelector("#engine-login-terminal .xterm-rows")?.textContent.includes("Fixture login terminal"));
    await page.keyboard.type("login");
    await page.keyboard.press("Enter");
    await page.locator("#engine-login-dialog").waitFor({state:"hidden"});
    await page.waitForFunction(() => !document.getElementById("session-source").disabled);
    assert.equal(await page.locator("#session-source").inputValue(),"");
    assert(await page.locator("#create-agent").isDisabled());
    assert.equal(await count(),beforeLogin);
    await page.locator("#session-source").selectOption("new");
    await page.waitForFunction(() => !document.getElementById("create-agent").disabled);
    await page.locator("#new-dialog [data-close]").first().click();
    console.log("PASS native login cancel, verify and explicit session selection without creating a window");
    let installed = false, finishInstall;
    const holdInstall = new Promise(resolve => { finishInstall = resolve; });
    await page.route("**/api/engines", async route => {
      const response = await route.fetch();
      await route.fulfill({json:{...await response.json(),codebuddy:installed}});
    });
    await page.route("**/api/engines/codebuddy/install", async route => {
      await holdInstall;
      await route.fulfill({status:204});
    });
    await page.route("**/api/engines/codebuddy/installed", route => { installed = true; return route.fulfill({json:{ready:true}}); });
    await page.locator("#new-agent").click();
    const install = page.locator('[data-install="codebuddy"]');
    await install.click();
    await install.waitFor({state:"hidden",timeout:12000});
    assert(installed);
    assert.equal(await page.locator("#new-form input[name=engine]:checked").inputValue(), "codebuddy");
    finishInstall();
    await page.unroute("**/api/engines");
    await page.locator("#new-dialog [data-close]").first().click();
    console.log("PASS installed CLI detected while installer is still exiting");
    await page.evaluate(() => {
      window.timings = []; window.layoutCalls = 0;
      const original = renderLayout;
      renderLayout = function(...args) { if(byId("new-dialog").open) window.layoutCalls++; return original(...args); };
      byId("agent-name").addEventListener("input", event => {
        const start = event.timeStamp;
        requestAnimationFrame(()=>window.timings.push(performance.now()-start));
      });
      // 持续改变底层终端画面，模拟任务日志滚动。
      window.paint = setInterval(() => {
        for (const p of panes.values()) p.term.write("\r\n" + "活动终端输出 ".repeat(12));
      },60);
    });
    await page.locator("#new-agent").click();
    assert.deepEqual(await page.locator("#agent-engine input").evaluateAll(items => items.map(item => item.value)),
      ["codex", "copilot", "claude", "codebuddy", "traecli", "zcode", "qwen", "qoder"]);
    await page.locator("#agent-name").pressSequentially("create-agent-latency-test",{delay:45});
    const available = await page.locator("#create-agent").isEnabled();
    await page.waitForTimeout(1100);
    const result = await page.evaluate(()=>{
      clearInterval(window.paint);
      const times = window.timings.sort((a,b)=>a-b);
      return {keys:times.length,p95Ms:Math.round(times[Math.floor(times.length*.95)]),layoutCalls:window.layoutCalls,
        backdrop:getComputedStyle(byId("new-dialog"),"::backdrop").backdropFilter};
    });
    assert.equal(await page.locator("#agent-name").inputValue(),"create-agent-latency-test");
    console.log("Create dialog under terminal output:", JSON.stringify({...result,defaultAvailable:available}));
    await page.route("**/api/sessions/codex?*", route => route.fulfill({json:[
      {id:"free",name:"可继续"}, {id:"bound",name:"正在使用的原会话",mayBeInUse:true}
    ]}));
    await page.locator("#session-source").selectOption("history");
    await page.locator("#history-session").selectOption("free",{timeout:3000});
    assert.equal(await page.locator("#history-session option[value=bound]").count(),0);
    await page.getByRole("radio",{name:"创建分支"}).check();
    await page.locator("#history-session").selectOption("bound");
    assert(await page.locator("#create-agent").isEnabled());
    let body;
    await page.route("**/api/windows/fork", async route => {
      body = route.request().postDataJSON();
      await route.fulfill({status:409,json:{error:"测试结束"}});
    });
    await page.locator("#create-agent").click();
    await page.locator("#create-error").filter({hasText:"测试结束"}).waitFor();
    assert.equal(body.forkSession,true);
    assert.equal(body.session,"bound");
    assert.equal(body.newSession,false);
    await page.keyboard.press("Escape");
    assert.equal(result.layoutCalls,0);
    assert.equal(result.backdrop,"none");
    assert(available,"engine default blocked by outstanding model catalog");
    assert.equal(result.keys,25);
  } finally { await browser.close(); }
})().catch(error=>{console.error(error);process.exitCode=1;});
