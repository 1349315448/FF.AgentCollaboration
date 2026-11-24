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
      return res.json();
    };
    const topic = await post("/topics", {name:"建窗响应",project:path.join(root,"one")});
    for (let i=0; i<4; i++) await post("/windows", {topic:topic.id, name:"终端"+i, engine:"claude",newSession:true});
    await page.goto(base);
    await page.waitForFunction(() => panes.size === 4 && [...panes.values()].every(p => p.view.inputReady));
    const flights = await page.evaluate(async () => {
      const [source, target] = [...panes.values()];
      const samples = [];
      const observer = new MutationObserver(() => {
        const envelope = document.querySelector(".message-flight");
        if (!envelope || samples.some(s=>s.element===envelope)) return;
        const anchor = messageAnchor(target.agent.id).getBoundingClientRect();
        const points = envelope.style.offsetPath.match(/-?\d+(?:\.\d+)?/g).map(Number);
        samples.push({element:envelope,svg:!!envelope.querySelector("svg"),time:envelope.getAnimations()[0].effect.getTiming().duration,
          hit:Math.abs(points.at(-2)-anchor.x-anchor.width/2)<2 && Math.abs(points.at(-1)-anchor.y-anchor.height/2)<2,
          folded:!!messageAnchor(target.agent.id).closest("#collapsed-panes")});
      });
      observer.observe(document.body,{childList:true});
      const flight = flyMessage({source:source.agent.id,target:target.agent.id});
      await new Promise(resolve=>setTimeout(resolve,380));
      target.element.querySelector(".toggle-collapse").click();
      await flight;
      observer.disconnect();
      target.element.querySelector(".toggle-collapse").click();
      return samples.map(({svg,time,hit,folded})=>[svg,time,hit,folded]);
    });
    assert.deepEqual(flights,[[true,1150,true,false],[true,1150,true,true]]);
    await page.route("**/api/models/codex?*", async route => {
      await new Promise(resolve=>setTimeout(resolve,4000));
      await route.fulfill({json:[]}).catch(()=>{});
    });
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
