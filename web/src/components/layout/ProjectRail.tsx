import { useLayoutEffect, useRef } from "react";
import { useWorkbench, type ProjectTotals } from "../../store/workbenchStore";
import { projectKey, navigation } from "../../services/navigation";
import { fitProjectPath } from "../../services/projectPath";

function ProjectCard({path, active, total}: {path: string; active: boolean; total: ProjectTotals}) {
  const label = useRef<HTMLElement>(null);
  const card = useRef<HTMLDivElement>(null);
  useLayoutEffect(() => {
    const el = label.current!;
    const fit = () => fitProjectPath(el);
    fit();
    const resize = new ResizeObserver(fit);
    resize.observe(el);
    return () => resize.disconnect();
  }, [path]);
  useLayoutEffect(() => { if (active) card.current?.scrollIntoView({block:"nearest",inline:"nearest"}); }, [active]);
  const summary = "会话 " + total.topics + " · Agent " + total.agents;
  const description = path + " · " + summary + " · 工作 " + total.working + " · 空闲 " + total.idle + " · 离线 " + total.offline + " · " + total.unread + " 条待处理";
  return <div ref={card} className="project-button" id={active ? "current-project" : undefined} title={path}
    data-key={projectKey(path)} data-activity={total.activity} data-selected={active}
    onClick={() => navigation.selectProject(path)}>
    <div className="project-card-title">
      <button className="project-select" data-activity={total.activity} type="button" aria-pressed={active} aria-label={description}>
        <span className="nav-dot" aria-hidden="true" />
        <strong ref={label} data-path={path} title={path} id={active ? "project-name" : undefined} />
        <span className="tag-inbox" hidden={!total.unread}>{total.unread}</span>
      </button>
      <button type="button" className="text-button remove-project-btn" id={active ? "remove-project" : undefined}
        title={"从列表移除 " + path} aria-label={active ? "移除当前项目" : "移除项目"}
        onClick={e => { e.stopPropagation(); navigation.removeProject(path); }}>移除</button>
    </div>
    <span className="project-summary">{summary}</span>
    <span className="project-states">
      <span>工作 <b>{total.working}</b></span><span>空闲 <b>{total.idle}</b></span><span>离线 <b>{total.offline}</b></span>
    </span>
  </div>;
}
export function ProjectRail() {
  const { snapshot, totals } = useWorkbench();
  return <aside id="project-rail" aria-label="项目">
    <div className="project-tools"><strong>项目</strong><button className="icon-button" id="add-project" type="button" title="打开项目文件夹" aria-label="打开项目文件夹" disabled={!snapshot}>＋</button></div>
    <nav id="project-tabs" aria-label="已打开的项目">{snapshot?.projects.map(path => {
      const key = projectKey(path);
      return <ProjectCard key={key} path={path} active={!!snapshot.project && key === projectKey(snapshot.project)} total={totals.get(key)!} />;
    })}</nav>
  </aside>;
}
