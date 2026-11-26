import { memo, useEffect, useState } from "react";
import {MailDetail} from "./MailDetail";
import { MarkdownBody } from "./MarkdownBody";
import { request } from "../../services/api";
import { WorkspaceIcon } from "../layout/WorkspaceIcon";
import { fileUrl, workspaceUrl, type FileView } from "./workspaceFiles";

interface Doc {id:string;name:string;path:string}
const readme:Doc={id:"readme",name:"README.md",path:"README.md"};
export default memo(function DocPreview({project,topic,revision,initial}:{project:string;topic?:string;revision?:string;initial:string}) {
  const [docs,setDocs]=useState<Doc[]>([]), [active,select]=useState(initial||"readme");
  const [body,setBody]=useState(""), [error,setError]=useState(""), [listError,setListError]=useState("");
  useEffect(()=>{
    if (!topic) { setDocs([]); return; }
    const control=new AbortController();
    request<Doc[]>(workspaceUrl("documents",{topic}),{signal:control.signal})
      .then(rows=>{if(!control.signal.aborted){setDocs(rows);setListError("");}})
      .catch(e=>{if(!control.signal.aborted)setListError(e.message);});
    return ()=>control.abort();
  },[topic,revision]);
  useEffect(()=>{if(initial) select(initial);},[initial]);
  useEffect(()=>{
    const control=new AbortController();
    setBody("");setError("");
    if(active.startsWith("message:") || active.startsWith("peer:")) return ()=>control.abort();
    const route=active==="readme"?fileUrl(project,"README.md"):workspaceUrl("documents/"+active,{topic:topic??""});
    request<FileView>(route,{signal:control.signal}).then(f=>{
      if (control.signal.aborted) return;
      if (f.kind==="text") setBody(f.content??"");
      else setError(f.kind==="large"?"文档超过 512 KB，无法预览":"文档无法显示为文本");
    }).catch(e=>{if (!control.signal.aborted) setError(active==="readme" && e.status===404?"项目中没有 README.md":e.message);});
    return ()=>control.abort();
  },[project,topic,active]);
  return <div className="docs-workspace"><nav aria-label="话题文档">
    {[readme,...docs].map(doc=><button key={doc.id} title={doc.path} aria-current={active===doc.id?"page":undefined} onClick={()=>select(doc.id)}><WorkspaceIcon name="docs"/><span>{doc.name}</span></button>)}
  </nav>{listError && <p role="alert">{listError}</p>}<article className="markdown-preview" aria-label="文档预览">
    {active.startsWith("message:") || active.startsWith("peer:")?<MailDetail key={active} reference={active}/>:error?<p role="status">{error}</p>:<MarkdownBody content={body}/>}
  </article></div>;
});
