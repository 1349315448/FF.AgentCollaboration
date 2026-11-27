import { useEffect, useState } from "react";
import { engineNames } from "../../services/engines";
import { TopicLeaderButton, TopicLeaderDialog } from "./TopicLeaderDialog";

export function AgentConfigDialog() {
  const [agentId, setAgentId] = useState<string>();
  useEffect(() => {
    const opened = (event: Event) => setAgentId((event as CustomEvent<string>).detail);
    window.addEventListener("ff:agent-config", opened);
    return () => window.removeEventListener("ff:agent-config", opened);
  }, []);
  return (
    <><dialog id="pane-session-dialog" closedby="any" aria-labelledby="pane-session-title" onClose={() => setAgentId(undefined)}>
      <header className="dialog-header">
        <div><h2 id="pane-session-title">会话记录</h2><small id="pane-session-context"></small></div>
        <button type="button" className="icon-button" data-close="" aria-label="关闭会话记录">×</button>
      </header>
      <div className="new-agent-tabs" role="tablist" aria-label="Agent 设置">
        <button role="tab" aria-controls="agent-config-basic" data-config-tab="basic">基本设置</button>
        <button role="tab" aria-controls="agent-config-history" data-config-tab="history">会话管理</button>
      </div>
      <form id="agent-config-basic" role="tabpanel" hidden>
        <div className="dialog-body">
          <label className="field">Agent 名称<input id="rename-name" required maxLength={40} placeholder="例如：后端GPT"/></label>
          <label className="field">身份提示词 / 职责描述（选填）<textarea id="config-role" rows={3} maxLength={200} placeholder="选填，例如：负责后端接口与数据库迁移；留空则不设固定身份"/></label>
          {agentId && <TopicLeaderButton agentId={agentId} inSettings />}
          <label className="field">引擎<select id="config-engine">
            {Object.entries(engineNames).map(([id,name])=><option key={id} value={id}>{name}</option>)}
          </select></label>
          <div id="config-launch-fields" hidden>
            <div className="field-pair" id="config-model-fields">
              <label className="field">启动模型<select id="config-model"/></label>
              <label className="field">推理档位<select id="config-effort"/></label>
            </div>
            <label className="field">运行模式<select id="config-mode"/></label>
            <label className="field">审批权限<select id="config-approval"><option value="">跟随引擎默认</option><option value="ask">需要审批</option><option value="auto">自动审批</option></select></label>
            <small id="config-catalog-status" role="status"/>
          </div>
          <p id="rename-error" className="form-error" role="alert" hidden/>
        </div>
        <footer className="dialog-footer"><small id="config-effect"/><button className="button primary" type="submit" id="config-save">保存</button></footer>
      </form>
      <div id="agent-config-history" role="tabpanel">
      <div className="session-tools">
        <div className="view-modes" role="group" aria-label="会话列表">
          <button type="button" className="text-button" data-history="recent" aria-pressed="true">最近会话</button>
          <button type="button" className="text-button" data-history="archived" aria-pressed="false">已归档</button>
        </div>
        <button type="button" className="text-button" id="pane-session-reload">刷新</button>
      </div>
      <div className="session-scroll">
        <p id="pane-session-status" role="status"></p>
        <ul id="pane-history" aria-label="会话记录"></ul>
        <p id="pane-session-error" className="form-error" role="alert" hidden></p>
      </div>
      <section id="session-delete-confirm" aria-label="确认永久删除" hidden>
        <strong>永久删除这条会话？</strong><p id="session-delete-name"></p>
        <p>原生 CLI 中的这条记录也会删除，无法恢复。</p>
        <div><button className="button" type="button" id="cancel-session-delete">取消</button>
          <button className="button delete-confirm" type="button" id="confirm-session-delete">永久删除</button></div>
      </section>
      <footer className="dialog-footer"><small id="config-session-effect"/><button className="button primary" id="pane-session-new" type="button">＋ 新建会话</button></footer>
      </div>
    </dialog><TopicLeaderDialog /></>
  );
}
