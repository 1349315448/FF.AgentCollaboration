import { useEffect, useRef, useState } from "react";
import { knowledgeModeFor, knowledgeModes, type KnowledgeMode } from "../../services/knowledge";
import { useWorkbench } from "../../store/workbenchStore";
import { request } from "../../services/api";

export function KnowledgeModeOptions() {
  return <>{Object.entries(knowledgeModes).map(([value, title]) => <option key={value} value={value}>{title}</option>)}</>;
}

export function ProjectKnowledgeDialog({project, onClose}:{project:string; onClose:()=>void}) {
  const {snapshot} = useWorkbench();
  const dialog = useRef<HTMLDialogElement>(null);
  const current = knowledgeModeFor(snapshot?.knowledgeModes, project);
  const [mode, setMode] = useState(current);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  useEffect(() => { dialog.current?.showModal(); }, []);

  async function save() {
    setBusy(true);
    setError("");
    try {
      await request("/knowledge/project", {method:"PUT", body:{project, mode}});
      const runtime = await import("../../runtime/workbench.js");
      await runtime.refreshing;
      await runtime.refresh();
      dialog.current?.close();
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  }
  return <dialog ref={dialog} aria-labelledby="project-knowledge-title" onClose={onClose} onCancel={event=>{if (busy) event.preventDefault();}}>
    <form onSubmit={event=>{event.preventDefault(); void save();}}>
      <header className="dialog-header">
        <h2 id="project-knowledge-title">项目设置</h2>
        <button type="button" className="icon-button" aria-label="关闭" disabled={busy} onClick={()=>dialog.current?.close()}>×</button>
      </header>
      <div className="dialog-body">
        <p className="file-caption" title={project}>{project}</p>
        <label className="field">项目知识库（.ff-acp-knowledge）
          <select value={mode} onChange={event=>setMode(event.target.value as KnowledgeMode)} disabled={busy}><KnowledgeModeOptions/></select>
        </label>
        {error && <p className="form-error" role="alert">{error}</p>}
      </div>
      <footer className="dialog-footer">
        <button type="button" className="button" onClick={()=>dialog.current?.close()} disabled={busy}>取消</button>
        <button type="submit" className="button primary" disabled={busy || mode===current}>{busy?"保存中…":"保存"}</button>
      </footer>
    </form>
  </dialog>;
}
