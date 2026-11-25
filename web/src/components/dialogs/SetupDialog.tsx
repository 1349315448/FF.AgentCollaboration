export function SetupDialog() {
  return (
    <dialog id="setup-dialog" closedby="none" aria-labelledby="setup-title">
      <form id="setup-form">
        <header className="dialog-header"><h2 id="setup-title">初始化 FF.Agent 工作台</h2></header>
        <div className="dialog-body">
          <p className="setup-intro">选择三个存储目录。点击初始化后将创建数据库并打开工作台。</p>
          <div id="setup-paths"></div>
          <p className="form-error" id="setup-error" role="alert" hidden></p>
        </div>
        <footer className="dialog-footer">
          <button type="button" className="button" id="setup-retry" hidden>重试</button>
          <button type="submit" className="button primary" id="setup-submit">初始化</button>
        </footer>
      </form>
    </dialog>
  );
}
