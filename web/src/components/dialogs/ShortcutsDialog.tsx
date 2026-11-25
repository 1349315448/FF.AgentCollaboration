export function ShortcutsDialog() {
  return (
    <dialog id="shortcuts-dialog" aria-labelledby="shortcuts-title">
      <form id="shortcut-form">
        <header className="dialog-header"><h2 id="shortcuts-title">管理快捷指令</h2><button type="button" className="icon-button" data-close="" aria-label="关闭">×</button></header>
        <div className="dialog-body">
          <label className="field">选择指令<select id="shortcut-choice"></select></label>
          <label className="field">指令文本<input id="shortcut-command" required maxLength={200} autoComplete="off" /></label>
          <label className="field">备注<input id="shortcut-note" maxLength={200} autoComplete="off" /></label>
          <p id="shortcut-error" className="form-error" role="alert" hidden></p>
        </div>
        <footer className="dialog-footer">
          <button type="button" className="text-button" id="shortcuts-reset">恢复默认</button>
          <button type="button" className="button" id="shortcut-delete">删除</button>
          <button type="submit" className="button primary">保存</button>
        </footer>
      </form>
    </dialog>
  );
}
