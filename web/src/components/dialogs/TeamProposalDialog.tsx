import { useEffect, useRef, useState } from "react";
import { request } from "../../services/api";
import { useEngineQuota } from "../../services/engineQuota";
import { accountReady, checkAccount, engineCapabilities, engineNames, type EngineCapabilities, type EngineModel } from "../../services/engines";
import type { CreatedTeam, TeamProposal } from "../../services/teamProposals";
import { isDialogOpen, useWorkbench } from "../../store/workbenchStore";
import { LeaderFlag } from "../layout/LeaderFlag";
import { WorkspaceIcon } from "../layout/WorkspaceIcon";
import { createPortal } from "react-dom";
import "./teamProposal.css";

interface MemberDraft {
  selected: boolean;
  remoteId: string;
  index: number;
  name: string;
  role: string;
  engine: string;
  model: string;
  effort: string;
  policy: string;
  mode: string;
}

interface Catalog {
  ready: boolean;
  models: EngineModel[];
  message?: string;
}

function EngineOption({engine, installed}:{engine:string; installed:boolean}) {
  const quota = useEngineQuota(engine);
  const state = installed ? "已安装" + (quota && !quota.loading && quota.value !== "—" ? " · 剩余 " + quota.value : "") : "未安装";
  return <option value={engine} disabled={!installed}>{engineNames[engine]} · {state}</option>;
}

function MemberFields({draft, name, engines, capabilities, catalog, update}:{
  draft:MemberDraft; name:string; engines:Record<string,boolean>; capabilities?:EngineCapabilities; catalog?:Catalog;
  update:(fields:Partial<MemberDraft>)=>void;
}) {
  const chosenModel = catalog?.models.find(item => item.id === draft.model);
  const efforts = capabilities?.efforts === false ? [] : (chosenModel ?? catalog?.models.find(item => item.isDefault))?.efforts ?? [];
  const approval = capabilities?.startupApproval;
  const otherModes = (capabilities?.modes ?? []).filter(mode => mode.id !== approval?.ask && mode.id !== approval?.auto);
  return <div className="team-member-fields">
    <div className="team-member-text">
      <label className="field">成员名称<input required aria-label={name+"的成员名称"} maxLength={40} value={draft.name} onChange={event=>update({name:event.target.value})}/></label>
      <label className="field">身份提示词<textarea aria-label={name+"的身份提示词"} maxLength={200} rows={3} value={draft.role}
        onChange={event=>update({role:event.target.value})}/></label>
    </div>
    <div>
      <div className="team-engine-fields">
        <label className="field">引擎
          <select aria-label={name+"的引擎"} value={draft.engine} onChange={event=>update({engine:event.target.value, model:"", effort:"", mode:"", policy:""})}>
            <option value="" disabled>选择已安装引擎</option>
            {Object.keys(engines).map(engine=><EngineOption key={engine} engine={engine} installed={engines[engine]}/>)}
          </select>
        </label>
        <label className="field">模型<select aria-label={name+"的模型"} value={chosenModel?.id ?? ""} disabled={!catalog?.models.length}
          onChange={event=>update({model:event.target.value, effort:""})}>
          <option value="">跟随引擎默认</option>
          {catalog?.models.map(choice=><option key={choice.id} value={choice.id}>{choice.name}{choice.isDefault ? "（默认）" : ""}</option>)}
        </select></label>
        <label className="field">推理档位<select aria-label={name+"的推理档位"} disabled={!efforts.length}
          value={efforts.includes(draft.effort) ? draft.effort : ""} onChange={event=>update({effort:event.target.value})}>
          <option value="">跟随模型默认</option>
          {efforts.map(effort=><option key={effort}>{effort}</option>)}
        </select></label>
        <label className="field">审批权限<select aria-label={name+"的审批权限"} value={draft.policy} disabled={!approval && !otherModes.length}
          onChange={event=>update({policy:event.target.value})}>
          <option value="">跟随引擎默认</option>
          {approval && <><option value="ask">需要审批</option><option value="auto">{draft.engine==="copilot" ? "自动审批（允许所有工具）" : "自动审批"}</option></>}
          {draft.engine!=="copilot" && otherModes.map(choice=><option key={choice.id} value={"mode:"+choice.id}>{choice.label}</option>)}
        </select></label>
        {draft.engine==="copilot" && capabilities?.modes.length ? <label className="field">运行模式<select value={draft.mode}
          onChange={event=>update({mode:event.target.value})}>
          <option value="">跟随引擎默认</option>
          {capabilities.modes.map(choice=><option key={choice.id} value={choice.id}>{choice.label}</option>)}
        </select></label> : null}
      </div>
      {draft.policy==="auto" && approval && <small className="team-field-message">{approval.description}</small>}
      {catalog?.message ? <small className="team-field-message">{catalog.message}</small> : draft.engine && engines[draft.engine] && !catalog ?
        <small className="team-field-message">正在检查登录与模型…</small> : null}
      {catalog?.ready && draft.model && !chosenModel && <small className="team-field-message">推荐的模型不可用，已选用引擎默认。</small>}
    </div>
  </div>;
}

