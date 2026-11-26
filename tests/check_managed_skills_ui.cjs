const assert=require("node:assert/strict");
const fs=require("node:fs");
const path=require("node:path");
const {chromium}=require("playwright");

const [base,,root]=process.argv.slice(2);
const project=path.join(root,"one");
const markdown="---\nname: drawer-test\ndescription: Inspect the drawer\n---\n\n# Skill\n";
(async()=>{
  const browser=await chromium.launch({executablePath:process.env.CHROME_PATH});
  try {
    const page=await browser.newPage({viewport:{width:1340,height:880}});
    const errors=[];page.on("pageerror",err=>errors.push(err.message));
    const sample=path.join(root,"sample");
    fs.mkdirSync(path.join(sample,"references"),{recursive:true});
    fs.writeFileSync(path.join(sample,"SKILL.md"),markdown);
    fs.writeFileSync(path.join(sample,"references","check.txt"),"example");
    await page.goto(base);
    await page.locator('#workspace-mode-switch [data-mode="code"]').click();
    await page.getByRole("button",{name:"Skills 管理",exact:true}).click();
    const ui=page.getByRole("region",{name:"Skills 管理"});
    assert.equal(await ui.getByRole("button",{name:"CLI 原生",exact:true}).evaluate(el=>el.offsetHeight),36);
    assert.equal(await ui.getByRole("button",{name:"Qoder CLI",exact:true}).evaluate(el=>el.offsetHeight),32);
    assert.equal(await ui.getByLabel("查看项目").count(),0);
    await ui.getByRole("button",{name:"ff-agent-collab",exact:true}).click();
    await ui.getByRole("article",{name:"Skill 正文"}).waitFor();
    assert((await ui.getByRole("article",{name:"Skill 正文"}).innerText()).includes("send_to_agent"));
    await ui.getByRole("button",{name:"返回列表"}).click();
    await ui.getByLabel("选择技能目录").setInputFiles(sample);
    await ui.getByLabel("作用范围").selectOption("project");
    await ui.getByLabel("目标项目").selectOption(project);
    await ui.getByLabel("Qoder CLI",{exact:true}).check();
    await ui.getByLabel("SKILL.md",{exact:true}).fill(markdown+"edited in browser\n");
    await ui.getByRole("button",{name:"保存 Skill"}).click();
    await ui.getByText("已保存并同步",{exact:true}).waitFor();
    const dest=path.join(project,".qoder","skills","drawer-test");
    assert(fs.readFileSync(path.join(dest,"SKILL.md"),"utf8").includes("edited in browser"));
    assert(fs.readFileSync(path.join(dest,"references","check.txt"),"utf8")==="example");
    await ui.getByLabel("启用",{exact:true}).uncheck();
    const save=page.waitForResponse(r=>r.request().method()==="PUT"&&r.url().includes("/managed-skills/"));
    await ui.getByRole("button",{name:"保存 Skill"}).click();
    assert((await save).ok());
    assert(!fs.existsSync(dest));
    fs.mkdirSync(dest,{recursive:true});fs.writeFileSync(path.join(dest,"SKILL.md"),markdown);
    await ui.getByRole("button",{name:"Qoder CLI",exact:true}).click();
    await ui.getByRole("button",{name:"刷新",exact:true}).click();
    await ui.getByRole("button",{name:"drawer-test",exact:true}).click();
    await ui.getByRole("button",{name:"删除原生 Skill",exact:true}).click();
    const remove=ui.getByRole("button",{name:"确认删除原生 Skill",exact:true});
    assert(await remove.isDisabled());
    await ui.getByLabel("确认原生技能名称").fill("incorrect");
    assert(await remove.isDisabled());
    await ui.getByLabel("确认原生技能名称").fill("drawer-test");
    await remove.click();
    await ui.getByText("已删除原生 Skill",{exact:true}).waitFor();
    assert(!fs.existsSync(dest));
    await ui.getByRole("button",{name:"平台配置",exact:true}).click();
    await ui.getByRole("button",{name:"drawer-test",exact:true}).click();
    await ui.locator("textarea").waitFor();
    assert((await ui.getByLabel("SKILL.md",{exact:true}).inputValue()).includes("edited in browser"));
    await page.setViewportSize({width:390,height:800});
    assert(await ui.evaluate(el=>el.scrollWidth<=el.clientWidth+2));
    console.log("PASS Chromium directory import, edit, scope, disable, native exact-name confirmation, mobile drawer");

    async function post(route,data) {
      const reply=await page.request.post(base+"/api/"+route,{data});
      assert(reply.ok(),route+" returned "+reply.status());
      return reply.status()===204?null:reply.json();
    }
    // Validate the two launch-only roots using real ConPTY processes with native protocol fixtures.
    const topic=await post("topics",{name:"Skills",project});
    for(const engine of ["claude","zcode"]) {
      const claude=engine==="claude";
      await post("managed-skills",{content:markdown.replace("drawer-test",engine+"-fixture"),scope:claude?"project":"global",projectPath:project,engines:[engine],enabled:true});
      const agent=await post("windows",{topic:topic.id,project,engine,name:engine,newSession:true});
      const file=path.join(project,"terminal-launch.txt");
      await page.waitForTimeout(200);
      if(claude) {
        const args=fs.readFileSync(file,"utf8").trim().split(/\r?\n/);
        const index=args.indexOf("--add-dir");assert(index>=0);
        assert(fs.existsSync(path.join(args[index+1],".claude","skills","claude-fixture",".ff-managed")));
        assert(!fs.existsSync(path.join(project,".claude","skills","claude-fixture")));
      } else {
        const cfg=JSON.parse(fs.readFileSync(path.join(root,"state","engine-profiles",agent.id,".zcode","cli","config.json"),"utf8"));
        assert(fs.existsSync(path.join(cfg.skills.roots[0],"zcode-fixture","SKILL.md")));
      }
      await post("windows/"+agent.id+"/stop",{});
    }
    assert.deepEqual(errors,[]);
    console.log("PASS Claude additional project root and ZCode isolated user root in native launch adapters");
  } finally {await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1;});
