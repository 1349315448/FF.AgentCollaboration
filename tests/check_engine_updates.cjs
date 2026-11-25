const assert = require("node:assert/strict");
const { chromium } = require("playwright");

async function run() {
  const [base, latest] = process.argv.slice(2);
  const browser = await chromium.launch({ executablePath: process.env.CHROME_PATH || undefined });
  try {
    const page = await browser.newPage({ viewport: { width: 1000, height: 760 } });
    await page.route("**/api/engines", route => route.fulfill({ json: { codebuddy: true, zcode: true, qwen: false } }));
    await page.route("**/api/engines/zcode/release*", route => route.fulfill({ json: {
      installed: { installed: true, version: "0.17.0" }, latestVersion: "0.18.0", status: "available", canUpdate: false
    } }));
    await page.route("**/api/engines/qwen/release*", route => route.fulfill({ json: {
      installed: { installed: false }, latestVersion: null, status: "unavailable", canUpdate: false
    } }));
    await page.goto(base);
    await page.locator("#open-settings").click();
    const rows = page.locator("#settings-engines .health-row");
    const row = rows.filter({ hasText: "CodeBuddy" });
    await row.getByText("最新 " + latest + " · 有更新").waitFor();
    await row.getByRole("button", { name: "更新", exact: true }).click();
    await row.getByText("最新 " + latest + " · 已是最新").waitFor();
    assert.equal(await row.getByRole("button", { name: "更新", exact: true }).count(), 0);
    const forced = page.waitForRequest(req => req.url().endsWith("/codebuddy/release?refresh=true"));
    await row.getByRole("button", { name: "重新检测" }).click();
    await forced;
    await rows.filter({ hasText: "智谱" }).getByRole("button", { name: "更新指南" }).waitFor();
    const qwen = rows.filter({ hasText: "千问" });
    await qwen.getByText("最新版本暂不可用").waitFor();
    assert(await qwen.getByRole("button", { name: "一键安装" }).isEnabled());
    console.log("PASS settings drawer update, recheck, manual guide and offline release");
  } finally { await browser.close(); }
}

run().catch(error => { console.error(error); process.exitCode = 1; });
