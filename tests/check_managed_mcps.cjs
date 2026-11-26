const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const http = require("node:http");
const {spawnSync} = require("node:child_process");
const {chromium} = require("playwright");

const [base,,root] = process.argv.slice(2);
const project=path.join(root,"one");
const route="/managed-mcps";
const engines=["codex","copilot","claude","codebuddy","qwen","zcode","traecli","qoder"];
function write(file,value) {
  fs.mkdirSync(path.dirname(file),{recursive:true});
  fs.writeFileSync(file,value);
}
async function readLaunch(file) {
  for(let i=0;i<100;i++) {
    if(fs.existsSync(file) && fs.statSync(file).size) return fs.readFileSync(file,"utf8").trim().split(/\r?\n/);
    await new Promise(resolve=>setTimeout(resolve,75));
  }
  throw new Error("终端夹具没有收到启动参数");
}

(async()=>{
  const browser=await chromium.launch({executablePath:process.env.CHROME_PATH});
  const probe=http.createServer();
  await new Promise(resolve=>probe.listen(0,"127.0.0.1",resolve));
  try {
    const page=await browser.newPage({viewport:{width:1440,height:980}});
    const errors=[];
    page.on("pageerror",e=>errors.push(e.message));
    async function api(method,endpoint,data,status=200) {
      const res=await page.request.fetch(base+"/api"+endpoint,{method,data});
      assert.equal(res.status(),status,method+" "+endpoint+" returned "+res.status());
      const body=await res.text();
      if(endpoint.startsWith(route)) assert(!body.includes("private-fixture-secret"),"a management response contained a fixture credential");
      return body?JSON.parse(body):null;
    }
    const nativePath=path.join(root,"cli-user",".codex","config.toml");
    const nativeContent='[mcp_servers."native-one"]\ncommand = "native-fixture"\n';
    write(nativePath,nativeContent);
    write(path.join(project,".zcode","config.json"),'{"mcp":{"servers":{"from-project":{"type":"stdio","command":"fixture"}}}}');
    write(path.join(root,"cli-user",".zcode","cli","config.json"),'{"mcp":{"servers":{"from-user":{"type":"stdio","command":"fixture"}}}}');
    write(path.join(root,"claude",".config.json"),'{"mcpServers":{"claude-active":{"command":"fixture"}}}');
    write(path.join(root,"claude",".claude.json"),'{"mcpServers":{"claude-shadowed":{"command":"fixture"}}}');
    const catalog=()=>api("GET",route+"?"+new URLSearchParams({project}));
    const initial=await catalog();
    assert(initial.native.some(n=>n.name==="native-one" && n.engine==="codex"));
    assert(initial.native.some(n=>n.name==="from-project" && n.activeInWindow));
    assert(initial.native.some(n=>n.name==="from-user" && !n.activeInWindow));
    assert(initial.native.some(n=>n.name==="claude-active") && !initial.native.some(n=>n.name==="claude-shadowed"));
    const zcodeConfig=path.join(project,".zcode","config.json");
    const zcodeFallback=path.join(project,".agents","mcp.json");
    write(zcodeFallback,'{"mcpServers":{"fallback":{"command":"fixture"}}}');
    assert(!(await catalog()).native.some(n=>n.name==="fallback"));
    write(zcodeConfig,'{"mcpServers":{"wrong-wrapper":{"command":"fixture"}},"mcp":{"servers":{"invalid":{}}}}');
    const fallbackCatalog=await catalog();
    assert(fallbackCatalog.native.some(n=>n.name==="fallback" && n.activeInWindow));
    assert(!fallbackCatalog.native.some(n=>n.name==="wrong-wrapper"));
    write(zcodeConfig,'{"mcp":{"servers":{"uncertain":{"command":"fixture","args":"wrong-type"}}}}');
    assert((await catalog()).native.some(n=>n.name==="fallback" && n.issue));
    for(const engine of engines) assert.equal(initial.compatibility[engine].includes("sse"),!["codex","traecli"].includes(engine));
    const config={command:process.execPath,args:["--version"],env:{TOKEN:"private-fixture-secret"}};
    for(const name of ["LX_COLLAB","Workbench-MCP"]) await api("POST",route,{name,...config},400);
    await api("POST",route,{name:"bad-grant",command:"test",env:{TOKEN:"$"+"{FF_P0_WINDOW_GRANT}"}},400);
    await api("POST",route,{name:"bad-project",scope:"project",projectPath:path.join(root,"two"),...config},403);
    const addr="http://127.0.0.1:"+probe.address().port+"/mcp";
    await api("POST",route+"/import",{mcpServers:{"rollback-entry":config,lx_collab:config}},400);
    assert(!(await catalog()).managed.length);
    const [stdio,remote]=await api("POST",route+"/import",{mcpServers:{
      "fixture-stdio":config,"fixture-http":{type:"http",url:addr,headers:{Authorization:"private-fixture-secret"}},
      "fixture-sse":{type:"sse",url:addr},"native-one":config
    }});
    assert.equal(stdio.env.TOKEN,null);
    assert.equal(remote.headers.Authorization,null);
    assert((await api("POST",route+"/"+remote.id+"/preflight")).ok);
    assert((await api("POST",route+"/"+stdio.id+"/preflight")).ok);
    const stdioUpdate=await api("PUT",route+"/"+stdio.id,{...stdio,args:["--help"]});
    await api("PUT",route+"/"+stdio.id,stdio,409);
    await api("POST",route,{name:"FIXTURE-STDIO",...config},409);
    console.log("PASS native inventories, reserved grants/names, masked round trip, atomic imports, revision conflict, TCP and executable preflight");

    await page.goto(base);
    const launch=page.getByRole("button",{name:"MCP 管理",exact:true});
    await launch.waitFor();
    for (const mode of ["code","agent"]) {
      await page.locator('#workspace-mode-switch [data-mode="'+mode+'"]').click();
      assert(await launch.isVisible() && await page.getByRole("button",{name:"Skills 管理",exact:true}).isVisible());
      assert(await launch.evaluate(el=>el.getBoundingClientRect().top>=document.getElementById("workspace-mode-switch").getBoundingClientRect().bottom-1));
    }
    await page.getByRole("button",{name:"MCP 管理",exact:true}).click();
    const manager=page.getByRole("region",{name:"MCP 管理"});
    assert((await page.locator("#resource-dialog").boundingBox()).width>=900);
    assert.equal(await manager.getByLabel("查看项目").count(),0);
    await manager.getByRole("button",{name:"lx_collab",exact:true}).click();
    await manager.getByText("stdio",{exact:true}).waitFor();
    await manager.getByRole("button",{name:"返回列表"}).click();
    await manager.getByRole("button",{name:"fixture-stdio",exact:true}).click();
    assert.equal(await manager.getByLabel("环境变量值").inputValue(),"");
    assert.match(await manager.getByLabel("环境变量值").getAttribute("placeholder"),/保留/);
    await manager.getByRole("button",{name:"Codex",exact:true}).click();
    await manager.getByRole("button",{name:"native-one",exact:true}).click();
    await manager.getByText("同名冲突",{exact:true}).first().waitFor();
    await manager.getByRole("button",{name:"平台配置"}).click();
    await manager.getByRole("button",{name:"新增 MCP"}).click();
    await manager.getByLabel("名称",{exact:true}).fill("ui-entry");
    await manager.getByLabel("启动命令").fill(process.execPath);
    await manager.getByRole("button",{name:"保存 MCP"}).click();
    await manager.getByRole("button",{name:"预检已保存配置"}).click();
    await manager.getByText("已找到可执行文件",{exact:true}).waitFor();
    await manager.getByLabel("启用",{exact:true}).uncheck();
    await manager.getByRole("button",{name:"保存 MCP"}).click();
    await manager.getByText("已保存",{exact:true}).waitFor();
    assert.equal((await catalog()).managed.find(m=>m.name==="ui-entry").enabled,false);
    page.once("dialog",d=>d.accept());
    await manager.getByRole("button",{name:"删除",exact:true}).click();
    await manager.getByText("已删除",{exact:true}).waitFor();
    await manager.getByRole("button",{name:"导入 JSON"}).click();
    await manager.getByLabel("mcpServers JSON").fill(JSON.stringify({mcpServers:{"ui-only":{command:"missing-fixture-executable"}}}));
    await manager.getByRole("button",{name:"确认导入"}).click();
    await manager.getByText("已导入",{exact:true}).waitFor();
    const uiOnly=(await catalog()).managed.find(m=>m.name==="ui-only");
    assert(!(await api("POST",route+"/"+uiOnly.id+"/preflight")).ok);
    await api("DELETE",route+"/"+uiOnly.id+"?"+new URLSearchParams({revision:uiOnly.updatedAt}),undefined,204);
    await page.getByRole("button",{name:"关闭管理面板"}).click();
    await api("DELETE","/project?"+new URLSearchParams({path:project}),undefined,204);
    await page.waitForFunction(()=>document.querySelectorAll("#project-tabs [data-project]").length===0);
    await launch.click();
    await manager.getByRole("button",{name:"fixture-stdio",exact:true}).waitFor();
    await manager.getByRole("button",{name:"Codex",exact:true}).click();
    await manager.getByRole("button",{name:"native-one",exact:true}).waitFor();
    assert.equal(await manager.getByLabel("查看项目").count(),0);
    await page.keyboard.press("Escape");
    await api("PUT","/project",{path:project});
    console.log("PASS Chromium create, edit, masked values, conflict badge, enable/disable, import, preflight and delete");

    for(const engine of engines) {
      const dir=path.join(root,engine);
      fs.mkdirSync(dir,{recursive:true});
      write(path.join(dir,"native-prompt.fixture"),engine);
      const topic=await api("POST","/topics",{name:engine,project:dir});
      await api("POST",route,{name:"only-"+engine,scope:"project",projectPath:dir,engines:[engine],...config});
      const window=await api("POST","/windows",{topic:topic.id,project:dir,name:engine,engine,newSession:true});
      const file=path.join(dir,"terminal-launch.txt");
      const args=await readLaunch(file);
      const directory=path.join(root,"state","engine-profiles",window.id);
      let text=args.join("\n"), definitions;
      if(engine==="qoder") definitions=JSON.parse(fs.readFileSync(path.join(directory,"qoder-mcp.json"),"utf8")).mcpServers;
      else if(engine==="zcode") definitions=JSON.parse(fs.readFileSync(path.join(directory,".zcode","cli","config.json"),"utf8")).mcp.servers;
      else if(!["codex","traecli"].includes(engine)) {
        const flag=engine==="copilot"?"--additional-mcp-config":"--mcp-config";
        definitions=JSON.parse(args[args.indexOf(flag)+1]).mcpServers;
      }
      if(definitions) {
        assert(definitions.lx_collab && definitions["fixture-stdio"]);
        assert.equal(definitions["fixture-stdio"].env.TOKEN,"private-fixture-secret");
        assert.equal(definitions["fixture-stdio"].env.LX_P0_WINDOW_GRANT,"");
        assert.equal(definitions["fixture-http"].headers.Authorization,"private-fixture-secret");
        if(engine==="qwen") assert.equal(definitions["fixture-http"].httpUrl,addr);
        assert(definitions["fixture-sse"]);
        assert(!args.includes("--allow-tool=fixture-stdio"));
        text=JSON.stringify(definitions);
      } else {
        assert(text.includes("mcp_servers.lx_collab"));
        assert(text.includes("mcp_servers.fixture-http") && text.includes("http_headers") && text.includes("private-fixture-secret"));
        assert(!text.includes("mcp_servers.fixture-sse"));
      }
      assert(text.includes("only-"+engine));
      assert(engines.filter(e=>e!==engine).every(e=>!text.includes("only-"+e)));
      if(engine==="codex") {
        assert(!text.includes("mcp_servers.native-one"));
        if(process.env.FF_TEST_NATIVE_CODEX) {
          const nativeHome=path.join(root,"native-codex-smoke");
          fs.mkdirSync(nativeHome);
          const override=args.find(a=>a.startsWith("mcp_servers.fixture-stdio="));
          const result=spawnSync(process.env.FF_TEST_NATIVE_CODEX,["-c",override,"mcp","get","fixture-stdio","--json"],
            {cwd:dir,env:{...process.env,CODEX_HOME:nativeHome},encoding:"utf8",timeout:12000,windowsHide:true});
          assert.equal(result.status,0,"installed Codex could not parse the generated override");
          const native=JSON.parse(result.stdout);
          assert.equal(native.name,"fixture-stdio");
          assert.equal(native.transport.command,process.execPath);
          console.log("PASS installed Codex mcp get parsed the generated stdio definition offline");
        }
        const disclosure=page.locator(".project-select").filter({has:page.getByTitle(dir,{exact:true})});
        if(await disclosure.getAttribute("aria-expanded")==="false") await disclosure.click();
        await page.locator('.topic-choice[data-topic="'+topic.id+'"] .topic-label').click();
        const state=async()=>(await api("GET","/state")).windows.find(w=>w.agent.id===window.id);
        assert(!(await state()).mcpRestartRequired);
        const duplicate=(await catalog()).managed.find(m=>m.name==="native-one");
        await api("PUT",route+"/"+duplicate.id,{...duplicate,args:[]});
        assert(!(await state()).mcpRestartRequired);
        await api("PUT",route+"/"+stdioUpdate.id,{...stdioUpdate,args:["--test-revision"]});
        assert((await state()).mcpRestartRequired);
        const hint=page.locator(".mcp-restart:visible");
        await hint.waitFor();
        const generation=(await state()).terminal;
        const huge=await api("POST",route,{name:"oversize",engines:["codex"],transport:"http",url:addr,
          headers:{First:"a".repeat(16384),Second:"b".repeat(16384)}});
        await api("PUT","/windows/"+window.id+"/approval",{terminal:generation,approval:"ask"},400);
        assert.equal((await state()).terminal,generation);
        await api("DELETE",route+"/"+huge.id+"?"+new URLSearchParams({revision:huge.updatedAt}),undefined,204);
        await api("POST","/windows/"+window.id+"/stop",undefined,204);
        fs.unlinkSync(file);
        await api("POST","/windows/"+window.id+"/start",undefined,204);
        assert((await readLaunch(file)).join("\n").includes("--test-revision"));
        assert(!(await state()).mcpRestartRequired);
        await hint.waitFor({state:"hidden"});
      }
      await api("POST","/windows/"+window.id+"/stop",undefined,204);
      if(engine==="qwen") {
        write(path.join(dir,"qwen-legacy.fixture"),"");
        await api("POST","/windows/"+window.id+"/start",undefined,204);
        const legacy=JSON.parse(fs.readFileSync(path.join(directory,"qwen-mcp.json"),"utf8")).mcpServers;
        assert(legacy.lx_collab && legacy["fixture-http"].httpUrl===addr && legacy["fixture-sse"].url===addr);
        await api("POST","/windows/"+window.id+"/stop",undefined,204);
      }
      console.log("PASS ConPTY fixture launch "+engine+" scope, transport and bridge isolation");
    }
    assert.equal(fs.readFileSync(nativePath,"utf8"),nativeContent);
    assert.deepEqual(errors,[]);
  } finally {
    await browser.close();
    await new Promise(resolve=>probe.close(resolve));
  }
})().catch(e=>{console.error(e.message);process.exitCode=1;});
