import { lazy, Suspense, useEffect, useState } from "react";
import DocPreview from "./DocPreview";
import { knowledgeUiLocal, type KnowledgeMode } from "../../services/knowledge";

const KnowledgeBrowser = lazy(()=>import("./KnowledgeBrowser"));

export default function CollaborationDocuments({project,topic,revision,initial,mode,showKnowledge,setShowKnowledge}:{
  project:string; topic?:string; revision?:string; initial:string; mode:KnowledgeMode;
  showKnowledge:boolean; setShowKnowledge:(open:boolean)=>void
}) {
  const [openedKnowledge,setOpenedKnowledge]=useState(showKnowledge);
  useEffect(()=>{if(showKnowledge)setOpenedKnowledge(true);},[showKnowledge]);
  return <>
    <div className="knowledge-switch" role="group" aria-label="协作文档来源">
      <button type="button" aria-pressed={!showKnowledge} onClick={()=>setShowKnowledge(false)}>交接文档</button>
      <button type="button" aria-pressed={showKnowledge} disabled={!knowledgeUiLocal}
        title={knowledgeUiLocal?undefined:"请在工作台主机上浏览知识库"} onClick={()=>setShowKnowledge(true)}>知识库</button>
    </div>
    <div className="knowledge-area" hidden={showKnowledge}>
      <DocPreview project={project} topic={topic} revision={revision} initial={initial}/>
    </div>
    {(showKnowledge || openedKnowledge) && <div className="knowledge-area" hidden={!showKnowledge}>
      <Suspense fallback={<p className="inspector-empty">正在打开知识库…</p>}>
        <KnowledgeBrowser project={project} topic={topic} mode={mode}/>
      </Suspense>
    </div>}
  </>;
}
