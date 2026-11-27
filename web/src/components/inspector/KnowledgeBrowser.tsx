import { useEffect, useState } from "react";
import { request } from "../../services/api";
import { useWorkbench } from "../../store/workbenchStore";
import { WorkspaceIcon } from "../layout/WorkspaceIcon";
import { MarkdownBody } from "./MarkdownBody";
import type { KnowledgeMode } from "../../services/knowledge";

interface Entry {
  path:string;
  title?:string;
  scope:"project"|"topic"|"agent";
  agentId?:string;
  owner?:string;
  theme?:string;
}
interface KnowledgeDocument { path:string; content:string; body?:string; version?:string; frontmatter?:{topicId?:string} }
const recordsPageSize=40;

function knowledgeUrl(route:string, project:string, topicId?:string, params:Record<string,string> = {}) {
  return "/knowledge/"+route+"?"+new URLSearchParams({project, ...(topicId?{topicId}:{}), ...params});
}

function markdown(document:KnowledgeDocument) {
  return document.body ?? document.content.replace(/^\uFEFF?---[ \t]*\r?\n(?:.*\r?\n)*?(?:---|\.\.\.)[ \t]*(?:\r?\n|$)/, "");
}

export default function KnowledgeBrowser({project,topic,mode}:{project:string;topic?:string;mode:KnowledgeMode}) {
  const {snapshot}=useWorkbench();
  const [query,setQuery]=useState("");
  const [recordTopic,setRecordTopic]=useState<string>();
  const [offset,setOffset]=useState(0);
  const [more,setMore]=useState(false);
  const recordsOnly=!!topic && recordTopic===topic;
  const [entries,setEntries]=useState<Entry[]>([]);
  const [selection,select]=useState("");
  const [document,setDocument]=useState<KnowledgeDocument>();
  const [draft,setDraft]=useState<string>();
  const [searching,setSearching]=useState(false);
  const [saving,setSaving]=useState(false);
  const [listError,setListError]=useState("");
  const [documentError,setDocumentError]=useState("");
  const [revision,setRevision]=useState(0);
  const [documentRevision,reloadDocument]=useState(0);

  useEffect(()=>{
    if (mode==="disabled") return;
    const control=new AbortController();
    setSearching(true);
    setListError("");
    const timer=setTimeout(()=>{
      const params:Record<string,string>=recordsOnly ? {query:query.trim(),limit:String(recordsPageSize+1),recordsOnly:"true",offset:String(offset)}
        : {query:query.trim(),limit:"100"};
      const url=knowledgeUrl("search",project,topic,params);
      request<Entry[]>(url,{signal:control.signal}).then(rows=>{
        if (!control.signal.aborted) {
          setMore(recordsOnly && rows.length>recordsPageSize);
          setEntries(recordsOnly ? rows.slice(0,recordsPageSize) : rows);
        }
      }).catch(error=>{
        if (!control.signal.aborted) {setEntries([]);setListError(error.message);}
      }).finally(()=>{if (!control.signal.aborted) setSearching(false);});
    },query?180:0);
    return ()=>{clearTimeout(timer);control.abort();};
  },[project,topic,mode,query,revision,recordsOnly,offset]);
  useEffect(()=>{
    setDocument(undefined);
    setDocumentError("");
    if (!selection || mode==="disabled") return;
    const control=new AbortController();
    request<KnowledgeDocument>(knowledgeUrl("document",project,topic,{path:selection}),{signal:control.signal})
      .then(value=>{if (!control.signal.aborted) setDocument(value);})
      .catch(error=>{if (!control.signal.aborted) setDocumentError(error.message);});
    return ()=>control.abort();
  },[project,topic,mode,selection,documentRevision]);

  if (mode==="disabled") return <p className="inspector-empty">项目知识库尚未启用。双击左侧项目可打开项目设置。</p>;
  const dirty=draft!==undefined && draft!==document?.content;
  const isTopicPlan=!!topic && document?.frontmatter?.topicId===topic && /^topics\/[^/]+\/plan\.md$/i.test(document.path);
  function showTasks() {
    setRecordTopic(topic);setOffset(0);setQuery("");setRevision(value=>value+1);
  }
  const discard=()=>!dirty || window.confirm("放弃未保存的知识库修改？");
  function open(path:string) {
    if (path===selection) {
      if (!document && documentError) reloadDocument(value=>value+1);
      return;
    }
    if (saving || !discard()) return;
    setDraft(undefined);
    select(path);
  }
  async function save() {
    if (!document || !dirty || saving) return;
    setSaving(true);
    setDocumentError("");
    try {
      const saved=await request<KnowledgeDocument|null>("/knowledge/document",{method:"PUT",body:{
        project,topicId:topic,path:document.path,content:draft,version:document.version
      }});
      setDocument(saved ?? await request<KnowledgeDocument>(knowledgeUrl("document",project,topic,{path:document.path})));
      setDraft(undefined);
      setRevision(value=>value+1);
    } catch (e) {
      setDocumentError((e as Error).message);
    } finally {
      setSaving(false);
    }
  }

  const agents=new Map<string,{name:string; entries:Entry[]}>();
  const names=new Map(snapshot?.windows.map(({agent})=>[agent.id,"@"+agent.name]));
  for (const entry of entries.filter(row=>row.scope==="agent")) {
    const key=entry.agentId ?? entry.path.split("/")[1];
    if (!agents.has(key)) agents.set(key,{name:names.get(key) ?? entry.owner ?? key,entries:[]});
    agents.get(key)!.entries.push(entry);
  }
  function rows(items:Entry[]) {
    return items.map(entry=><button type="button" key={entry.path} title={[entry.path,entry.theme].filter(Boolean).join(" · ")}
      onClick={()=>open(entry.path)} aria-current={selection===entry.path?"page":undefined}>
      <WorkspaceIcon name="docs"/><span>{entry.title || entry.path.split("/").at(-1)}</span>
    </button>);
  }
  function group(items:Entry[],depth:number) {
    const folders=new Map<string,Entry[]>();
    const files=items.filter(entry=>{
      const path=entry.path.split("/");
      if (path.length<=depth+1) return true;
      const folder=path[depth];
      const child=folders.get(folder) ?? [];
      child.push(entry);folders.set(folder,child);
      return false;
    });
    return <>{rows(files)}{[...folders].map(([folder,child])=><details className="knowledge-folder" key={folder} open>
      <summary>{folder}/</summary>{group(child,depth+1)}
    </details>)}</>;
  }
  return <div className="docs-workspace knowledge-workspace">
    <nav aria-label="知识库目录">
      <label className="tree-search"><WorkspaceIcon name="search"/><input type="search" value={query} maxLength={200}
        aria-label="搜索知识库" placeholder="搜索关键词或主题" onChange={event=>{setQuery(event.target.value);setOffset(0);}}/></label>
      {topic && <div className="file-actions">{recordsOnly ? <>
        <button type="button" onClick={()=>{setRecordTopic(undefined);setOffset(0);}}>返回全部文档</button>
        <button type="button" disabled={searching} onClick={()=>setRevision(value=>value+1)}>刷新记录</button>
      </> : <button type="button" onClick={showTasks}>成员任务记录</button>}</div>}
      <div className="knowledge-tree">
        {listError?<p role="alert" className="file-error">{listError}</p>:!entries.length?<p role="status" className="file-notice">
          {searching?"正在检索…":query?"没有匹配的知识":recordsOnly?"暂无成员任务记录":"暂无知识库文档"}</p>:recordsOnly ?
          <details open><summary>成员任务记录</summary>{group(entries,3)}</details>:<>
          <details open><summary>项目级</summary>{group(entries.filter(row=>row.scope==="project"),1)}</details>
          {topic && <details open><summary>当前话题</summary>{group(entries.filter(row=>row.scope==="topic"),2)}</details>}
          <details open><summary>Agent 私有记忆</summary>
            {[...agents.entries()].map(([id,agent])=><details className="knowledge-folder" key={id} open>
              <summary title={agent.name}>{agent.name}</summary>{group(agent.entries,2)}</details>)}
          </details>
        </>}
        {recordsOnly && (more || offset>0) && <div className="file-actions">
          <button type="button" disabled={searching || !offset} onClick={()=>setOffset(value=>Math.max(0,value-recordsPageSize))}>上一页</button>
          <button type="button" disabled={searching || !more} onClick={()=>setOffset(value=>value+recordsPageSize)}>下一页</button>
        </div>}
      </div>
    </nav>
    <section className="code-workspace" aria-label="知识库文档">
      {document && <div className="file-caption" title={document.path}>{document.path}</div>}
      {documentError && <p className="file-error" role="alert">{documentError}</p>}
      {draft!==undefined?<textarea className="source-editor" aria-label="知识库 Markdown" value={draft}
        disabled={saving} spellCheck={false} onChange={event=>setDraft(event.target.value)}/>:
        document?<article className="markdown-preview" aria-label="知识库预览"><MarkdownBody content={markdown(document)}
          onTaskRecords={isTopicPlan?showTasks:undefined}/>
          {isTopicPlan && <button className="text-button plan-records-fallback" type="button" onClick={showTasks}>成员任务记录</button>}
        </article>:
        <p className="inspector-empty">{selection&&!documentError?"正在读取…":"选择知识库文档"}</p>}
      {document && <footer className="file-actions">
        {draft===undefined?<button type="button" onClick={()=>setDraft(document.content)}><WorkspaceIcon name="edit"/>编辑</button>:<>
          <button type="button" disabled={!dirty || saving} onClick={()=>void save()}>{saving?"保存中…":"保存"}</button>
          <button type="button" disabled={saving} onClick={()=>{if (discard()) setDraft(undefined);}}>取消</button>
        </>}
        {documentError && <button type="button" disabled={saving} onClick={()=>{
          if (discard()) {setDraft(undefined);reloadDocument(value=>value+1);}
        }}>重新读取</button>}
      </footer>}
    </section>
  </div>;
}
