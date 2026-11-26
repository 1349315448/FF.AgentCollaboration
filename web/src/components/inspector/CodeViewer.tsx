import { lazy, Suspense, useCallback, useEffect, useState } from "react";
import { request } from "../../services/api";
import { copyText } from "../../services/clipboard";
import { layout, selectedTopicId, snapshot } from "../../store/workbenchStore";
import { codeViewport } from "../../runtime/workspaceModes.js";
import { WorkspaceIcon } from "../layout/WorkspaceIcon";
import { FileTreeView } from "./FileTreeView";
import { useGitWorkspace, type GitChange } from "./gitWorkspace";
import { dirty, fileUrl, useEditor, type FileTab } from "./workspaceFiles";

const RichPreview = lazy(()=>import("./RichPreview"));
const CodeText = lazy(()=>import("./CodeText"));
function currentAgent() {
  const members=snapshot?.windows.filter(w=>w.agent.topicId===selectedTopicId)??[];
  if (!layout) return;
  const ids=members.map(w=>w.agent.id);
  const id=layout.workspaceMode==="code"?codeViewport(ids,layout).active:
    [layout.activeWindowId,layout.primaryWindowId,...ids].find(x=>x && ids.includes(x));
  return members.find(w=>w.agent.id===id)?.agent;
}
function normalizeEdit(text:string, previous:string) {
  if (!previous.includes("\r")) return text;
  const normalized=previous.replace(/\r\n?/g,"\n");
  let start=0, end=normalized.length, nextEnd=text.length;
  while (start<end && start<nextEnd && normalized[start]===text[start]) start++;
  while (end>start && nextEnd>start && normalized[end-1]===text[nextEnd-1]) {end--;nextEnd--;}
  // 浏览器统一把换行呈现为 LF。只对本次新输入的片段应用附近的换行符。
  let raw=0;
  const advance=(from:number,to:number)=>{
    for(let i=from;i<to;i++) { if(previous[raw]==="\r" && previous[raw+1]==="\n") raw++; raw++; }
  };
  advance(0,start);
  const prefix=previous.slice(0,raw);
  advance(start,end);
  const ending=previous.slice(raw).match(/\r\n|\r|\n/)?.[0]??previous.match(/\r\n|\r|\n/)?.[0]??"\n";
  return prefix+text.slice(start,nextEnd).replaceAll("\n",ending)+previous.slice(raw);
}
export default function CodeViewer({project,openDiff}:{project:string;openDiff:(c:GitChange)=>void}) {
  const git=useGitWorkspace();
  const {tabs,active,session}=useEditor(project);
  const tab=tabs.find(t=>t.path===active), file=tab?.file;
  const [treeOpen,showTree]=useState(true), [notice,setNotice]=useState("");
  const [,setLayoutTick]=useState(0);
  useEffect(()=>{
    const update=()=>setLayoutTick(n=>n+1);
    const onFocus=(event:FocusEvent)=>{if(event.target instanceof Element && event.target.closest(".terminal-surface")) update();};
    window.addEventListener("ff:layout-changed",update);
    document.addEventListener("focusin",onFocus);
    return ()=>{window.removeEventListener("ff:layout-changed",update);document.removeEventListener("focusin",onFocus);};
  },[]);
  const agent=currentAgent();
  const open=useCallback(async(path:string)=>{
    try { setNotice(""); await session.open(path); }
    catch(e){setNotice((e as Error).message);}
  },[session]);
  async function sendPath() {
    const target=currentAgent();
    if (!target?.online || !file) return;
    try {
      // Agent 可以有自己的工作目录；跨目录时使用完整路径，插入后保留原生输入行。
      const path=target.project===project?file.path:project.replaceAll("\\","/").replace(/\/+$/,"")+"/"+file.path;
      const text=(/\s/.test(path)?'"'+path+'"':path)+" ";
      await request("/windows/"+target.id+"/input",{method:"POST",body:{text}});
      setNotice("已插入 @"+target.name+" 的输入行");
    } catch(e){setNotice((e as Error).message);}
  }
  function reload(item:FileTab) {
    if (!dirty(item) || window.confirm("重新读取会放弃当前草稿，继续？")) void session.load(item.path);
  }
  return <div className="files-workbench" data-tree={treeOpen} onKeyDown={e=>{
    if (e.key.toLowerCase()==="s" && (e.ctrlKey||e.metaKey)) {e.preventDefault();if(tab && dirty(tab)) void session.save(tab).then(git.refresh);}
  }}>
    {treeOpen && <FileTreeView {...{project,open,active,openDiff}}/>}
    <div className="code-workspace"><div className="file-tabbar">
      <button className="inspector-icon" aria-label={treeOpen?"收起文件树":"展开文件树"} aria-expanded={treeOpen} title={treeOpen?"收起文件树":"展开文件树"} onClick={()=>showTree(v=>!v)}><WorkspaceIcon name="rail"/></button>
      <div role="tablist" aria-label="已打开文件">{tabs.map(t=><div className="file-tab" key={t.path} data-active={active===t.path}
        onMouseDown={e=>{if(e.button===1)e.preventDefault();}} onAuxClick={e=>{if(e.button===1){e.preventDefault();session.close(t.path);}}}>
        <button type="button" role="tab" aria-selected={active===t.path} title={t.path} onClick={()=>{session.select(t.path);setNotice("");}}>{t.path.split("/").pop()}{dirty(t) && <i className="dirty-dot" aria-label="有未保存的修改"/>}</button>
        <button type="button" aria-label={"关闭 "+t.path} disabled={t.saving} onClick={()=>session.close(t.path)}><WorkspaceIcon name="close"/></button>
      </div>)}</div>
    </div>
    {!tab?<p className="inspector-empty">选择项目文件以查看内容</p>:<>
      <div className="file-caption" title={active}>{active}</div>
      {tab.error && <div role="alert" className="file-error">{tab.error} <button onClick={()=>reload(tab)}>重新读取</button></div>}
      {!file?(!tab.error && <p className="inspector-empty">正在读取…</p>):file.kind==="text"?
        tab.draft!==undefined?<textarea aria-label="编辑文件内容" className="source-editor" spellCheck={false} value={tab.draft} disabled={tab.saving||tab.loading}
          onChange={e=>session.update(active,{draft:normalizeEdit(e.target.value,tab.draft??"")})}/>:
        <Suspense fallback={<p className="inspector-empty">正在显示代码…</p>}><CodeText path={active} content={file.content??""}/></Suspense>
        :<Suspense fallback={<p className="inspector-empty">正在加载预览…</p>}><RichPreview key={active+file.mtimeUtc} file={file} url={"/api"+fileUrl(project,active)+"&raw=true&v="+encodeURIComponent(file.mtimeUtc)}/></Suspense>}
      {file && <div className="file-actions">
        <button title="复制相对路径" onClick={async()=>{try {await copyText(active);setNotice("已复制相对路径");} catch {setNotice("浏览器未允许复制路径");}}}><WorkspaceIcon name="copy"/>复制路径</button>
        <button disabled={!agent?.online} title={agent?"插入 @"+agent.name+" 的输入行":"当前话题没有 Agent"} onClick={sendPath}><WorkspaceIcon name="send"/>发给当前 Agent</button>
        {file.kind==="text" && (tab.draft===undefined?<button onClick={()=>session.update(active,{draft:file.content??""})}><WorkspaceIcon name="edit"/>编辑</button>:<>
          <button disabled={tab.saving} onClick={()=>{if (!dirty(tab) || window.confirm("放弃未保存的修改？")) {if(tab.conflict) void session.load(active);else session.update(active,{draft:undefined});}}}>取消</button>
          <button disabled={!dirty(tab)||tab.saving||tab.loading||tab.conflict} onClick={()=>void session.save(tab).then(git.refresh)}>{tab.saving?"保存中…":"保存"}</button>
        </>)}
      </div>}
    </>}
    {notice && <div role="status" className="file-notice">{notice}</div>}
    </div>
  </div>;
}
