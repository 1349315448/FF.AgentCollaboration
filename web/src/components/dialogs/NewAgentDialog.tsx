import { useRef, useState } from "react";
import { request } from "../../services/api";
import { engineNames, installGuides, traeAccountNotice } from "../../services/engines";
import {ReturnAgentSelect} from "../federation/ReturnAgentSelect";

export function NewAgentDialog() {
  const dialogRef=useRef<HTMLDialogElement>(null);
  const generation=useRef(0);
  const [external,choose]=useState(false), [busy,setBusy]=useState(false), [error,setError]=useState("");
  return (
    <><dialog ref={dialogRef} id="new-dialog" aria-labelledby="new-title" onClose={()=>{generation.current++;choose(false);setError("");setBusy(false);}}>
        <header className="dialog-header"><h2 id="new-title">新建 Agent 窗口</h2><button type="button" className="icon-button" data-close="" aria-label="关闭">×</button></header>
      <div className="new-agent-tabs" role="tablist" aria-label="Agent 来源">
        {["本地 Agent","外部协作 Agent"].map((label,i)=><button key={label} type="button" role="tab" disabled={busy} aria-selected={external===Boolean(i)} onClick={()=>choose(Boolean(i))}>{label}</button>)}
      </div>
      <form id="new-form" hidden={external}>
        <div className="dialog-body">
          <label className="field"><span><span className="required">*必填</span> 窗口别名</span>
            <input id="agent-name" type="text" maxLength={40} placeholder="例如 后端GPT" required ref={input => { if (input) input.autofocus = true; }} />
          </label>
          <label className="field">身份 / 职责描述（选填）<input id="agent-role" maxLength={200} placeholder="例如：负责接口开发与联调" /></label>
          <div className="field">
            <span id="new-project-label"><span className="required">*必填</span> 项目文件夹</span>
            <button className="select-project" id="agent-project" type="button" aria-labelledby="new-project-label agent-project-name">
              <span className="folder-icon" aria-hidden="true"></span>
              <span className="selected-folder"><strong id="agent-project-name"></strong><span id="agent-project-path"></span></span>
              <span className="change-label">更改</span>
            </button>
          </div>
          <div className="field">
            <span id="engine-label"><span className="required">*必填</span> 引擎</span>
            <div role="radiogroup" aria-labelledby="engine-label" id="agent-engine">
              {Object.entries(engineNames).map(([id, label]) => <div className="engine-row" key={id}>
                <label><input type="radio" name="engine" value={id} defaultChecked={id === "codex"} /> {label}</label>
                <button type="button" className="text-button" data-install={id} aria-label={(installGuides[id] ? "安装指南 " : "安装 ") + label} hidden>安装</button>
              </div>)}
            </div>
          </div>
          <div className="field"><label htmlFor="session-source"><span className="required">*必填</span> 会话</label>
            <select id="session-source" disabled><option value="" disabled>请选择会话</option><option value="new">新建</option><option value="latest" hidden disabled>恢复最近会话</option><option value="history">恢复已有会话</option></select>
            <button className="button" type="button" id="open-engine-login" hidden>打开登录终端</button>
          </div>
          <p id="trae-create-notice" className="field-hint" hidden>{traeAccountNotice}</p>
          <label className="field" id="history-field" hidden><span><span className="required">*必填</span> 历史会话</span><select id="history-session" required disabled></select></label>
          <fieldset id="history-action" hidden><legend>打开方式</legend>
            <label><input type="radio" name="historyAction" value="resume" defaultChecked /> 使用原会话</label>
            <label><input type="radio" name="historyAction" value="fork" /> 创建分支</label>
          </fieldset>
          <div className="field-pair">
            <label className="field"><span>初始模型</span><select id="agent-model"></select></label>
            <label className="field"><span>推理档位</span><select id="agent-effort"></select></label>
          </div>
          <label className="field"><span id="mode-label">初始模式</span><select id="agent-mode"></select></label>
          <label className="field" id="copilot-approval-field" hidden><span>审批权限</span><select id="copilot-approval"><option value="">跟随引擎默认</option><option value="ask">需要审批</option><option value="auto">自动审批（允许所有工具）</option></select></label>
          <div className="quota-row"><div className="quota-info" id="engine-quota" role="status"></div><button className="text-button" id="refresh-quota" type="button">刷新额度</button></div>
          <div className="model-feedback"><span id="catalog-status" role="status"></span><button id="reload-catalog" className="text-button" type="button" hidden>重新读取</button></div>
          <p id="create-error" className="form-error" role="alert" hidden></p>
        </div>
        <footer className="dialog-footer"><button className="button" type="button" data-close="">取消</button><button className="button primary" type="submit" id="create-agent">打开终端</button></footer>
      </form>
      {external && <form className="dialog-body fed-fields" onSubmit={async e=>{
        e.preventDefault(); if(busy)return; const dialog=e.currentTarget.closest("dialog")!, form=new FormData(e.currentTarget);
        const attempt=generation.current;setBusy(true);setError("");
        try {await request("/peers/remotes",{method:"POST",body:{topicId:dialog.dataset.topic,link:form.get("link"),purpose:form.get("purpose"),agentId:form.get("contactAgent")||null}});if(attempt===generation.current)dialog.close();}
        catch(err){if(attempt===generation.current)setError((err as Error).message);} finally{if(attempt===generation.current)setBusy(false);}
      }}>
        <label>分享链接<input name="link" required placeholder="ffcp://" autoComplete="off" spellCheck={false}/></label>
        <label>申请用途<input name="purpose" required maxLength={300}/></label>
        <ReturnAgentSelect topicId={dialogRef.current?.dataset.topic}/>
        {error && <p role="alert">{error}</p>}<button disabled={busy} className="button primary">发送申请</button>
      </form>}
    </dialog>
    <dialog id="engine-login-dialog" aria-labelledby="engine-login-title">
      <header className="dialog-header"><h2 id="engine-login-title">引擎登录</h2><button type="button" className="icon-button" data-close="" aria-label="关闭">×</button></header>
      <div id="zcode-login-providers" className="dialog-body" hidden><button type="button" className="button" value="bigmodel">智谱 BigModel 登录</button> <button type="button" className="button" value="zai">Z.AI 登录</button></div>
      <p id="trae-login-notice" className="field-hint" hidden>{traeAccountNotice}</p>
      <div id="engine-login-terminal" />
      <footer className="dialog-footer"><span id="engine-login-status" role="status"></span><button type="button" className="button primary" id="verify-engine-login">检查登录状态</button></footer>
    </dialog>
    <dialog id="approval-dialog" aria-labelledby="approval-title">
      <form method="dialog">
        <header className="dialog-header"><h2 id="approval-title">切换审批</h2><button className="icon-button" value="cancel" aria-label="关闭">×</button></header>
        <div className="dialog-body">
          <label className="field"><span>审批权限</span><select id="approval-choice"><option value="ask">需要审批</option><option value="auto">自动审批</option></select></label>
          <p className="field-hint" id="approval-detail"></p>
          <p className="field-hint" id="approval-restart-note"></p>
        </div>
        <footer className="dialog-footer"><button className="button" value="cancel">取消</button><button className="button primary" value="apply" id="apply-approval">重启并应用</button></footer>
      </form>
    </dialog></>
  );
}
