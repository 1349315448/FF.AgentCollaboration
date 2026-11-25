export function RenameDialog() {
  return (
    <dialog id="rename-dialog" aria-labelledby="rename-title">
      <form id="rename-form">
        <header className="dialog-header"><h2 id="rename-title">修改 Agent 名称</h2></header>
        <div className="dialog-body">
          <label className="field">Agent 名称<input id="rename-name" type="text" required maxLength={40} ref={input => { if (input) input.autofocus = true; }} /></label>
          <p id="rename-error" className="form-error" role="alert" hidden></p>
        </div>
        <footer className="dialog-footer"><button className="button" type="button" data-close="">取消</button><button className="button primary" type="submit">保存</button></footer>
      </form>
    </dialog>
  );
}
