export function PaneSessionDialog() {
  return (
    <dialog id="pane-session-dialog" closedby="any" aria-labelledby="pane-session-title">
      <header className="dialog-header">
        <div><h2 id="pane-session-title">会话记录</h2><small id="pane-session-context"></small></div>
        <button type="button" className="icon-button" data-close="" aria-label="关闭会话记录">×</button>
      </header>
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
      <footer className="dialog-footer"><small>新建或切换会话会重开当前终端</small><button className="button primary" id="pane-session-new" type="button">＋ 新建会话</button></footer>
    </dialog>
  );
}
