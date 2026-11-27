import { request } from "../../services/api";
import { pageRetired } from "../../services/workbenchPage";
import { useSyncExternalStore } from "react";

export interface FileView { path:string; kind:"text"|"image"|"video"|"binary"|"large"|"ppt"|"excel"|"pdf"|"legacy-ppt"; size:number; content?:string; mtimeUtc:string }
export interface FileTab { path:string; file?:FileView; draft?:string; loading?:boolean; saving?:boolean; conflict?:boolean; error?:string }
export interface EditorState { tabs:FileTab[]; active:string }
const sessions = new Map<string, EditorSession>();
export function workspaceUrl(route:string, params:Record<string,string>) {
  return "/workspace/" + route + "?" + new URLSearchParams(params);
}
export function fileUrl(project:string, path:string) { return workspaceUrl("file", {project,path}); }
export function dirty(tab:FileTab) { return tab.draft !== undefined && tab.draft !== tab.file?.content; }

// 保存期间切换话题时，结果依然归原项目；只有仍在页签栏里的同一份文件接受响应。
class EditorSession {
  state:EditorState = {tabs:[], active:""};
  private listeners = new Set<() => void>();
  private loads = new Map<string, object>();
  constructor(readonly project:string) {}
  subscribe = (fn:() => void) => { this.listeners.add(fn); return () => { this.listeners.delete(fn); }; };
  read = () => this.state;
  private set(state:EditorState) { this.state=state; this.listeners.forEach(fn => fn()); }
  update(path:string, fields:Partial<FileTab>) {
    this.set({...this.state, tabs:this.state.tabs.map(t => t.path === path ? {...t,...fields} : t)});
  }
  select(path:string) { this.set({...this.state,active:path}); }
  async open(path:string) {
    if (this.state.tabs.some(t => t.path === path)) { this.select(path); return; }
    if (this.state.tabs.length === 8) throw new Error("已打开 8 个文件，请先关闭一个标签");
    this.set({tabs:[...this.state.tabs,{path}], active:path});
    await this.load(path);
  }
  async load(path:string) {
    const key = {};
    this.loads.set(path,key);
    this.update(path,{error:undefined,loading:true});
    try {
      const file = await request<FileView>(fileUrl(this.project,path));
      if (this.loads.get(path) === key) this.update(path,{file,draft:undefined,conflict:false,loading:false});
    } catch (e) { if (this.loads.get(path) === key) this.update(path,{error:(e as Error).message,loading:false}); }
  }
  close(path:string) {
    const index = this.state.tabs.findIndex(t => t.path === path);
    if (index < 0) return;
    const item = this.state.tabs[index];
    if (item.saving || (dirty(item) && !window.confirm("关闭标签并放弃未保存的修改？"))) return;
    this.loads.delete(path);
    const tabs = this.state.tabs.filter(t => t.path !== path);
    this.set({tabs,active:this.state.active === path ? tabs[Math.min(index,tabs.length-1)]?.path ?? "" : this.state.active});
  }
  async save(tab:FileTab) {
    if (!dirty(tab) || !tab.file || tab.loading || tab.saving || tab.conflict) return;
    this.update(tab.path,{saving:true,error:undefined});
    try {
      const file = await request<FileView>("/workspace/file", {method:"PUT",body:{
        project:this.project,path:tab.path,content:tab.draft,expectedMtimeUtc:tab.file.mtimeUtc
      }});
      this.update(tab.path,{file,draft:undefined,saving:false,conflict:false});
    } catch (e) { this.update(tab.path,{error:(e as Error).message,saving:false,conflict:(e as {status?:number}).status === 409}); }
  }
}
function editor(project:string) {
  let entry = sessions.get(project);
  if (!entry) { entry = new EditorSession(project); sessions.set(project,entry); }
  return entry;
}
export function useEditor(project:string) {
  const session = editor(project);
  return {session,...useSyncExternalStore(session.subscribe,session.read)};
}
window.addEventListener("ff:recover-drafts", event => {
  const recovery = (event as CustomEvent<Document>).detail;
  for (const session of sessions.values()) {
    for (const tab of session.state.tabs.filter(dirty)) {
      const title = recovery.createElement("h2");
      title.textContent = session.project + " / " + tab.path;
      const content = recovery.createElement("pre");
      content.textContent = tab.draft!;
      recovery.body.append(title, content);
    }
  }
});
window.addEventListener("beforeunload", event => {
  if (!pageRetired && [...sessions.values()].some(s => s.state.tabs.some(dirty))) { event.preventDefault(); event.returnValue=""; }
});
