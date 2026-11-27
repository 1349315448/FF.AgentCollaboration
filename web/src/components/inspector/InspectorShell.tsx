import { createPortal } from "react-dom";
import { lazy, Suspense, useEffect, useState } from "react";
import { WorkspaceIcon } from "../layout/WorkspaceIcon";
import "../../styles/inspector.css";
import "../../styles/git.css";
import { useWorkbench } from "../../store/workbenchStore";
import { knowledgeModeFor } from "../../services/knowledge";
import type { GitChange } from "./gitWorkspace";
const CodeViewer = lazy(()=>import("./CodeViewer"));
const CollaborationDocuments = lazy(()=>import("./CollaborationDocuments"));
const GitPanel = lazy(()=>import("./GitPanel"));

const primarySections = [
  { id: "files", title: "文件" },
  { id: "docs", title: "Agent 协作文档" }
] as const;

type SectionId = (typeof primarySections)[number]["id"] | "changes";

export function InspectorShell({toolbarHost}:{toolbarHost:HTMLDivElement | null}) {
  const [current, select] = useState<SectionId>("files");
  const [showKnowledge,setShowKnowledge]=useState(false);
  const [diffFile,setDiffFile]=useState<{project:string;change:GitChange}>();
  const {snapshot,topic}=useWorkbench();
  const project=topic?.project ?? snapshot?.project;
  const [documentLink,setDocumentLink]=useState({topic:"",id:"",sequence:0});
  useEffect(()=>{
    const open=(e:Event)=>{
      const {topic:owner,id}=(e as CustomEvent<{topic:string;id:string}>).detail;
      if (owner!==topic?.id) return;
      if(document.getElementById("inspector-shell")?.hidden) document.getElementById("toggle-inspector")?.click();
      select("docs");setShowKnowledge(false);setDocumentLink(v=>({topic:owner,id,sequence:v.sequence+1}));
    };
    window.addEventListener("ff:open-document",open);
    return ()=>window.removeEventListener("ff:open-document",open);
  },[topic?.id]);
  useEffect(()=>{
    const open=()=>{
      if(document.getElementById("inspector-shell")?.hidden) document.getElementById("toggle-inspector")?.click();
      select("docs");setShowKnowledge(true);
    };
    window.addEventListener("ff:open-knowledge",open);
    return ()=>window.removeEventListener("ff:open-knowledge",open);
  },[]);
  const allIds: SectionId[] = ["files", "docs", "changes"];
  const handleKeyDown = (id: SectionId, event: React.KeyboardEvent) => {
    const i = allIds.indexOf(id);
    const next = { ArrowLeft: (i + allIds.length - 1) % allIds.length, ArrowRight: (i + 1) % allIds.length, Home: 0, End: allIds.length - 1 }[event.key];
    if (next === undefined) return;
    event.preventDefault();
    const target = allIds[next];
    select(target);
    document.getElementById("inspector-tab-" + target)?.focus();
  };
  return <>
    {toolbarHost && createPortal(<div className="inspector-header">
      <div role="tablist" aria-label="代码工作台视图">{primarySections.map(section =>
        <button key={section.id} id={"inspector-tab-" + section.id} type="button" role="tab" aria-controls="inspector-content"
          tabIndex={section.id === current ? 0 : -1} aria-selected={section.id === current} onClick={() => select(section.id)}
          onKeyDown={event => handleKeyDown(section.id, event)} aria-label={section.title} title={section.title}><WorkspaceIcon name={section.id}/><span>{section.title}</span></button>)}
        <button id="inspector-tab-changes" type="button" role="tab" className="inspector-utility-tab" aria-controls="inspector-content"
          aria-label="变更" title="Git 变更与提交" tabIndex={current === "changes" ? 0 : -1} aria-selected={current === "changes"}
          onClick={() => select("changes")} onKeyDown={event => handleKeyDown("changes", event)}>
          <WorkspaceIcon name="changes"/><span>变更</span>
        </button>
      </div>
      <button id="inspector-panel-action" type="button" className="text-button"><WorkspaceIcon name="panel"/><span>收起面板</span></button>
    </div>, toolbarHost)}
    <aside id="inspector-shell" aria-label="代码工作台" hidden>
    <section id="inspector-content" data-section={current} role="tabpanel" aria-labelledby={"inspector-tab-" + current}>
      {!project?<p className="inspector-empty">请先选择项目</p>:
        <Suspense fallback={<p className="inspector-empty">正在打开…</p>}>
          {current==="changes"?<GitPanel key={project} initial={diffFile?.project===project?diffFile.change:undefined}/>:current==="files"?<CodeViewer key={project} project={project}
            openDiff={change=>{setDiffFile({project,change});select("changes");}}/>:<CollaborationDocuments key={(topic?.id??project)+documentLink.sequence}
            project={project} topic={topic?.id} revision={snapshot?.timelineTopic===topic?.id?snapshot?.timeline?.[0]?.id:undefined}
            mode={knowledgeModeFor(snapshot?.knowledgeModes,project)} showKnowledge={showKnowledge} setShowKnowledge={setShowKnowledge}
            initial={documentLink.topic===topic?.id?documentLink.id:""}/>}
        </Suspense>}
    </section>
  </aside></>;
}
