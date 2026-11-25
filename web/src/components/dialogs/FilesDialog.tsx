export function FilesDialog() {
  return (
    <dialog id="files-dialog" aria-labelledby="files-title">
      <header className="dialog-header"><h2 id="files-title">选择项目文件</h2><button type="button" className="icon-button" data-close="" aria-label="关闭">×</button></header>
      <div className="dialog-body">
        <input id="file-query" aria-label="搜索文件" placeholder="搜索文件名或路径" autoComplete="off" ref={input => { if (input) input.autofocus = true; }} />
        <p className="form-error" id="file-error" role="alert" hidden></p>
        <div className="selection-list file-list" id="file-options" aria-label="匹配的项目文件"></div>
        <p className="selection-status" id="file-status" role="status"></p>
      </div>
      <footer className="dialog-footer"><button className="button" type="button" data-close="">取消</button><button className="button primary" type="button" id="insert-files" disabled>插入路径</button></footer>
    </dialog>
  );
}
