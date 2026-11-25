export function FolderDialog() {
  return (
    <dialog id="folder-dialog" aria-labelledby="folder-title">
      <header className="dialog-header"><h2 id="folder-title">选择项目文件夹</h2><button type="button" className="icon-button" data-close="" aria-label="关闭">×</button></header>
      <div className="dialog-body">
        <form id="folder-path-form" className="folder-address"><input id="folder-path" type="text" aria-label="文件夹路径" placeholder="输入本机文件夹路径" /><button type="submit" className="button">打开</button></form>
        <div className="folder-navigation"><button className="text-button" id="folder-disks" type="button">磁盘</button><button className="text-button" id="folder-up" type="button">↑ 上一级</button></div>
        <p className="form-error" id="folder-error" role="alert" hidden></p>
        <div className="selection-list" id="folder-list" aria-label="子文件夹"></div>
      </div>
      <footer className="dialog-footer"><button className="button" type="button" data-close="">取消</button><button className="button primary" type="button" id="choose-folder" disabled>选用此文件夹</button></footer>
    </dialog>
  );
}
