import { TopicNavigation } from "./TopicNavigation";

export function TopBar() {
  return (
    <header className="topbar">
      <div className="brand"><button type="button" className="icon-button" id="project-collapse" aria-controls="project-rail" aria-expanded="true" aria-label="收起项目栏"><span className="rail-icon" aria-hidden="true"></span></button><span title="FF Collaboration Platform">FF.CP</span><button type="button" className="icon-button" id="open-settings" title="工作台配置与环境检测" aria-label="工作台配置与环境检测">⚙</button></div>
      <TopicNavigation />
      <div className="top-actions">
        <span className="connection" id="connection" role="status" title="连接中" aria-label="连接中">连接中</span>
      </div>
    </header>
  );
}
