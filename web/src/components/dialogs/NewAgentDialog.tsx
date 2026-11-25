import { engineNames } from "../../services/engines";

export function NewAgentDialog() {
  return (
    <dialog id="new-dialog" aria-labelledby="new-title">
      <form id="new-form">
        <header className="dialog-header"><h2 id="new-title">新建 Agent 窗口</h2><button type="button" className="icon-button" data-close="" aria-label="关闭">×</button></header>
        <div className="dialog-body">
          <label className="field"><span><span className="required">*必填</span> 窗口别名</span>
            <input id="agent-name" type="text" maxLength={40} placeholder="例如 后端GPT" required ref={input => { if (input) input.autofocus = true; }} />
          </label>
          <div className="field">
            <span id="new-project-label"><span className="required">*必填</span> 项目文件夹</span>
            <button className="select-project" id="agent-project" type="button" aria-labelledby="new-project-label agent-project-name">
              <span className="folder-icon" aria-hidden="true"></span>
              <span className="selected-folder"><strong id="agent-project-name"></strong><span id="agent-project-path"></span></span>
              <span className="change-label">更改</span>
            </button>
          </div>
          <div className="field-pair">
            <div className="field" role="radiogroup" aria-labelledby="engine-label" id="agent-engine">
              <span id="engine-label"><span className="required">*必填</span> 引擎</span>
              {Object.entries(engineNames).map(([id, label]) => <div className="engine-row" key={id}>
                <label><input type="radio" name="engine" value={id} defaultChecked={id === "codex"} /> {label}</label>
                <button type="button" className="text-button" data-install={id} aria-label={"安装 " + label} hidden>安装</button>
              </div>)}
            </div>
            <label className="field"><span><span className="required">*必填</span> 会话</span><select id="session-source"><option value="new">新建</option><option value="latest" hidden disabled>恢复最近会话</option><option value="history">恢复已有会话</option></select></label>
          </div>
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
          <div className="quota-row"><div className="quota-info" id="engine-quota" role="status"></div><button className="text-button" id="refresh-quota" type="button">刷新额度</button></div>
          <div className="model-feedback"><span id="catalog-status" role="status"></span><button id="reload-catalog" className="text-button" type="button" hidden>重新读取</button></div>
          <p id="create-error" className="form-error" role="alert" hidden></p>
        </div>
        <footer className="dialog-footer"><button className="button" type="button" data-close="">取消</button><button className="button primary" type="submit" id="create-agent">打开终端</button></footer>
      </form>
    </dialog>
  );
}
