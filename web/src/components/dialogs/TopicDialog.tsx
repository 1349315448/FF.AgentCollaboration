export function TopicDialog() {
  return (
    <dialog id="topic-dialog" aria-labelledby="topic-title">
      <form id="topic-form">
        <header className="dialog-header"><h2 id="topic-title">新建话题</h2><button type="button" className="icon-button" data-close="" aria-label="关闭">×</button></header>
        <div className="dialog-body">
          <label className="field">话题名称<input id="topic-name" required maxLength={80} pattern={'[^\\/@]+'} ref={input => { if (input) input.autofocus = true; }} /></label>
          <div className="field">项目文件夹<button type="button" className="select-project" id="topic-project"></button></div>
          <label className="field">目标（选填）<input id="topic-goal" /></label>
          <label className="field">任务归档目录<select id="topic-folder"><option value="">自动创建话题目录</option></select></label>
          <p id="topic-error" className="form-error" role="alert" hidden></p>
        </div>
        <footer className="dialog-footer"><button type="button" className="button" data-close="">取消</button>
          <button type="submit" className="button primary" id="topic-submit">创建话题</button></footer>
      </form>
    </dialog>
  );
}
