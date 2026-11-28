import { useEffect, useState } from "react";
import { request } from "../../services/api";
import type { FileView } from "./workspaceFiles";
import RichPreview from "./RichPreview";
import CodeText from "./CodeText";
import { MarkdownBody } from "./MarkdownBody";

export interface ChatFileSelection {
  selected:string;
  files:{name:string;key:string;route:string}[];
}

export default function ChatFilePreview({selection,close}:{selection:ChatFileSelection;close:()=>void}) {
  const [selected,select]=useState(selection.selected);
  useEffect(()=>select(selection.selected),[selection]);
  const entry=selection.files.find(f=>f.key===selected)!;
  return <section className="docs-workspace" aria-label="群聊附件">
    <nav aria-label="本条消息的附件">{selection.files.map(f=><button key={f.key} type="button"
      aria-current={f.key===selected?"page":undefined} onClick={()=>select(f.key)}>{f.name}</button>)}</nav>
    <div className="code-workspace"><div className="file-actions">
      <button type="button" onClick={close}>返回项目文件</button>
      <a href={"/api"+entry.route} download={entry.name}>下载附件</a></div>
      <Contents key={entry.route} route={entry.route}/>
    </div>
  </section>;
}

function Contents({route}:{route:string}) {
  const [file,setFile]=useState<FileView>(),[error,setError]=useState("");
  useEffect(()=>{
    const controller=new AbortController();
    request<FileView>(route+"?view=text",{signal:controller.signal}).then(setFile)
      .catch(e=>{if(!controller.signal.aborted)setError(e.message);});
    return ()=>controller.abort();
  },[route]);
  if(!file)return <p className="inspector-empty" role="status">{error||"正在打开附件…"}</p>;
  if(file.kind==="text")return /\.md$/i.test(file.path)?<article className="markdown-preview"><MarkdownBody content={file.content??""}/></article>
    :<CodeText path={file.path} content={file.content??""}/>;
  return <RichPreview file={file} url={"/api"+route+"?view=inline"}/>;
}
