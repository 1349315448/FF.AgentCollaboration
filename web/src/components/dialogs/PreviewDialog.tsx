export function PreviewDialog() {
  return (
    <dialog id="preview-dialog" aria-labelledby="preview-title">
      <header className="dialog-header"><h2 id="preview-title">收件预览</h2><button type="button" className="icon-button" data-close="" aria-label="关闭">×</button></header>
      <div className="dialog-body"><pre id="preview-content"></pre></div>
      <footer className="dialog-footer"><small id="preview-status" role="status"></small><button className="button" type="button" id="dismiss-preview" hidden>撤回 / 忽略此消息</button><button className="button" type="button" id="copy-preview">复制消息</button><button className="button" type="button" data-close="">关闭</button></footer>
    </dialog>
  );
}
