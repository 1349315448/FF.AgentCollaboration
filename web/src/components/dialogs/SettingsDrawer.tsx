import { NodePreferences } from "../federation/Federation";
import { SignalingPreferences } from "../federation/SignalingPreferences";
import { LanAccessSettings } from "./LanAccessSettings";
export function SettingsDrawer() {
  return (
    <dialog id="settings-dialog" closedby="any" aria-labelledby="settings-title">
      <header className="dialog-header"><div><h2 id="settings-title">工作台配置</h2><small id="settings-context">数据存储、CLI 引擎与运行环境</small></div><button type="button" className="icon-button" id="close-settings" aria-label="关闭配置">×</button></header>
      <div className="settings-scroll">
        <NodePreferences />
        {location.hostname==="127.0.0.1" && <SignalingPreferences/>}
        {location.hostname==="127.0.0.1" && <LanAccessSettings/>}
        <section><h3>数据存储与交接目录</h3><div id="settings-paths"></div><p id="settings-database"></p></section>
        <section><h3>快捷键</h3>
          <div className="mention-setting"><label htmlFor="mention-record">提及 Agent</label>
            <button type="button" id="mention-record" className="button" title="点击后按下新的组合键">Alt+Shift+A</button>
            <button type="button" id="mention-save" className="button" disabled>保存</button>
            <button type="button" id="mention-reset" className="text-button">恢复默认</button></div>
          <small id="mention-key-feedback" role="status"></small>
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
        <section><h3>CLI 引擎</h3><div id="settings-engines"></div></section>
        <section><h3>运行环境</h3><div id="settings-environment"></div></section>
      </div>
      <footer className="dialog-footer"><small id="settings-feedback" role="status"></small><button type="button" className="button primary" id="save-paths">保存目录配置</button></footer>
    </dialog>
  );
}