function TeamProposalForm({proposal, project, active, close, onBusy}:{
  proposal:TeamProposal; project:string; active:boolean; close:()=>void; onBusy:(busy:boolean)=>void;
}) {
  const [drafts,setDrafts] = useState<MemberDraft[]>(()=>proposal.members.map((member,index)=>({
    index, selected:true, remoteId:member.remoteId ?? "", name:member.name, role:member.role ?? "", engine:member.suggestedEngine ?? "",
    model:member.suggestedModel ?? "", effort:member.suggestedEffort ?? "", policy:"", mode:""
  })));
  const [engines,setEngines] = useState<Record<string,boolean>>({});
  const [profiles,setProfiles] = useState<Record<string,EngineCapabilities>>({});
  const [catalogs,setCatalogs] = useState<Record<string,Catalog>>({});
  const [checks,setChecks] = useState(0);
  const [loading,setLoading] = useState(true);
  const [busy,setBusy] = useState(false);
  const [error,setError] = useState("");
  const [outcome,setOutcome] = useState<CreatedTeam>();
  const {snapshot} = useWorkbench();
  const peers = (snapshot?.federation?.remotes ?? []).filter(peer => peer.topicId === proposal.topicId &&
    peer.status === "approved" && !peer.teamMember && !peer.deniedAgentIds?.includes(proposal.leaderWindowId));

  const available = Object.keys(engines).find(engine=>engines[engine]) ?? "";
  const rows = drafts.map(draft=>draft.engine ? draft : {...draft, engine:available});
  const selected = rows.filter(row=>row.selected);
  const localRows = selected.filter(row=>!row.remoteId);
  const engineKeys = [...new Set(localRows.map(row=>row.engine).filter(engine=>engine && engines[engine]))].sort().join(",");
  const ready = !!selected.length && localRows.every(row=>!loading && catalogs[row.engine]?.ready) &&
    selected.filter(row=>row.remoteId).every(row=>row.role.trim() && peers.some(peer=>peer.id===row.remoteId) &&
      !selected.some(other=>other.index!==row.index && other.remoteId===row.remoteId));

  useEffect(()=>{
    const controller = new AbortController();
    setLoading(true);
    Promise.all([request<Record<string,boolean>>("/engines",{signal:controller.signal}),engineCapabilities()])
      .then(([installed,options])=>{
        if (!controller.signal.aborted) { setEngines(installed); setProfiles(options); }
      }).catch(failure=>{ if (!controller.signal.aborted) setError((failure as Error).message); })
      .finally(()=>{ if (!controller.signal.aborted) setLoading(false); });
    return ()=>controller.abort();
  },[checks]);

  useEffect(()=>{
    if (loading || !active || outcome || !engineKeys) return;
    const controller = new AbortController();
    const needed = engineKeys.split(",").filter(engine=>!catalogs[engine]);
    for (const engine of needed) {
      void import("../../runtime/quota.js").then(({loadQuota})=>{ if (!controller.signal.aborted) loadQuota(engine); });
      void (async ()=>{
        const suffix = "?" + new URLSearchParams({project});
        try {
          const account = await checkAccount(engine,project,controller.signal);
          if (controller.signal.aborted) return;
          if (!accountReady(account)) {
            const message = account.message || (account.status==="signed-out" ? "请先登录此引擎，或为成员另选引擎。" : "暂时无法确认引擎账号。");
            setCatalogs(current=>({...current,[engine]:{ready:false,models:[],message}}));
            return;
          }
          let models:EngineModel[] = [], message:string|undefined;
          try {
            if (profiles[engine]?.models !== false)
              models = await request<EngineModel[]>("/models/"+engine+suffix,{signal:controller.signal});
          } catch { message = "无法读取模型目录，可以沿用引擎默认。"; }
          if (!controller.signal.aborted) setCatalogs(current=>({...current,[engine]:{ready:true,models,message}}));
        } catch(failure) {
          if (!controller.signal.aborted) setCatalogs(current=>({...current,[engine]:{ready:false,models:[],message:(failure as Error).message}}));
        }
      })();
    }
    return ()=>controller.abort();
  },[active, engineKeys, profiles, project, loading, outcome]);

  function update(index:number, fields:Partial<MemberDraft>) {
    setDrafts(current=>current.map(row=>row.index===index ? {...row,...fields} : row));
  }

  async function resolve(action:"confirm"|"reject") {
    if (!active || busy || outcome || (action==="confirm" && !ready)) return;
    setBusy(true);
    onBusy(true);
    setError("");
    try {
      const route = "/topics/"+proposal.topicId+"/team-proposals/"+proposal.id+"/"+action;
      if (action==="reject") {
        await request(route,{method:"POST"});
        close();
        return;
      }
      const members = selected.map(({index, name, role, engine, model, effort, mode, policy, remoteId})=>{
        if (remoteId) return {index, remoteId, role};
        const models = catalogs[engine]?.models;
        const chosen = models?.find(item=>item.id===model);
        const efforts = profiles[engine]?.efforts===false ? [] : (chosen ?? models?.find(item=>item.isDefault))?.efforts;
        return {
          index, ...(proposal.members[index].remoteId ? {remoteId:""} : {}), name:name.trim(), role, engine, model:chosen?.id ?? null, effort:efforts?.includes(effort) ? effort : null,
          mode:policy.startsWith("mode:") ? policy.slice(5) : mode || null, approval:policy==="ask" || policy==="auto" ? policy : null
        };
      });
      setOutcome(await request<CreatedTeam>(route,{method:"POST",body:{members}}));
    } catch(failure) {
      setError((failure as Error).message);
    } finally {
      setBusy(false);
      onBusy(false);
    }
  }

  return <form onSubmit={event=>{event.preventDefault(); void resolve("confirm");}}>
    <header className="dialog-header">
      <div><h2 id="team-proposal-title">团队组建确认</h2>
        <p className="team-proposer"><LeaderFlag />{proposal.leaderName.startsWith("@") ? proposal.leaderName : "@"+proposal.leaderName}</p></div>
      <button className="icon-button" type="button" aria-label="关闭团队提案" title="关闭" disabled={busy} onClick={close}>×</button>
    </header>
    <div className="dialog-body team-proposal-body">
      <p className="team-proposal-summary">{proposal.summary}</p>
      {outcome ? <div className="team-proposal-result" role="status">
        <h3>{outcome.status==="created" ? "团队成员已创建" : outcome.status==="partial" ? "部分成员未能启动" : "成员创建未完成"}</h3>
        <ul>{outcome.members.map(result=><li key={result.index}>
          <strong>@{result.name}</strong><span>{result.remoteId ? "外部成员已加入 · "+(result.online ? "在线" : "离线") :
            result.online ? "在线待命" : result.windowId ? "已创建，当前离线" : "未创建"}</span>
          {result.error && <p>{result.error}</p>}
        </li>)}</ul>
        {outcome.rosterWarning && <p className="field-hint">{outcome.rosterWarning}</p>}
      </div> : <>
        {!active && !busy && <p role="status">该提案已结束，请关闭后查看当前提案。</p>}
        {(active || busy) && <div className="team-proposal-members">{rows.map(row=>{
          const suggestion = proposal.members[row.index];
          return <fieldset className="team-member" key={row.index} disabled={busy}>
            <legend><label><input type="checkbox" aria-label={"创建 "+suggestion.name} checked={row.selected}
              onChange={event=>update(row.index,{selected:event.target.checked})}/> {suggestion.name}</label></legend>
            {row.selected && <>
              <p className="field-hint team-task-summary">{suggestion.taskSummary}</p>
              {(peers.length>0 || row.remoteId) && <label className="field">成员来源
                <select aria-label={suggestion.name+"的成员来源"} value={row.remoteId} onChange={event=>update(row.index,{remoteId:event.target.value})}>
                  <option value="">新建本地 Agent</option>
                  {row.remoteId && !peers.some(peer=>peer.id===row.remoteId) && <option value={row.remoteId} disabled>该外部成员已不可选</option>}
                  {peers.map(peer=><option key={peer.id} value={peer.id} disabled={selected.some(other=>other.index!==row.index && other.remoteId===peer.id)}>
                    @{peer.agentName} · {peer.nodeName}（{peer.online && peer.connected ? "在线" : "离线"}）
                  </option>)}
                </select></label>}
              {row.remoteId ? <label className="field">在本团队的职责<textarea value={row.role} maxLength={200} rows={2} required
                onChange={event=>update(row.index,{role:event.target.value})}/></label> :
                <MemberFields draft={row} name={suggestion.name} engines={engines} capabilities={profiles[row.engine]} catalog={catalogs[row.engine]}
                  update={fields=>update(row.index,fields)}/>}
            </>}
          </fieldset>;
        })}</div>}
        {loading && active && localRows.length>0 && <p className="field-hint" role="status">正在读取本机引擎…</p>}
        {!loading && active && !busy && localRows.length>0 && (error || localRows.some(row=>row.engine && catalogs[row.engine]?.ready===false)) &&
          <button className="text-button" type="button" onClick={()=>{setCatalogs({}); setError("");setChecks(current=>current+1);}}>重新检查引擎</button>}
        {error && <p className="form-error" role="alert">{error}</p>}
      </>}
    </div>
    <footer className="dialog-footer">
      {(active || busy) && !outcome ? <>
        <button type="button" className="button" disabled={busy} onClick={()=>void resolve("reject")}>拒绝提案</button>
        <button type="submit" className="button primary" disabled={!ready || busy}>
          {busy ? "正在处理…" : (localRows.length===selected.length ? "确认并创建已选 Agent" : "确认组队")+" ("+selected.length+")"}
        </button>
      </> : <button type="button" className="button" disabled={busy} onClick={close}>关闭</button>}
    </footer>
  </form>;
}

