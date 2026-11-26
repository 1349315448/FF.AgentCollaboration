import { useState, type ReactNode } from "react";
import { engineNames } from "../../services/engines";
import { WorkspaceIcon } from "../layout/WorkspaceIcon";

export function CatalogItem({name,meta,onClick}:{name:string;meta:string;onClick:()=>void}) {
  return <button type="button" className="catalog-item" aria-label={name} onClick={onClick}>
    <strong>{name}</strong><small>{meta}</small><WorkspaceIcon name="right"/>
  </button>;
}

interface CatalogProps {
  kind:"MCP"|"Skills";
  source:string;
  onSource:(id:string)=>void;
  search:string;
  onSearch:(value:string)=>void;
  projects:string[];
  project:string;
  onProject:(value:string)=>void;
  refresh:()=>void;
  busy:boolean;
  loading:boolean;
  detail?:string;
  back:()=>void;
  actions?:ReactNode;
  diagnostics:string[];
  notice:string;
  children:ReactNode;
}

export function ResourceCatalog(p:CatalogProps) {
  const [expanded,setExpanded]=useState(true);
  const native=p.source!=="platform";
  return <section className="mcp-manager" aria-label={p.kind+" 管理"}>
    <nav className="catalog-sources" aria-label={p.kind+" 来源"}>
      <button type="button" aria-current={!native?"true":undefined} disabled={p.busy} onClick={()=>p.onSource("platform")}><WorkspaceIcon name="panel"/>平台配置</button>
      <button type="button" aria-expanded={expanded} onClick={()=>setExpanded(!expanded)}><WorkspaceIcon name={expanded?"down":"right"}/>CLI 原生</button>
      {expanded && <div className="catalog-engines">{Object.entries(engineNames).map(([id,label])=>
        <button type="button" key={id} disabled={p.busy} title={label} aria-current={id===p.source?"true":undefined} onClick={()=>p.onSource(id)}>{label}</button>)}</div>}
    </nav>
    <div className="catalog-main">
      <div className="catalog-heading">{p.detail && <button type="button" className="text-button" disabled={p.busy} onClick={p.back}>返回列表</button>}
        <h3>{p.detail || (native?engineNames[p.source]:"平台配置")}</h3>
        {!p.detail && <button type="button" className="text-button" disabled={p.busy} onClick={p.refresh}>刷新</button>}
      </div>
      {!p.detail && <div className="catalog-tools">
        <input type="search" aria-label={"搜索 "+p.kind} placeholder={"搜索 "+p.kind} value={p.search} onChange={e=>p.onSearch(e.target.value)}/>
        {native && p.projects.length>0 && <select aria-label="查看项目" disabled={p.busy} value={p.project} onChange={e=>p.onProject(e.target.value)}>
          <option value="">用户配置</option>{p.projects.map(path=><option key={path}>{path}</option>)}</select>}
        {p.actions}
      </div>}
      {p.notice && <p role="status" className="skill-status">{p.notice}</p>}
      <fieldset className="catalog-body" disabled={p.busy}>
        {p.loading && !p.detail && <small role="status">正在读取配置…</small>}
        {p.children}
      </fieldset>
      {!p.detail && p.diagnostics.length>0 && <details className="catalog-diagnostics"><summary>扫描详情（{p.diagnostics.length}）</summary>
        {p.diagnostics.map(message=><p key={message}>{message}</p>)}</details>}
    </div>
  </section>;
}
