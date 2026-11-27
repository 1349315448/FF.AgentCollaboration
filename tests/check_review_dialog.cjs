const assert = require("node:assert/strict");
const { chromium } = require("playwright");

async function verifyShare(page, url) {
  const directLink = "ffcp://192.0.2.10:5187/share/test";
  const rtcLink = "ffcp://192.0.2.10:5190/signal/test";
  let rtcBlockReason = "signaling_not_configured";
  let issued = 0;
  await page.addInitScript(() => {
    window.copiedLinks = [];
    Object.defineProperty(navigator, "clipboard", {
      value: { writeText: async text => { window.copiedLinks.push(text); } }
    });
  });
  await page.route("**/api/peers/windows/*", route => route.fulfill({
    json: { enabled: true, link: directLink, rtc: rtcBlockReason === null, rtcBlockReason }
  }));
  await page.route("**/api/peers/windows/*/signaling-invitation", route => {
    issued++;
    return route.fulfill({ json: { link: rtcLink } });
  });
  await page.goto(url, { waitUntil: "domcontentloaded" });
  const share = page.getByRole("button", { name: "分享", exact: true });
  const dialog = page.locator("#federation-dialog[open]");
  const direct = dialog.getByRole("button", { name: "直接连接", exact: true });
  const rtc = dialog.getByRole("button", { name: "跨内网连接", exact: true });
  const copied = () => page.evaluate(() => window.copiedLinks);

  await share.click();
  await dialog.getByText("未配置信令服务器", { exact: true }).waitFor();
  assert.equal(await rtc.isDisabled(), true);
  const left = await direct.boundingBox(), right = await rtc.boundingBox();
  assert(left && right && right.x > left.x && Math.abs(left.y - right.y) < 1 && Math.abs(left.width - right.width) < 1);
  assert.deepEqual(await copied(), []);
  assert.equal(issued, 0);
  await direct.click();
  await dialog.getByText("已复制直接连接链接", { exact: true }).waitFor();
  assert.deepEqual(await copied(), [directLink]);
  await dialog.getByRole("button", { name: "关闭", exact: true }).click();
  await dialog.waitFor({ state: "hidden" });

  for (const [reason, message] of [
    ["bridge_missing", "缺少 WebRTC 后台组件"],
    ["signing_key_missing", "生成链接需要签发密钥"],
    ["restart_required", "重启工作台后生效"],
    ["settings_unreadable", "信令设置无法解密，请重新填写"]
  ]) {
    rtcBlockReason = reason;
    await share.click();
    await dialog.getByText(message, { exact: true }).waitFor();
    assert.equal(await rtc.isDisabled(), true);
    assert.equal(await dialog.getByText("未配置信令服务器", { exact: true }).count(), 0);
    await dialog.getByRole("button", { name: "关闭", exact: true }).click();
    await dialog.waitFor({ state: "hidden" });
  }

  rtcBlockReason = null;
  await share.click();
  await rtc.waitFor();
  assert.equal(await rtc.isEnabled(), true);
  assert.deepEqual(await copied(), [directLink]);
  await rtc.click();
  await dialog.getByText("已复制跨内网链接，15 分钟内可领取", { exact: true }).waitFor();
  assert.equal(issued, 1);
  assert.deepEqual(await copied(), [directLink, rtcLink]);
  console.log("PASS Chromium share chooser, disabled signaling, and explicit clipboard actions");
}

async function verifySignalingSettings(page, url) {
  await page.goto(url, { waitUntil: "domcontentloaded" });
  await page.locator("#open-settings").click();
  const dialog = page.locator("#settings-dialog[open]");
  const field = dialog.getByLabel("签发密钥（仅生成链接的一端填写）");
  const fingerprint = dialog.getByLabel("公钥指纹", { exact: true });
  const save = dialog.getByRole("button", { name: "保存信令设置", exact: true });
  await dialog.getByLabel("服务地址", { exact: true }).fill(process.env.FF_TEST_SIGNAL_ORIGIN);
  const consoleLine = "FF_SIGNALING_SPKI_SHA256  " + process.env.FF_TEST_SIGNAL_PIN;
  await fingerprint.click();
  await page.keyboard.insertText(consoleLine);
  assert.equal(await fingerprint.inputValue(), consoleLine);
  await field.fill(process.env.FF_TEST_SIGNAL_KEY);
  await save.click();
  await dialog.getByText("已保存信令设置", { exact: true }).waitFor();
  assert.equal(await field.inputValue(), "");
  assert.equal(await field.getAttribute("placeholder"), "已保存，留空保留");
  await dialog.getByRole("button", { name: "测试连接", exact: true }).click();
  await dialog.getByText("连接成功，公钥指纹已核对", { exact: true }).waitFor();
  await dialog.getByRole("button", { name: "关闭配置", exact: true }).click();
  await dialog.waitFor({ state: "hidden" });
  const reload = page.waitForResponse(response =>
    new URL(response.url()).pathname === "/api/peers/signaling/settings" && response.request().method() === "GET" && response.ok());
  await page.locator("#open-settings").click();
  await reload;
  await dialog.getByRole("button", { name: "测试连接", exact: true }).waitFor();
  await page.waitForFunction(pin => document.querySelector('input[placeholder="可粘贴服务器控制台的整行指纹"]')?.value === pin,
    process.env.FF_TEST_SIGNAL_PIN.toUpperCase());
  assert.equal(await dialog.getByLabel("服务地址", { exact: true }).inputValue(), process.env.FF_TEST_SIGNAL_ORIGIN);
  assert.equal(await field.inputValue(), "");
  assert.equal(await field.getAttribute("placeholder"), "已保存，留空保留");
  console.log("PASS Chromium pastes a full console fingerprint line, verifies TLS and restores saved settings on reopen");
}

async function verify(url, mode) {
  const browser = await chromium.launch({ executablePath: process.env.CHROME_PATH || undefined });
  try {
    const page = await browser.newPage();
    if (mode === "share") return await verifyShare(page, url);
    if (mode === "signaling-settings") return await verifySignalingSettings(page, url);
    await page.goto(url, { waitUntil: "domcontentloaded" });
    const dialog = page.locator("#federation-dialog[open]");
    const title = dialog.getByRole("heading", { name: mode === "application" ? "外部协作申请" : "发送附件授权" });
    await title.waitFor({ timeout: 15_000 });
    if (mode === "application") {
      assert(await dialog.getByText("后台传输验证", { exact: true }).isVisible());
      assert(await dialog.getByRole("button", { name: "同意", exact: true }).isEnabled());
      assert(await dialog.getByRole("button", { name: "拒绝", exact: true }).isEnabled());
    } else {
      assert(await dialog.getByText("intro.txt", { exact: true }).isVisible());
      assert(await dialog.getByRole("button", { name: "永久授权此 Agent", exact: true }).isEnabled());
      await dialog.getByRole("button", { name: "单次授权", exact: true }).click();
      await dialog.waitFor({ state: "hidden", timeout: 10_000 });
    }
    console.log(`PASS Chromium ${mode} approval dialog`);
  } finally {
    await browser.close();
  }
}

verify(...process.argv.slice(2)).catch(error => { console.error(error); process.exitCode = 1; });
