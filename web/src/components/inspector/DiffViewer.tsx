import { useEffect, useMemo, useState } from "react";
import { request } from "../../services/api";
import { fileUrl, type FileView } from "./workspaceFiles";
import type { GitDiff } from "./gitWorkspace";

function parse(text:string) {
  let before=0, after=0, hunk=false;
  return text.split("\n").map(line=>{
    const header=line.match(/^@@ -(\d+)(?:,\d+)? \+(\d+)(?:,\d+)? @@/);
    if(header) { before=Number(header[1]);after=Number(header[2]);hunk=true; }
    else if(line.startsWith("diff --git")) hunk=false;
    if(!hunk || header || !/^[ +\-]/.test(line)) return {kind:"meta",code:line};
    const mark=line[0];
    return {kind:mark==="+"?"added":mark==="-"?"deleted":"context",old:mark==="+"?undefined:before++,
      next:mark==="-"?undefined:after++,code:line};
  });
}

export function DiffViewer({project,path,diff,error,version}:{project:string;path:string;diff?:GitDiff;error?:string;version:number}) {
  const [original,toggle]=useState(false),[file,setFile]=useState<FileView>(),[readError,setReadError]=useState("");
  useEffect(()=>{
    if(!original) return;
    const controller=new AbortController();
    setFile(undefined);setReadError("");
    request<FileView>(fileUrl(project,path),{signal:controller.signal}).then(value=>{
      if(!controller.signal.aborted)setFile(value);
    }).catch(e=>{if(!controller.signal.aborted)setReadError(e.message);});
    return ()=>controller.abort();
  },[project,path,original,version]);
  const parsed=useMemo(()=>diff?parse(diff.text):[],[diff]);
  const lines=parsed.slice(0,12000);
  return <div className="diff-viewer">
    <div className="diff-toolbar"><span title={path}>{path}</span><div role="group" aria-label="Diff 显示方式">
      <button aria-pressed={!original} onClick={()=>toggle(false)}>Unified</button>
      <button aria-pressed={original} onClick={()=>toggle(true)}>原文件</button>
    </div></div>
    {original ? <div className="diff-scroll" tabIndex={0} aria-label="当前磁盘文件">{readError?<p className="inspector-empty">{readError}</p>:
      file?.kind==="text"?<pre>{file.content}</pre>:file?.kind==="image"?<div className="file-image"><img src={"/api"+fileUrl(project,path)+"&image=true"} alt={path}/></div>:
      <p className="inspector-empty">{file?"文件类型或大小超出预览范围":"正在读取…"}</p>}</div>:
      error?<p className="inspector-empty" role="alert">{error}</p>:!diff?.text?<p className="inspector-empty">{diff?"文件没有文本差异":"正在读取 Diff…"}</p>:
      <div className="diff-scroll" aria-label="统一差异" tabIndex={0}>
        {(diff.truncated||lines.length<parsed.length)&&<p role="status">差异过长，已截断预览</p>}
        <div className="diff-lines">{lines.map((line,i)=><div className={"diff-line "+line.kind} key={i}>
          <span aria-hidden="true">{line.old}</span><span aria-hidden="true">{line.next}</span><code>{line.code||" "}</code>
        </div>)}</div>
      </div>}
  </div>;
}
