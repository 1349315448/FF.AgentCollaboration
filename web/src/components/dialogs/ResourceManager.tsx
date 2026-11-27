import "./managedMcps.css";
import { lazy, Suspense, useRef, useState } from "react";
import { createPortal } from "react-dom";
import { knowledgeUiLocal } from "../../services/knowledge";

const Mcps=lazy(()=>import("./ManagedMcps").then(m=>({default:m.ManagedMcps})));
const Skills=lazy(()=>import("./ManagedSkills").then(m=>({default:m.ManagedSkills})));
const kinds=["MCP","Skills"] as const;
type Kind=(typeof kinds)[number];

export function ResourceManager() {
  const dialog=useRef<HTMLDialogElement>(null);
  const [kind,select]=useState<Kind|null>(null);
  function open(value:Kind) {
    select(value);
    dialog.current?.showModal();
  }
  return <>
    <div className="resource-launchers" role="group" aria-label="扩展管理">
      <button type="button" aria-controls="inspector-shell" disabled={!knowledgeUiLocal}
        title={knowledgeUiLocal?"打开项目知识库":"请在工作台主机上浏览知识库"}
        onClick={()=>window.dispatchEvent(new Event("ff:open-knowledge"))}>知识库</button>
      {kinds.map(value=><button type="button" key={value} aria-haspopup="dialog" aria-controls="resource-dialog"
        aria-label={value+" 管理"} title={value+" 管理"} onClick={()=>open(value)}>{value}<span> 管理</span></button>)}
    </div>
    {createPortal(<dialog id="resource-dialog" ref={dialog} closedby="any" aria-label="MCP 与 Skills 管理" onClose={()=>select(null)}>
      <header className="dialog-header"><div className="resource-kinds" role="group" aria-label="管理类别">
        {kinds.map(value=><button type="button" key={value} aria-pressed={kind===value} onClick={()=>select(value)}>{value} 管理</button>)}
      </div><button className="icon-button" type="button" aria-label="关闭管理面板" onClick={()=>dialog.current?.close()}>×</button></header>
      {kind && <Suspense fallback={<p role="status">正在打开…</p>}>{kind==="MCP"?<Mcps/>:<Skills/>}</Suspense>}
    </dialog>,document.body)}
  </>;
}