export function TeamProposalControl() {
  const {snapshot, topic} = useWorkbench();
  const dialog = useRef<HTMLDialogElement>(null);
  const previouslyShown = useRef<string | undefined>(undefined);
  const busy = useRef(false);
  const [opened,setOpened] = useState<{proposal:TeamProposal; project:string}>();
  const local = location.hostname==="127.0.0.1";
  const proposal = snapshot?.teamProposals?.find(item=>item.status==="pending" && item.topicId===topic?.id);
  const active = snapshot?.teamProposals?.some(item=>item.status==="pending" && item.id===opened?.proposal.id) ?? false;

  useEffect(()=>{
    if (!local || !topic || !proposal || opened || previouslyShown.current===proposal.id || isDialogOpen()) return;
    previouslyShown.current = proposal.id;
    setOpened({proposal,project:topic.project});
  },[local, topic, proposal, opened]);
  useEffect(()=>{
    if (opened && !dialog.current?.open) dialog.current?.showModal();
  },[opened]);
  if (!local) return null;

  const close = ()=>dialog.current?.close();
  return <>
    {proposal && topic && <button type="button" className="team-proposal-launcher" aria-label="团队提案" title="查看待确认的团队提案"
      onClick={()=>{previouslyShown.current=proposal.id; setOpened({proposal,project:topic.project});}}>
      <WorkspaceIcon name="flag"/><span>团队提案</span>
    </button>}
    {createPortal(<dialog id="team-proposal-dialog" ref={dialog} aria-labelledby="team-proposal-title" onClose={()=>setOpened(undefined)}
      onCancel={event=>{if (busy.current) event.preventDefault();}}>
      {opened && <TeamProposalForm key={opened.proposal.id} proposal={opened.proposal} project={opened.project} active={active}
        close={close} onBusy={value=>{busy.current=value;}}/>}
    </dialog>,document.body)}
  </>;
}
