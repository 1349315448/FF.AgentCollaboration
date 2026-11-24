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
  const res = JSON.parse(execFileSync(exe, ["workbench-mcp", path.dirname(db)], {
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
async function run() {
  const browser = await chromium.launch({executablePath:process.env.CHROME_PATH || undefined});
  try {
    const page = await browser.newPage({viewport:{width:1460,height:940}});
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
        window.effects.push({classes:this.className,path:this.style.offsetPath,frames,center:[rect.x+rect.width/2,rect.y+rect.height/2],
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
    await page.goto(base);
    await page.locator("#setup-dialog[open]").waitFor();
    assert(!fs.existsSync(db));
    assert.equal((await page.request.get(base + "/api/state")).status(), 409);
    await page.keyboard.press("Escape");
    await page.locator("#setup-archive_root").locator("..").getByRole("button").click();
    await page.locator("#folder-dialog[open]").waitFor();
    await page.keyboard.press("Escape");
    assert(await page.locator("#setup-dialog").isVisible());
    await page.locator("#setup-submit").click();
    await page.waitForFunction(() => !!snapshot);
    const post = async (url, body) => {
      const res = await page.request.post(base + "/api" + url, {data:body});
      assert(res.ok(), await res.text());
      return res.status() === 204 ? null : res.json();
    };
    const pane = id => page.locator('[data-window="' + id + '"]');
    const tab = name => page.locator("#project-tabs button").getByTitle(path.join(root, name), {exact:true});
    async function openProject(name) {
      if (!await page.locator("#project-rail").isVisible()) await page.locator("#project-collapse").click();
      await tab(name).click();
      await page.waitForFunction(path => snapshot.project === path, path.join(root, name));
    }
    async function choose(id) {
      await page.waitForFunction(id => snapshot?.topics.some(topic => topic.id === id), id);
      const name = await page.evaluate(id => folderName(snapshot.topics.find(t => t.id === id).project), id);
      await openProject(name);
      const button = page.locator('.topic-choice[data-topic="' + id + '"]');
      if (await button.isVisible()) await button.click();
      else await page.locator('#mobile-topic').selectOption(id);
    }
    const ready = async id => {
      try { await page.waitForFunction(id => panes.get(id)?.view.inputReady, id); }
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
    const send = (target, message) => mcp("send_to_agent", {target, message}).id;
    const state = id => sql("SELECT state FROM inbox WHERE id=?", id)[0][0];
    const key = async (id, name) => { await page.evaluate(id => panes.get(id).term.focus(), id); await page.keyboard.press(name); };
    const mode = value => fs.writeFileSync(path.join(root, "one", "mode.txt"), value);

    assert.equal(await page.title(),"FF.Agent");
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
    assert.equal(await page.locator(".topbar #new-agent").count(), 0);
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
    const paneBounds = id => pane(id).boundingBox();
    const settleLayout = () => page.waitForFunction(() => !document.querySelector(".pane-flight") &&
      [...panes.values()].every(p => p.layoutMotion?.playState !== "running"));
    const byPosition = await Promise.all(offlineIds.map(async id => ({id, box:await paneBounds(id)})));
    byPosition.sort((a,b) => a.box.x - b.box.x);
    const [first, middle, last] = byPosition.map(item => item.id);
    assert(byPosition.every(item => item.box.y === byPosition[0].box.y));
    await pane(middle).locator(".toggle-collapse").click();
    await settleLayout();
    const gridBox = await page.locator("#panes").boundingBox();
    const [firstBox, foldedBox, lastBox] = await Promise.all([first,middle,last].map(paneBounds));
    assert(await pane(middle).evaluate(el => el.parentElement === document.querySelector("#topic-heading .topic-actions + #collapsed-panes")));
    assert(foldedBox.height >= 40 && foldedBox.height <= 44 && foldedBox.width >= 176 && foldedBox.width <= 220);
    assert(await pane(middle).evaluate(el => {
      const box = selector => el.querySelector(selector).getBoundingClientRect();
      return box(".agent-light").right <= box(".tile-name").left && box(".tile-name").right <= box(".status-label").left &&
        box(".status-label").right <= box(".toggle-collapse").left && box(".tile-engine").top >= box(".tile-name").bottom &&
        box(".pane-quota").top >= box(".toggle-collapse").bottom && box(".tile-engine").right <= box(".pane-quota").left;
    }));
    assert(await page.evaluate(() => window.effects.filter(e => e.duration === 240 && e.frames[0].transform?.includes("scale(")).length >= 3));
    assert.equal(firstBox.y, lastBox.y);
    assert(Math.abs(firstBox.height - gridBox.height) < 1);
    assert(Math.abs(firstBox.width - lastBox.width) < 1 && Math.abs(lastBox.x + lastBox.width - gridBox.x - gridBox.width) < 1);
    await pane(first).locator(".toggle-collapse").click();
    await settleLayout();
    assert(Math.abs((await paneBounds(last)).width - (await page.locator("#panes").boundingBox()).width) < 1);
    await page.emulateMedia({reducedMotion:"reduce"});
    const flightCount = await page.evaluate(() => window.effects.filter(e => e.duration === 240).length);
    for (const id of [first, middle]) await pane(id).locator(".toggle-collapse").click();
    await settleLayout();
    assert(await page.evaluate(() => window.effects.filter(e => e.duration === 240).length) > flightCount);
    assert(await page.locator("#collapsed-panes").isHidden());
    await page.emulateMedia({reducedMotion:"no-preference"});
    const pin = pane(first).locator(".set-primary");
    if (!await pin.isVisible()) await pane(first).locator(".pane-more").click();
    await pin.click();
    const pinnedBox = await paneBounds(first);
    await pane(middle).locator(".toggle-collapse").click();
    await settleLayout();
    const workspace = await page.locator("#workspace-layout").boundingBox();
    const pinned = await paneBounds(first), rightTile = await paneBounds(last);
    assert(await pane(first).evaluate(el => el.parentElement.id === "primary-panes"));
    assert(await page.locator("#pane-divider").isVisible());
    assert.equal(pinnedBox.width, pinned.width);
    assert.equal(workspace.height, pinned.height);
    assert.equal(pinned.y, rightTile.y);
    assert.equal(pinned.height, rightTile.height);
    await pane(middle).locator(".toggle-collapse").click();
    await settleLayout();
    assert(await page.evaluate(() => {
      const flights = window.effects.filter(e => e.classes.includes("pane-flight"));
      return new DOMMatrix(flights.at(-2).frames[0].transform).a > 1 && new DOMMatrix(flights.at(-1).frames[0].transform).a < 1;
    }));
    await pane(offlineIds[0]).getByRole("button", {name:"打开终端", exact:true}).click();
    await ready(offlineIds[0]);
    assert(await pane(offlineIds[0]).locator(".terminal-overlay").isHidden());
    assert(await pane(offlineIds[1]).getByRole("button", {name:"打开终端", exact:true}).isVisible());
    // 模拟两次轮询之间已送达的消息，验证同项目跨话题的两个方向及两次动画。
    await choose(a);
    const localPeer = (await post("/windows", {name:"回执测试", topic:a, engine:"codex", newSession:true})).id;
    await pane(localPeer).waitFor();
    await page.emulateMedia({reducedMotion:"reduce"});
    for (const [source,target] of [[localPeer,first],[first,localPeer]]) {
      await page.evaluate(({source,target}) => {
        window.effects = [];
        const before = {...snapshot, timelineTopic:selectedTopicId, timeline:[]};
        animateMessages(before, {...before, timeline:[{id:"already-delivered", source, target, state:"delivered"}]});
      }, {source,target});
      const envelope = page.locator(".message-flight").first();
      await envelope.waitFor({state:"visible"});
      const appearance = await envelope.evaluate(el => {
        const style = getComputedStyle(el), icon = getComputedStyle(el.querySelector("rect"));
        return [style.width, style.height, icon.fill, icon.stroke, style.filter];
      });
      assert.deepEqual(appearance.slice(0,4), ["28px", "20px", "rgb(2, 132, 199)", "rgb(186, 230, 253)"]);
      assert.match(appearance[4], /drop-shadow\(.*rgb\(56, 189, 248\).*8px/);
      await page.waitForFunction(() => window.effects.filter(e=>e.classes==="message-flight").length === 2 &&
        window.effects.filter(e=>e.classes.includes("topic-choice") && e.duration===320).length >= 2);
      const matched = await page.evaluate(({source,target,first}) => {
        const from = messageAnchor(source).getBoundingClientRect(), to = messageAnchor(target).getBoundingClientRect();
        const coords = window.effects.find(e=>e.classes==="message-flight").path.match(/-?\d+(?:\.\d+)?/g).map(Number);
        return messageAnchor(first).closest(".topic-choice") &&
          Math.abs(coords[0]-from.x-from.width/2)<2 && Math.abs(coords.at(-2)-to.x-to.width/2)<2;
      }, {source,target,first});
      assert(matched);
      await page.waitForFunction(()=>!document.querySelector(".message-flight"));
      assert.equal(await page.evaluate(() => window.effects.filter(e => e.classes === "message-flight").length), 2);
    }
    await page.emulateMedia({reducedMotion:"no-preference"});
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
    await page.locator("#rename-form").getByRole("button", {name:"保存"}).click();
    await page.locator("#rename-error").filter({hasText:"同名"}).waitFor();
    await page.locator("#rename-name").fill("同名");
    await page.locator("#rename-name").press("Enter");
    await pane(left).locator(".tile-name").filter({hasText:"@同名"}).waitFor();
    assert(await page.evaluate(id => panes.get(id).socket === window.beforeRename && window.beforeRename.readyState === WebSocket.OPEN, left));
    const actions = page.locator("#topic-heading .topic-actions");
    const tabUpdates = await page.evaluate(({a, another, left}) => {
      const find = id => document.querySelector('.topic-choice[data-topic="'+id+'"]');
      const topic = selectedTopic(), view = snapshot.windows.find(w => w.agent.id === left);
      const tab = find(a), neighbor = find(another), lamp = tab.querySelector('[data-agent="'+left+'"] .nav-dot');
      const width = tab.offsetWidth, height = tab.offsetHeight, x = neighbor.getBoundingClientRect().left;
      const saved = [topic.activity, view.activity, view.inbox];
      tab.focus({preventScroll:true});
      try {
        return [0, 9, 10, 100].map(n => {
          topic.activity = view.activity = n % 2 ? "working" : "idle";
          view.inbox = Array.from({length:n}, () => ({state:"pending"}));
          renderTopics();
          const box = selector => tab.querySelector(selector).getBoundingClientRect();
          const title = box(".topic-label"), running = box(".topic-running");
          const details = [box(".topic-count"), box(".topic-agents"), box(".tag-inbox")];
          const twoRows = title.top < running.bottom && running.top < title.bottom &&
            details.every(rect => rect.top >= Math.max(title.bottom, running.bottom)) &&
            Math.max(...details.map(r => r.top)) < Math.min(...details.map(r => r.bottom));
          return [tab === find(a) && neighbor === find(another) && lamp.isConnected && document.activeElement === tab,
            tab.offsetWidth === width && tab.offsetHeight === height && neighbor.getBoundingClientRect().left === x,
            twoRows, tab.querySelector(".tag-inbox").textContent];
        });
      } finally { [topic.activity, view.activity, view.inbox] = saved; renderTopics(); }
    }, {a, another, left});
    assert.deepEqual(tabUpdates, ["", "9", "10", "99+"].map(n => [true, true, true, n]));
    const topicWidth = id => page.locator('.topic-choice[data-topic="' + id + '"]').evaluate(tab => tab.offsetWidth);
    assert(await topicWidth(a) > await topicWidth(another), "带两个 Agent 的标签应该随成员名单加宽");
    assert.equal(await actions.locator(":scope > :last-child").getAttribute("id"), "new-agent");
    assert(await actions.evaluate(el => Math.abs(el.getBoundingClientRect().left -
      el.parentElement.getBoundingClientRect().left - parseFloat(getComputedStyle(el.parentElement).paddingLeft)) < 2));
    assert(await page.locator("#topic-heading .member-line").isHidden());
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
    await member.click();
    await ready(left);
    await key(left, "x");
    await page.waitForFunction(id => observePrompt(panes.get(id)).draft && panes.get(id).view.activity === "idle", left);
    await page.evaluate(id => panes.get(id).term.write("\r\n◦ Working (esc to interrupt)" +
      "\r\n工具进度".repeat(10) + "\r\n› ", () => reportPrompt(panes.get(id))), left);
    await page.waitForFunction(id => panes.get(id).view.activity === "working", left);
    assert.equal(await page.locator("#topic-activity").textContent(), "进行中");
    assert(await page.locator("#topic-activity").isVisible());
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
      await started;
      await key(left, "Control+u");
      await page.waitForFunction(id => readNativePrompt(panes.get(id)).screen === "clear", left);
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
    assert(await page.locator("#topic-activity").isHidden());
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
    assert.equal(await toggle.innerText(), "⤢ 展开");
    assert(await pane(left).locator(".terminal-surface").isHidden());
    assert(await header.isVisible() && await pane(left).locator(".terminal-toolbar").isHidden());
    assert(await pane(left).locator(".quota-reset").isHidden());
    remaining = 45.9;
    await pane(left).locator(".pane-quota").click();
    await until(() => quotaRefreshes === 6, "card quota refresh");
    await pane(left).locator(".quota-value").filter({hasText:"45.9%"}).waitFor();
    assert(await pane(left).locator(".quota-reset").isHidden());
    assert((await pane(left).boundingBox()).height < 90);
    await choose(another);
    await choose(a);
    assert.equal(await pane(left).getAttribute("data-collapsed"), "true");
    await member.click();
    assert.equal(await toggle.getAttribute("aria-expanded"), "true");
    assert(await page.evaluate(id => {
      const p = panes.get(id);
      return p.term === beforeCollapse.term && p.socket === beforeCollapse.socket &&
        p.socket.readyState === WebSocket.OPEN && document.activeElement === p.term.textarea;
    }, left));
    await pane(left).locator(".set-primary").click();
    assert(await header.locator(".primary-mark").isVisible());
    await page.waitForFunction(id => layout.primaryWindowId === id, left);
    assert(await toggle.isHidden());
    await pane(helper).locator(".toggle-collapse").click();
    await settleLayout();
    assert(await page.locator("#pane-divider").isHidden() && await page.locator("#collaboration").isHidden());
    const area = await page.locator("#workspace-layout").boundingBox(), main = await paneBounds(left);
    assert(await pane(left).evaluate(el => el.parentElement.id === "primary-panes"));
    assert(Math.abs(main.x - area.x) < 1 && Math.abs(main.height - area.height) < 1 && Math.abs(main.width - area.width) < 1);
    await pane(helper).locator(".toggle-collapse").click();
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
    await pane(left).locator(".pane-sessions").click();
    const drawer = page.locator("#pane-session-dialog[open]");
    await page.waitForFunction(() => window.reducedMotionDrawers.includes("pane-session-dialog"));
    await page.emulateMedia({reducedMotion:"no-preference"});
    await page.mouse.click(12, 90);
    await drawer.waitFor({state:"hidden"});
    await pane(left).locator(".pane-sessions").click();
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
    const switchUrl = "**/api/windows/" + left + "/session";
    await page.route(switchUrl, route => {
      switches.push(route.request().postDataJSON());
      return route.fulfill({status:204});
    });
    await drawer.getByRole("button",{name:"新建会话"}).click();
    await drawer.waitFor({state:"hidden"});
    historyState = "recent";
    await pane(left).locator(".pane-sessions").click();
    await historyRow.getByRole("button",{name:"切换"}).click();
    await drawer.waitFor({state:"hidden"});
    assert.deepEqual(switches, [{newSession:true},{newSession:false, session:"entry"}]);
    await page.unroute(switchUrl);
    await page.unroute("**/api/sessions/codex?*includeBound=true");
    await page.unroute("**/api/windows/" + left + "/history/*");
    await page.locator("#pane-divider").focus();
    await page.keyboard.press("ArrowRight");
    const ratio = await page.evaluate(() => layout.splitRatio);
    assert(await pane(left).locator(".pane-more").isHidden());
    await page.evaluate(id => { window.original = {term:panes.get(id).term,socket:panes.get(id).socket}; },left);
    const topicAPreview = page.locator('.topic-choice[data-topic="' + a + '"] .topic-agents');
    assert(await topicAPreview.isVisible());
    assert.equal(await page.locator('.topic-choice[data-topic="' + a + '"] > .nav-dot').count(), 0);
    await choose(another);
    assert(await topicAPreview.isVisible());
    assert.equal(await topicAPreview.textContent(), "@同名@测试");
    assert(await topicAPreview.evaluate(group => {
      const tab = group.parentElement, first = group.firstElementChild.getBoundingClientRect();
      const total = tab.querySelector(".topic-count"), badge = tab.querySelector(".tag-inbox").getBoundingClientRect();
      return first.left === tab.querySelector(".topic-label").getBoundingClientRect().left &&
        group.lastElementChild.getBoundingClientRect().right <= badge.left &&
        total.getBoundingClientRect().left >= badge.right && total.textContent === "2 位";
    }));
    assert.equal(await topicAPreview.locator('.topic-agent-tag[data-agent="' + left + '"]').getAttribute("data-activity"), "idle");
    await choose(a);
    assert(await topicAPreview.isVisible());
    await topicAPreview.locator('.topic-agent-tag[data-agent="' + helper + '"]').click();
    await page.waitForFunction(({a, helper}) => selectedTopicId === a && layout.activeWindowId === helper &&
      document.activeElement === panes.get(helper).term.textarea, {a,helper});
    assert.equal(await page.locator('.topic-choice[data-topic="' + b + '"]').count(), 0);
    assert.equal(await tab("third").count(), 1); // Agent cwd alone contributes a tab, not a duplicate topic.
    const emptyProject = path.join(root,"empty-project");
    fs.mkdirSync(emptyProject);
    await page.locator("#add-project").click();
    await page.locator("#folder-path").fill(emptyProject);
    await page.locator("#folder-path").press("Enter");
    await page.locator("#choose-folder").click();
    await page.waitForFunction(path => snapshot.project === path, emptyProject);
    assert.equal(await page.locator("#topic-list .topic-choice").count(), 0);
    assert.equal(await page.title(),"FF.Agent");
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
    await pane(left).locator(".insert-agent").click();
    await page.locator("#peer-topic").selectOption(b);
    await page.locator("#agent-options button").click();
    await page.waitForFunction(id=>observePrompt(panes.get(id)).editableText?.includes("@设计/同名"),left);
    await key(left,"Control+u");
    await ready(left);
    await page.evaluate(()=>{window.effects=[];});
    const cross = send("@设计/同名","检查跨话题收件");
    await page.waitForFunction(()=>window.effects.some(e=>e.classes==="message-flight" && e.path.includes(" Q ")));
    assert(await page.evaluate(({helper,b})=>{
      const flight=window.effects.find(e=>e.classes==="message-flight"), coords=flight.path.match(/-?\d+(?:\.\d+)?/g).map(Number);
      const from=messageAnchor(helper).getBoundingClientRect(), to=projectTab(snapshot.topics.find(t=>t.id===b).project).getBoundingClientRect();
      return Math.abs(flight.center[0]-from.x-from.width/2)<2 && Math.abs(flight.center[1]-from.y-from.height/2)<2 &&
        Math.abs(coords.at(-2)-to.x-to.width/2)<2 && Math.abs(coords.at(-1)-to.y-to.height/2)<2;
    },{helper,b}));
    const recipientTab = tab("two").locator("xpath=ancestor::button");
    await recipientTab.locator(".tag-inbox").filter({hasText:"1"}).waitFor();
    assert.equal(await recipientTab.getAttribute("data-activity"), "waiting");
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
    assert.deepEqual(await pendingBar.locator("button").allTextContents(), ["下一条","预览","忽略","写入"]);
    const entered = logLines("two","enters.txt").length;
    await pendingBar.getByRole("button", {name:"忽略",exact:true}).click();
    await pendingBar.waitFor({state:"hidden"});
    await page.locator('.timeline-entry[data-message="'+ignoredPending+'"] .log-state').filter({hasText:"已忽略"}).waitFor();
    assert.equal(logLines("two","enters.txt").length, entered);
    assert.equal((await page.request.post(base+"/api/windows/"+right+"/input", {data:{messageId:ignoredPending}})).status(),409);
    await page.waitForFunction(()=>window.effects.some(e=>e.frames[0]?.boxShadow));
    await page.locator('.timeline-entry[data-message="'+cross+'"]').click();
    await page.locator("#preview-content").filter({hasText:"检查跨话题收件"}).waitFor();
    await page.keyboard.press("Escape");
    await page.locator("#topic-state").click();
    await page.getByRole("button",{name:"重新打开",exact:true}).waitFor();
    assert(await page.locator("#new-agent").isDisabled());
    await page.locator("#topic-state").click();
    await choose(a);
    await page.emulateMedia({reducedMotion:"reduce"});
    await page.waitForFunction(() => !document.querySelector(".message-flight"));
    await page.evaluate(()=>{window.effects=[];});
    const reduced = send("@设计/同名","减少动态效果");
    await page.waitForFunction(id=>snapshot.windows.some(w=>w.inbox.some(m=>m.id===id)),reduced);
    // 收件角标先出现，目标项目卡会在信封落地时才高亮。
    await page.waitForFunction(() => window.effects.some(e => e.classes === "message-flight") &&
      window.effects.some(e => e.classes.includes("project-button") && e.frames[0]?.backgroundColor));
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
    await pane(helper).locator(".toggle-collapse").click();
    await page.setViewportSize({width:760,height:920});
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
    await page.waitForFunction(id=>selectedTopicId===id && !!layout,b);
    await openProject("one");
    await page.locator("#mobile-topic").selectOption(a);
    await page.setViewportSize({width:1460,height:940});
    assert.equal(await page.evaluate(()=>layout.primaryWindowId),left);
    assert(await pane(left).evaluate(el => el.parentElement.id === "primary-panes" && !layout.collapsedIds.includes(layout.primaryWindowId)));
    assert.equal(await pane(helper).getAttribute("data-collapsed"), "true");
    await pane(helper).locator(".toggle-collapse").click();
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
    assert.deepEqual(logLines("one","enters.txt"),["retry","retry"]);
    assert.equal(logLines("one","accepted.txt").length,1);
    assert(logLines("one","accepted.txt")[0].includes("交接路径 @index.html"));
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

    for (const [behavior, expected] of [["submit",1],["silent",2],["delayed",1],["foreign",1],["afterkey",1],["newtext",1],["running",1],["approval",1],["hold",2],["typing",1]]) {
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
      const submitted = ["submit","silent","delayed","foreign","afterkey","newtext","running"].includes(behavior);
      await until(()=>state(id)===(submitted?"delivered":"delivery_uncertain"),behavior+" result");
      assert.equal(logLines("one","enters.txt").length-before,expected,behavior);
      if (!submitted || ["foreign","newtext","running"].includes(behavior)) await key(left,"Control+u");
    }
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
    await pane(left).getByRole("button", {name:"忽略",exact:true}).click();
    await pane(left).locator(".pane-error").waitFor({state:"hidden"});
    await page.waitForFunction(({left, ignored}) => !panes.get(left).view.inbox.some(m => m.id === ignored), {left,ignored});
    assert.equal(sql("SELECT count(*) FROM inbox WHERE state='delivery_uncertain' AND acknowledged=1")[0][0],1);
    await page.locator('.timeline-entry[data-message="'+ignored+'"] .log-state').filter({hasText:"已忽略"}).waitFor();
    assert.equal(logLines("one","enters.txt").length, entersBeforeIgnore);
    const finished=mcp("finish_topic",{summary:"验收完成"});
    assert.equal(finished.id,a);
    assert.equal(finished.summary,"验收完成");
    await page.waitForFunction(()=>selectedTopic().activity==="finished");
    assert(await page.evaluate(()=>window.effects.every(e=>e.duration<=400 || (e.classes==="message-flight" && e.duration===1150))));
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
      return el.firstElementChild?.dataset.message === id && el.scrollTop <= 1;
    }, newest);
    await row.click();
    await page.locator("#preview-content").filter({hasText:"接口文档已更新"}).waitFor();
    await page.keyboard.press("Escape");
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
    await page.waitForFunction(() => document.querySelectorAll('#topic-list [aria-pressed="true"] .topic-agent-tag').length >= 10);
    const overflowId = await page.locator('#topic-list [aria-pressed="true"] .topic-agent-tag').last().getAttribute("data-agent");
    await page.locator('#topic-list [aria-pressed="true"] .topic-agent-tag[data-agent="' + overflowId + '"]').click();
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
    await more.click();
    await menu.locator(".rename-agent").click();
    await page.locator("#rename-dialog[open]").waitFor();
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
    await more.waitFor({state:"hidden"});
    assert.equal(await menu.locator("button,label").count(), 0);
    assert(await footer.locator(".close-pane").isVisible());
    await page.setViewportSize({width:1000,height:760});
    let lastId;
    for (let i = 0; i < 12; i++)
      lastId = (await post("/topics", {name:"导航回归 " + i + " 长话题名称", project:path.join(root,"one")})).id;
    const strip = page.locator("#topic-list");
    await strip.locator('[data-topic="'+lastId+'"]').waitFor({state:"attached"});
    assert(await topicWidth(lastId) > await topicWidth(another), "成员数相同时，长标题应比短标题宽");
    assert(await strip.evaluate(el => {
      const first = el.firstElementChild.getBoundingClientRect(), header = el.closest("header").getBoundingClientRect();
      return el.scrollWidth > el.clientWidth && first.top >= header.top && first.bottom <= header.bottom &&
        Array.from(el.children).every(tab => tab.getBoundingClientRect().top === first.top && tab.offsetWidth <= el.clientWidth);
    }));
    await page.locator("#mobile-topic").selectOption(lastId);
    await page.waitForFunction(id => {
      const el = document.querySelector('#topic-list [aria-pressed="true"]');
      if (el?.dataset.topic !== id) return false;
      const tab = el.getBoundingClientRect(), list = el.parentElement.getBoundingClientRect();
      return tab.left >= list.left - 1 && tab.right <= list.right + 1;
    }, lastId);
    console.log("PASS desktop five-row log, send while scrolled back, latest preview, resize, collapse and mobile Agent overflow");
    await page.setViewportSize({width:1180,height:760});
    if (!await page.locator("#project-rail").isVisible()) await page.locator("#project-collapse").click();
    const card = page.locator("#current-project");
    assert(await card.evaluate(el => {
      const group = snapshot.topics.filter(t => projectKey(t.project) === el.dataset.key);
      const members = snapshot.windows.filter(w => group.some(t => t.id === w.agent.topicId));
      const states = [...el.querySelectorAll(".project-states > span")];
      const widths = states.map(cell => cell.getBoundingClientRect().width);
      return el.querySelector(".project-summary").textContent === "会话 " + group.length + " · Agent " + members.length &&
        states.every((cell,i) => Number(cell.querySelector("b").textContent) === members.filter(w =>
          i===0 ? ["working","waiting"].includes(w.activity) : w.activity === (i===1?"idle":"offline")).length) &&
        Math.max(...widths)-Math.min(...widths)<1 && new Set(states.map(c=>getComputedStyle(c,"::before").backgroundColor)).size===3 &&
        states.every(c=>getComputedStyle(c,"::before").animationName==="none") &&
        el.querySelector(".project-summary").offsetTop > el.querySelector(".project-card-title").offsetTop &&
        el.querySelector(".project-states").offsetTop > el.querySelector(".project-summary").offsetTop;
    }));
    const claudeTopic = await post("/topics",{name:"Claude 实测",project:path.join(root,"third")});
    const claude = await post("/windows",{name:"Claude审阅",engine:"claude",topic:claudeTopic.id,newSession:true});
    await choose(claudeTopic.id);
    await ready(claude.id);
    const receipt = send("@Claude 实测/Claude审阅","验证 Claude 收件");
    await page.waitForFunction(id=>panes.get(id)?.view.inputReady,claude.id);
    await page.waitForFunction(id=>snapshot.timeline.some(m=>m.id===id && m.state==="delivered"),receipt);
    assert(fs.readFileSync(path.join(root,"third","accepted.txt"),"utf8").includes(receipt.slice(0,8)));
    console.log("PASS settings, three-row project cards with static working/idle/offline lights, Claude ConPTY delivery");
    assert.deepEqual(errors,[]);
    console.log("PASS pending-to-idle without new VT, multiline retry once, first-submit success, approval, typing, retry cap and stale error clearing");
  } finally { await browser.close(); }
}
run().catch(error=>{console.error(error);process.exitCode=1;});
