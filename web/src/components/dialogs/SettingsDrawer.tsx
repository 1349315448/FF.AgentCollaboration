import { useState } from "react";
import { NodePreferences } from "../federation/Federation";
import { SignalingPreferences } from "../federation/SignalingPreferences";
import { LanAccessSettings } from "./LanAccessSettings";

const categories = [
  ["general","常规"],["connection","协作连接"],["engines","CLI 引擎"],["shortcuts","快捷键"]
] as const;

export function SettingsDrawer() {
  const [category,select] = useState<(typeof categories)[number][0]>("general");
  return (
    <dialog id="settings-dialog" closedby="any" aria-labelledby="settings-title">
      <header className="dialog-header"><h2 id="settings-title">工作台设置</h2><button type="button" className="icon-button" id="close-settings" aria-label="关闭配置">×</button></header>
      <div className="settings-layout">
        <nav className="settings-nav" aria-label="设置分类">
          {categories.map(([id,label])=><button key={id} id={"settings-category-"+id} type="button" className="text-button"
            aria-pressed={category===id} aria-controls={"settings-section-"+id} onClick={()=>select(id)}>{label}</button>)}
        </nav>
        <div className="settings-scroll">
          <div id="settings-section-general" hidden={category!=="general"}>
            <section><h3>数据存储与交接目录</h3><div id="settings-paths"></div><p id="settings-database"></p>
              <button type="button" className="button primary" id="save-paths">保存目录配置</button>
            </section>
            <section><h3>协作日志清理</h3>
              <small id="log-count"></small>
              <div id="log-cleanup">
                <button className="button" type="button" data-days="30">清理 30 天前</button>
                <button className="button" type="button" data-days="15">清理 15 天前</button>
                <button className="button" type="button" data-days="7">清理 7 天前</button>
                <button className="button danger" type="button" data-days="0">全部清理</button>
              </div>
              <small id="cleanup-result" role="status"></small>
            </section>
          </div>
          <div id="settings-section-connection" hidden={category!=="connection"}>
            <NodePreferences />
            {location.hostname==="127.0.0.1" && <SignalingPreferences/>}
            {location.hostname==="127.0.0.1" && <LanAccessSettings/>}
          </div>
          <div id="settings-section-engines" hidden={category!=="engines"}>
            <section><h3>CLI 引擎</h3><div id="settings-engines"></div></section>
            <section><h3>运行环境</h3><div id="settings-environment"></div></section>
          </div>
          <section id="settings-section-shortcuts" hidden={category!=="shortcuts"}><h3>快捷键</h3>
          <div className="mention-setting"><label htmlFor="mention-record">提及 Agent</label>
            <button type="button" id="mention-record" className="button" title="点击后按下新的组合键">Alt+Shift+A</button>
            <button type="button" id="mention-save" className="button" disabled>保存</button>
            <button type="button" id="mention-reset" className="text-button">恢复默认</button></div>
          <small id="mention-key-feedback" role="status"></small>
          </section>
        </div>
      </div>
      <small id="settings-feedback" role="status"></small>
    </dialog>
  );
}
