import { isDialogOpen } from "../store/workbenchStore";
import { request } from "../services/api";
import { selectedEngine } from "./create.js";
import { byId, node } from "./dom.js";
import { panes } from "../services/terminalRegistry";
import { refresh } from "./workbench.js";

const quotas = new Map();

function loadQuota(engine, refresh = false) {
  let entry = quotas.get(engine);
  if (!entry?.loading && (refresh || !entry || Date.now() - entry.loadedAt >= 300_000)) {
    entry = { loading: true };
    quotas.set(engine, entry);
    request("/quota/" + engine + (refresh ? "?refresh=1" : "")).then(quota => {
      entry.quota = quota;
    }, error => {
      entry.error = error.message;
    }).finally(() => {
      entry.loading = false;
      entry.loadedAt = Date.now();
      renderQuota(engine);
    });
  }
  renderQuota(engine);
}

const quotaTime = new Intl.DateTimeFormat("zh-CN", { year: "numeric", month: "numeric", day: "numeric", hour: "2-digit", minute: "2-digit", hourCycle: "h23" });
function quotaResetLabel(seconds) {
  if (!seconds) return "";
  const reset = new Date(seconds * 1000);
  const two = number => String(number).padStart(2, "0");
  return `${reset.getFullYear()}-${two(reset.getMonth() + 1)}-${two(reset.getDate())} ${two(reset.getHours())}:${two(reset.getMinutes())} 刷新`;
}

function quotaDetails({ quota, loading, error }, engine) {
  if (loading) return ["正在读取额度…"];
  if (!quota?.supported) return [error || quota?.message || "暂时无法读取额度"];
  if (quota.unlimited) return [quota.message];
  const limits = [quota.primary, quota.secondary].filter(Boolean).map(window => {
    const minutes = window.windowDurationMins;
    const interval = engine === "copilot" ? "高级交互" : minutes == null ? "账号" : minutes % 1440 === 0 ? minutes / 1440 + " 天"
      : minutes % 60 === 0 ? minutes / 60 + " 小时" : minutes + " 分钟";
    const reset = window.resetsAt ? " · " + quotaTime.format(new Date(window.resetsAt * 1000)) + " 重置" : "";
    return interval + "额度剩余 " + window.remainingPercent + "%" + reset;
  });
  const credits = creditBalance(quota);
  if (credits != null) limits.push("剩余 credits：" + credits);
  return limits;
}

const wholeCredits = new Intl.NumberFormat("en-US", {maximumFractionDigits:0, roundingMode:"trunc", useGrouping:false});

function creditBalance(quota) {
  const credits = quota?.credits;
  if (credits?.unlimited) return "∞";
  return credits?.balance == null ? null : wholeCredits.format(credits.balance);
}

function renderQuota(engine) {
  const entry = quotas.get(engine);
  const details = quotaDetails(entry, engine);
  const limit = entry.quota?.primary ?? entry.quota?.secondary;
  const reset = quotaResetLabel(limit?.resetsAt);
  const percent = limit ? limit.remainingPercent + "%" : entry.quota?.unlimited ? "∞" : "";
  const credits = creditBalance(entry.quota);
  const value = [percent, credits != null ? credits + " credits" : ""].filter(Boolean).join(" · ") || (entry.loading ? "…" : "—");
  const checked = entry.quota?.checkedAt;
  const title = [...details, checked && "查询时间 " + quotaTime.format(new Date(checked)), "点击刷新"].filter(Boolean).join("\n");
  for (const pane of panes.values()) {
    if (pane.agent.engine !== engine) continue;
    const button = pane.element.querySelector(".pane-quota");
    button.disabled = entry.loading;
    button.querySelector(".quota-value").textContent = value;
    const date = button.querySelector(".quota-reset");
    date.textContent = reset;
    date.hidden = !reset;
    button.title = title;
    button.setAttribute("aria-label", [value, reset, "点击刷新额度"].filter(Boolean).join("，"));
  }
  if (isDialogOpen("new-dialog") && selectedEngine() === engine) {
    byId("engine-quota").replaceChildren(...details.map(text => node("span", "", text)));
    byId("refresh-quota").disabled = entry.loading;
  }
}

export { loadQuota, quotas };
