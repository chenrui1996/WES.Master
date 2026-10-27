import { expect, test } from "@playwright/test";
import { readFile } from "node:fs/promises";

function relativeLuminance(color: string): number {
  const match = /^#([0-9a-f]{3}|[0-9a-f]{6})$/i.exec(color);
  if (!match?.[1]) throw new Error(`Expected a computed hex color, received ${color}`);
  const hex = match[1].length === 3 ? [...match[1]].map(channel => `${channel}${channel}`).join("") : match[1];
  const channel = (offset: number) => {
    const value = Number.parseInt(hex.slice(offset, offset + 2), 16) / 255;
    return value <= 0.04045 ? value / 12.92 : ((value + 0.055) / 1.055) ** 2.4;
  };
  return 0.2126 * channel(0) + 0.7152 * channel(2) + 0.0722 * channel(4);
}

function contrastRatio(foreground: string, background: string): number {
  const values = [relativeLuminance(foreground), relativeLuminance(background)].sort((left, right) => right - left);
  return (values[0]! + 0.05) / (values[1]! + 0.05);
}

test("real HTTPS identity, authorized named pages and responsive frame", async ({ page }) => {
  if (!process.env.WES_E2E_NOW) throw new Error("The isolated HTTPS fixture must provide its server clock.");
  await page.clock.setFixedTime(new Date(process.env.WES_E2E_NOW));
  const failures: string[] = [];
  page.on("pageerror", error => failures.push(error.message));
  await page.route("**/api/config/features", route => route.fulfill({
    contentType: "application/json",
    body: JSON.stringify({
      modules: ["Platform", "Wms", "Orchestration", "WcsStacker", "WcsFourWay", "Simulator"]
        .map(code => ({ code, enabled: code === "Platform" })),
    }),
  }));
  await page.goto("/login");
  await expect(page.getByRole("heading", { name: "登录运行管理" })).toBeVisible();
  await expect(page.locator(".login-main .wes-page")).toHaveCount(1);
  await expect(page.locator(".app-layout")).toHaveCount(0);
  await page.getByLabel("租户标识").fill(process.env.WES_E2E_TENANT!);
  await page.getByLabel("账号", { exact: true }).fill("fixture");
  await page.getByLabel("密码", { exact: true }).fill("WES reference fixture password");
  await page.getByRole("button", { name: "登录", exact: true }).click();
  await expect(page.getByRole("heading", { name: "会话与安全", exact: true })).toBeVisible();
  await expect(page.locator(".workspace .wes-page")).toHaveCount(1);
  const initialPreferences = await page.evaluate(() => Object.entries(localStorage));
  expect(initialPreferences.every(([key]) => key.startsWith("wes-ops-web:columns:v1:"))).toBe(true);
  expect(JSON.stringify(initialPreferences)).not.toContain("accessToken");
  expect(await page.evaluate(() => sessionStorage.length)).toBe(0);
  await page.setViewportSize({ width: 1440, height: 900 });
  const nav = page.getByRole("navigation", { name: "管理导航" });
  const toggle = page.getByTestId("navigation-toggle");
  const densityControl = page.getByLabel("显示密度");
  await expect(toggle).toHaveAttribute("aria-expanded", "false");
  const collapsedWidth = await page.locator(".sidebar").evaluate(element => element.getBoundingClientRect().width);
  expect(collapsedWidth).toBeGreaterThanOrEqual(56);
  expect(collapsedWidth).toBeLessThanOrEqual(64);
  await page.locator(".brand").focus();
  await page.keyboard.press("Tab");
  await expect(densityControl).toBeFocused();
  expect(await densityControl.evaluate(element => element.matches(":focus-visible"))).toBe(true);
  await page.keyboard.press("ArrowDown");
  await expect(page.locator(".app-layout")).toHaveAttribute("data-density", "standard");
  await page.keyboard.press("ArrowDown");
  await expect(page.locator(".app-layout")).toHaveAttribute("data-density", "comfortable");
  await page.keyboard.press("Tab");
  await page.keyboard.press("Tab");
  await expect(toggle).toBeFocused();
  const keyboardFocus = page.locator(":focus-visible");
  await expect(keyboardFocus).toBeVisible();
  expect(await keyboardFocus.evaluate(element => Number.parseFloat(getComputedStyle(element).outlineWidth))).toBeGreaterThanOrEqual(2);
  await toggle.click();
  await expect(toggle).toHaveAttribute("aria-expanded", "true");
  const expandedWidth = await page.locator(".sidebar").evaluate(element => element.getBoundingClientRect().width);
  expect(expandedWidth).toBeGreaterThanOrEqual(216);
  expect(expandedWidth).toBeLessThanOrEqual(232);
  expect(expandedWidth).toBeGreaterThan(collapsedWidth + 100);
  await expect(nav.getByRole("link", { name: "用户与部门", exact: true })).toBeVisible();
  await expect(page.locator(".app-layout")).toHaveAttribute("data-density", "comfortable");
  const densityPreference = await page.evaluate(() => Object.entries(localStorage)
    .find(([key]) => key.startsWith("wes-ops-web:density:")) ?? null);
  expect(densityPreference?.[0]).toContain("production");
  expect(densityPreference?.[1]).toBe("comfortable");
  const storedVisualAndColumnPreferences = await page.evaluate(() => Object.entries(localStorage));
  expect(storedVisualAndColumnPreferences.every(([key]) =>
    key.startsWith("wes-ops-web:columns:v1:") || key.startsWith("wes-ops-web:density:"))).toBe(true);
  expect(JSON.stringify(storedVisualAndColumnPreferences)).not.toContain("accessToken");
  for (const width of [375, 768, 1024, 1440]) {
    await page.setViewportSize({ width, height: 900 });
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true);
  }
  const messageRequests: string[] = [];
  page.on("request", request => { if (new URL(request.url()).pathname === "/api/platform/messages") messageRequests.push(request.method()); });
  await nav.getByRole("link", { name: "消息诊断", exact: true }).click();
  await expect(page.getByRole("heading", { name: "消息诊断 · 仅 Platform" })).toBeVisible();
  expect(messageRequests).toEqual([]);
  const firstCounts = page.waitForResponse(response => new URL(response.url()).pathname === "/api/platform/messages/summary");
  await page.getByRole("button", { name: "读取 Platform 计数", exact: true }).click();
  const countResponse = await firstCounts;
  expect(countResponse.status()).toBe(200);
  expect(countResponse.headers()["cache-control"]).toContain("no-store");
  expect((await countResponse.json()).summary).toMatchObject({
    owner: "Platform", pendingCount: 59, leasedCount: 0, publishedCount: 0, quarantinedCount: 1, inboxReceiptCount: 0,
  });
  await expect(page.getByTestId("message-counts")).toContainText("现存 Inbox 收据0");
  const readMessages = page.getByRole("button", { name: "读取 Platform 消息", exact: true });
  await readMessages.focus();
  await page.keyboard.press("Shift+Tab");
  await page.keyboard.press("Tab");
  await expect(readMessages).toBeFocused();
  expect(await readMessages.evaluate(element => element.matches(":focus-visible"))).toBe(true);
  const messageResponse = page.waitForResponse(response => new URL(response.url()).pathname === "/api/platform/messages");
  await page.keyboard.press("Enter");
  const receivedMessages = await messageResponse;
  expect(receivedMessages.status()).toBe(200);
  expect(receivedMessages.headers()["cache-control"]).toContain("no-store");
  const messageBody = await receivedMessages.json() as { authorized: boolean; rows: { eventId: string; owner: string }[] };
  expect(messageBody.authorized).toBe(true);
  expect(messageBody.rows).toHaveLength(25);
  expect(messageBody.rows.every(row => row.owner === "Platform")).toBe(true);
  const messageList = page.getByTestId("messages");
  await expect(messageList.locator("li")).toHaveCount(25);
  await expect(messageList).toContainText(messageBody.rows[0]!.eventId);
  await expect(page.getByRole("main")).toContainText("不表示消费成功或业务完成");
  for (const width of [375, 768, 1440]) {
    await page.setViewportSize({ width, height: 900 });
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true);
  }
  const enlargedMessages = await page.addStyleTag({ content: "html { font-size: 200% !important; }" });
  expect(await page.evaluate(() => getComputedStyle(document.documentElement).fontSize)).toBe("32px");
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true);
  await enlargedMessages.evaluate(element => element.parentNode?.removeChild(element));
  await page.context().setOffline(true);
  await expect(readMessages).toBeDisabled();
  await expect(page.getByRole("main")).toContainText("陈旧");
  await page.context().setOffline(false);
  await expect(readMessages).toBeEnabled();
  expect(messageRequests).toEqual(["GET"]);
  const replayId = process.env.WES_E2E_REPLAY_EVENT;
  if (!replayId) throw new Error("The isolated owner fixture must provide a quarantined safe event.");
  const replayRow = messageList.locator("li").filter({ hasText: replayId });
  await expect(replayRow.getByRole("button", { name: "重新读取并准备此事件重放", exact: true })).toBeDisabled();
  const onlineReread = page.waitForResponse(response => new URL(response.url()).pathname === "/api/platform/messages");
  await readMessages.click();
  expect((await onlineReread).status()).toBe(200);
  await page.getByLabel("消息状态", { exact: true }).selectOption("3");
  await expect(messageList).toHaveCount(0);
  const messageNow = Date.parse(process.env.WES_E2E_NOW);
  await page.getByLabel("创建开始（UTC，含）", { exact: true }).fill(new Date(messageNow - 60000).toISOString().slice(0, 16));
  await page.getByLabel("创建结束（UTC，不含）", { exact: true }).fill(new Date(messageNow + 60000).toISOString().slice(0, 16));
  const filteredRead = page.waitForResponse(response => new URL(response.url()).pathname === "/api/platform/messages");
  await readMessages.click();
  const filteredMessages = await filteredRead;
  expect(filteredMessages.status()).toBe(200);
  expect(new URL(filteredMessages.url()).searchParams.get("status")).toBe("3");
  const filteredBody = await filteredMessages.json() as { rows: { eventId: string; status: number }[] };
  expect(filteredBody.rows).toHaveLength(1);
  expect(filteredBody.rows[0]).toMatchObject({ eventId: replayId, status: 3 });
  const freshReplayRead = page.waitForResponse(response => new URL(response.url()).pathname === "/api/platform/messages");
  await replayRow.getByRole("button", { name: "重新读取并准备此事件重放", exact: true }).click();
  const freshFilteredRead = await freshReplayRead;
  expect(freshFilteredRead.status()).toBe(200);
  expect(new URL(freshFilteredRead.url()).searchParams.get("status")).toBe("3");
  await expect(page.getByRole("heading", { name: "Platform 审计消息重放预览" })).toBeVisible();
  await page.getByLabel("重放理由", { exact: true }).fill("Browser reviewed contract repair");
  await page.getByRole("button", { name: "预览原事件重放", exact: true }).click();
  await expect(page.getByTestId("replay-preview")).toContainText(replayId);
  const previewOperation = await page.getByTestId("replay-preview").locator("dd").last().innerText();
  expect(previewOperation).toMatch(/^[a-f0-9-]{36}$/);
  expect(messageRequests).toEqual(["GET", "GET", "GET", "GET"]);
  for (const width of [375, 768, 1440]) {
    await page.setViewportSize({ width, height: 900 });
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true);
  }
  const replayResponse = page.waitForResponse(response => new URL(response.url()).pathname === `/api/platform/messages/${replayId}/replay`);
  const confirmReplay = page.getByRole("button", { name: "确认提交此原事件", exact: true });
  await confirmReplay.focus();
  await page.keyboard.press("Enter");
  const replayed = await replayResponse;
  expect(replayed.status()).toBe(200);
  expect(replayed.request().headers()["idempotency-key"]).toBe(previewOperation);
  expect(replayed.request().postDataJSON()).toEqual({ expectedAttempts: 1, reason: "Browser reviewed contract repair" });
  await expect(page.getByRole("status").filter({ hasText: "已提交或已识别原操作" })).toBeVisible();
  const countsRegion = page.getByRole("region", { name: "Platform 消息计数", exact: true });
  await expect(countsRegion).toContainText("陈旧统计");
  const rereadCounts = page.waitForResponse(response => new URL(response.url()).pathname === "/api/platform/messages/summary");
  await page.getByRole("button", { name: "读取 Platform 计数", exact: true }).click();
  expect((await rereadCounts).status()).toBe(200);
  await expect(countsRegion).not.toContainText("陈旧统计");
  await expect(page.getByTestId("message-counts")).toContainText("已隔离0");
  await expect(messageList).toHaveCount(0);
  await page.getByRole("button", { name: "关闭重放面板", exact: true }).click();
  await page.getByLabel("消息状态", { exact: true }).selectOption("");
  const afterReplay = page.waitForResponse(response => new URL(response.url()).pathname === "/api/platform/messages");
  await readMessages.click();
  expect((await afterReplay).status()).toBe(200);
  await expect(messageList.locator("li").filter({ hasText: replayId })).toContainText("待发布");
  expect(messageRequests).toEqual(["GET", "GET", "GET", "GET", "GET"]);
  await page.setViewportSize({ width: 1440, height: 900 });
  for (const [link, api] of [
    ["用户与部门", "/api/platform/users"], ["角色与权限", "/api/platform/roles"],
    ["租户", "/api/platform/tenants"], ["配置与语言", "/api/platform/settings"], ["审计与登录事件", "/api/platform/audit"],
  ]) {
    const response = page.waitForResponse(value => value.url().includes(api!) && value.request().method() === "GET");
    await page.getByRole("navigation", { name: "管理导航" }).getByRole("link", { name: link!, exact: true }).click();
    expect((await response).status()).toBe(200);
    await expect(page.getByRole("main").getByRole("heading", { level: 1 })).toBeVisible();
    await expect(page.locator(".workspace .wes-page")).toHaveCount(1);
    const tables = page.locator(".table-scroll");
    if (api !== "/api/platform/audit") {
      expect(await tables.count()).toBeGreaterThan(0);
      await expect(tables.first()).toBeVisible();
    }
    for (const width of [375, 768, 1024, 1440]) {
      await page.setViewportSize({ width, height: 900 });
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true);
    }
    if (api === "/api/platform/audit") {
      await page.getByLabel("开始时间（UTC，含）").fill("2026-10-24T00:00");
      await page.getByLabel("结束时间（UTC，不含）").fill("2026-10-25T00:00");
      await page.getByLabel("关联 UUID").fill(process.env.WES_E2E_AUDIT_CORRELATION!);
      const firstPage = page.waitForResponse(value =>
        value.url().includes("/api/platform/audit?") && new URL(value.url()).searchParams.get("limit") === "25");
      await page.getByRole("button", { name: "查询实时证据", exact: true }).click();
      expect((await firstPage).status()).toBe(200);
      const evidenceRows = page.locator(".query-state tbody tr");
      await expect(evidenceRows).toHaveCount(25);
      const firstIds = await evidenceRows.locator("td:first-child").allTextContents();
      const pagination = page.getByRole("navigation", { name: "证据分页", exact: true });
      await expect(pagination).toContainText("实时");
      await expect(pagination).toContainText("实时数据变化时不保证跨请求无丢重");
      const secondPage = page.waitForResponse(value =>
        value.url().includes("/api/platform/audit?") && new URL(value.url()).searchParams.get("offset") === "25");
      await pagination.getByRole("button", { name: "下一页", exact: true }).click();
      expect((await secondPage).status()).toBe(200);
      await expect(evidenceRows).toHaveCount(25);
      const secondIds = await evidenceRows.locator("td:first-child").allTextContents();
      const thirdPage = page.waitForResponse(value =>
        value.url().includes("/api/platform/audit?") && new URL(value.url()).searchParams.get("offset") === "50");
      await pagination.getByRole("button", { name: "下一页", exact: true }).click();
      expect((await thirdPage).status()).toBe(200);
      await expect(evidenceRows).toHaveCount(10);
      const thirdIds = await evidenceRows.locator("td:first-child").allTextContents();
      expect(new Set([...firstIds, ...secondIds, ...thirdIds]).size).toBe(60);
      await expect(pagination.getByRole("button", { name: "下一页", exact: true })).toBeDisabled();
      for (const size of ["50", "100"]) {
        const resized = page.waitForResponse(value => value.url().includes("/api/platform/audit?")
          && new URL(value.url()).searchParams.get("limit") === size
          && new URL(value.url()).searchParams.get("offset") === "0");
        await pagination.getByRole("combobox", { name: "每页条数", exact: true }).selectOption(size);
        expect((await resized).status()).toBe(200);
        await expect(evidenceRows).toHaveCount(size === "50" ? 50 : 60);
      }
      const liveTwentyFive = page.waitForResponse(value => value.url().includes("/api/platform/audit?")
        && new URL(value.url()).searchParams.get("limit") === "25"
        && new URL(value.url()).searchParams.get("offset") === "0");
      await pagination.getByRole("combobox", { name: "每页条数", exact: true }).selectOption("25");
      expect((await liveTwentyFive).status()).toBe(200);
      const createSnapshot = page.waitForResponse(value =>
        value.url().endsWith("/api/platform/audit/snapshots") && value.request().method() === "POST");
      await page.getByRole("button", { name: "创建固定核对快照", exact: true }).click();
      expect((await createSnapshot).status()).toBe(200);
      await expect(pagination).toContainText("固定快照");
      await expect(page.getByRole("status").filter({ hasText: "固定核对快照：60 条" })).toBeVisible();
      await expect(evidenceRows).toHaveCount(25);
      const snapshotFirstIds = await evidenceRows.locator("td:first-child").allTextContents();
      const snapshotSecondPage = page.waitForResponse(value =>
        value.url().includes("/api/platform/audit/snapshots/") && value.url().includes("offset=25"));
      await pagination.getByRole("button", { name: "下一页", exact: true }).click();
      expect((await snapshotSecondPage).status()).toBe(200);
      await expect(evidenceRows).toHaveCount(25);
      const snapshotSecondIds = await evidenceRows.locator("td:first-child").allTextContents();
      const snapshotThirdPage = page.waitForResponse(value =>
        value.url().includes("/api/platform/audit/snapshots/") && value.url().includes("offset=50"));
      await pagination.getByRole("button", { name: "下一页", exact: true }).click();
      expect((await snapshotThirdPage).status()).toBe(200);
      await expect(evidenceRows).toHaveCount(10);
      const snapshotThirdIds = await evidenceRows.locator("td:first-child").allTextContents();
      expect(new Set([...snapshotFirstIds, ...snapshotSecondIds, ...snapshotThirdIds]).size).toBe(60);
      const exportForm = page.getByRole("form", { name: "导出固定审计快照", exact: true });
      await exportForm.getByRole("checkbox", { name: "动作码", exact: true }).check();
      const exported = page.waitForEvent("download");
      await exportForm.getByRole("button", { name: "导出所选列 CSV", exact: true }).click();
      const downloaded = await exported;
      expect(downloaded.suggestedFilename()).toMatch(/^audit-[0-9a-f-]{36}\.csv$/);
      const downloadPath = await downloaded.path();
      expect(downloadPath).not.toBeNull();
      const csv = (await readFile(downloadPath!)).toString("utf8").split("\r\n").filter(Boolean);
      expect(csv[0]).toBe('"Id","OccurredAt","Action"');
      expect(csv).toHaveLength(61);
      expect(csv.slice(1).map(line => line.split(",")[0]!.replaceAll('"', "")))
        .toEqual([...snapshotFirstIds, ...snapshotSecondIds, ...snapshotThirdIds]);
      expect(csv.slice(1).every(line => line.endsWith(',"RoleChanged"'))).toBe(true);
      await downloaded.delete();
      await expect(exportForm.getByRole("status")).toContainText("不代表客户端已收齐");
      await expect(page.getByRole("button", { name: "返回实时查询", exact: true })).toBeVisible();
      for (const width of [375, 768, 1024, 1440]) {
        await page.setViewportSize({ width, height: 900 });
        expect(await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true);
      }
    }
    if (api === "/api/platform/settings") {
      const settingRow = page.getByRole("row").filter({ hasText: "query.snapshotTtlSeconds" });
      await settingRow.getByTestId("edit-setting").click();
      await page.getByLabel("新的正整数值").fill("300");
      const settingWrite = page.waitForResponse(value =>
        value.url().includes("/api/platform/settings/") && value.request().method() === "PUT");
      await page.getByRole("button", { name: "确认提交此版本", exact: true }).click();
      expect((await settingWrite).status()).toBe(200);
      await expect(page.getByText("配置已提交；受影响服务的实际读取与现场许可仍按各自门禁判断。")).toBeVisible();
      await expect(settingRow).toContainText("300");
      await expect(settingRow).toContainText("AuthorizedOverride");
      await page.setViewportSize({ width: 1600, height: 900 });
      const settingsTable = page.locator(".table-scroll").first();
      await page.locator(".column-settings").first().locator("summary").click();
      await page.getByTestId("fixed-position-key").selectOption("right");
      await page.getByTestId("apply-columns").first().click();
      const keyHeader = settingsTable.getByRole("columnheader", { name: "稳定键", exact: true });
      await expect(keyHeader).toHaveClass(/table-pinned-right/);
      await expect.poll(() => keyHeader.evaluate(element => getComputedStyle(element).insetInlineEnd)).toBe("160px");
      await expect.poll(() => settingsTable.getByRole("columnheader", { name: "操作", exact: true })
        .evaluate(element => getComputedStyle(element).position)).toBe("sticky");
      const columnPreference = await page.evaluate(() => Object.entries(localStorage)
        .find(([key]) => key.startsWith("wes-ops-web:columns:v1:") && key.endsWith(":platform-settings:settings")) ?? null);
      expect(columnPreference?.[0]).toContain(":platform-settings:settings");
      expect(columnPreference?.[1]).toContain('"fixedPositions":{"key":"right"}');

      await page.setViewportSize({ width: 375, height: 900 });
      await expect(settingsTable.locator(".table-fixed-notice")).toBeVisible();
      await expect.poll(() => keyHeader.evaluate(element => getComputedStyle(element).insetInlineEnd)).toBe("auto");
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true);
      for (const width of [721, 720]) {
        for (let attempt = 0; attempt < 5; attempt++) {
          const frameWidth = await settingsTable.evaluate(element => element.clientWidth);
          if (frameWidth === width) break;
          const viewportWidth = await page.evaluate(() => window.innerWidth);
          await page.setViewportSize({
            width: Math.max(375, Math.round(viewportWidth + width - frameWidth)),
            height: 900,
          });
        }
        await expect.poll(() => settingsTable.evaluate(element => element.clientWidth)).toBe(width);
        const identityHeader = settingsTable.getByRole("columnheader", { name: "标识", exact: true });
        if (width === 721) {
          await expect(settingsTable.locator(".table-fixed-notice")).toBeHidden();
          await expect(identityHeader).toHaveClass(/table-pinned-left/);
          await expect.poll(() => identityHeader.evaluate(element => getComputedStyle(element).insetInlineStart)).toBe("0px");
        } else {
          await expect(settingsTable.locator(".table-fixed-notice")).toBeVisible();
          await expect(identityHeader).toHaveClass(/table-pinned-left/);
          await expect.poll(() => identityHeader.evaluate(element => getComputedStyle(element).insetInlineStart)).toBe("auto");
        }
        expect(await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true);
      }
      await page.setViewportSize({ width: 1440, height: 900 });
      const zoomEquivalent = await page.context().newCDPSession(page);
      await zoomEquivalent.send("Emulation.setDeviceMetricsOverride", {
        width: 720,
        height: 900,
        deviceScaleFactor: 2,
        mobile: false,
      });
      expect(await page.evaluate(() => window.innerWidth)).toBe(720);
      expect(await page.evaluate(() => window.devicePixelRatio)).toBe(2);
      await expect(settingsTable.locator(".table-fixed-notice")).toBeVisible();
      await expect.poll(() => keyHeader.evaluate(element => getComputedStyle(element).insetInlineEnd)).toBe("auto");
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true);
      await zoomEquivalent.send("Emulation.clearDeviceMetricsOverride");
      await zoomEquivalent.detach();
      await page.setViewportSize({ width: 1440, height: 900 });
      const largeText = await page.addStyleTag({ content: "html { font-size: 200% !important; }" });
      await page.setViewportSize({ width: 1024, height: 900 });
      expect(await page.evaluate(() => getComputedStyle(document.documentElement).fontSize)).toBe("32px");
      expect(await page.evaluate(() => getComputedStyle(document.body).fontSize)).toBe("28px");
      expect(await page.evaluate(() => getComputedStyle(document.querySelector("h1")!).fontSize)).toBe("40px");
      const scaledHeading = page.getByRole("main").getByRole("heading", { level: 1 });
      await expect(scaledHeading).toBeVisible();
      const expandedHeading = await scaledHeading.evaluate(element => {
        const original = element.textContent ?? "";
        const extension = "扩".repeat(Math.ceil(original.length * 0.4));
        element.textContent = original + extension;
        return { originalLength: original.length, expandedLength: original.length + extension.length };
      });
      expect(expandedHeading.expandedLength).toBeGreaterThanOrEqual(expandedHeading.originalLength * 1.4);
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true);
      await expect(settingsTable).toBeVisible();
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true);
      const tableOverflow = await settingsTable.evaluate(element => element.scrollWidth >= element.clientWidth);
      expect(tableOverflow).toBe(true);
      await largeText.evaluate(element => element.parentNode?.removeChild(element));
      await page.setViewportSize({ width: 1440, height: 900 });
    }
  }
  const auditMode = page.getByLabel("证据类型");
  await auditMode.selectOption("login-events");
  await page.getByLabel("开始时间（UTC，含）").fill("2026-10-24T00:00");
  await page.getByLabel("结束时间（UTC，不含）").fill("2026-10-25T00:00");
  await page.getByLabel("关联 UUID").fill("");
  const loginEvents = page.waitForResponse(value => value.url().includes("/api/platform/login-events") && value.request().method() === "GET");
  const queryButton = page.getByRole("button", { name: "查询实时证据", exact: true });
  let queryButtonReached = false;
  for (let index = 0; index < 20 && !queryButtonReached; index++) {
    await page.keyboard.press("Tab");
    queryButtonReached = await queryButton.evaluate(element => element === document.activeElement);
  }
  expect(queryButtonReached).toBe(true);
  await expect(queryButton).toBeFocused();
  expect(await queryButton.evaluate(element => element.matches(":focus-visible"))).toBe(true);
  await page.keyboard.press("Enter");
  expect((await loginEvents).status()).toBe(200);
  const auditRows = page.locator(".table-scroll tbody tr");
  await expect(auditRows.first()).toBeVisible();
  const densityRows = [
    { mode: "compact", min: 36 },
    { mode: "standard", min: 44 },
    { mode: "comfortable", min: 52 },
  ] as const;
  const measuredRowHeights: number[] = [];
  const measuredBlockPadding: number[] = [];
  for (const densityRow of densityRows) {
    await densityControl.selectOption(densityRow.mode);
    const rowHeight = await auditRows.first().evaluate(element => element.getBoundingClientRect().height);
    expect(rowHeight).toBeGreaterThanOrEqual(densityRow.min);
    measuredRowHeights.push(rowHeight);
    measuredBlockPadding.push(await auditRows.first().locator("td").first().evaluate(element =>
      Number.parseFloat(getComputedStyle(element).paddingBlockStart)));
    const shortControls = await page.locator("button, input:not([type=checkbox]), select").evaluateAll(elements =>
      elements.filter(element => {
        const style = getComputedStyle(element);
        const bounds = element.getBoundingClientRect();
        return style.display !== "none" && style.visibility !== "hidden" && bounds.width > 0 && bounds.height < 44;
      }).map(element => ({
        tag: element.tagName,
        type: element.getAttribute("type"),
        label: element.getAttribute("aria-label") ?? element.getAttribute("id"),
        height: element.getBoundingClientRect().height,
      })));
    expect(shortControls).toEqual([]);
  }
  expect(measuredRowHeights[1]! - measuredRowHeights[0]!).toBeGreaterThanOrEqual(8);
  expect(measuredRowHeights[1]! - measuredRowHeights[0]!).toBeLessThanOrEqual(12);
  expect(measuredRowHeights[2]! - measuredRowHeights[1]!).toBeGreaterThanOrEqual(8);
  expect(measuredRowHeights[2]! - measuredRowHeights[1]!).toBeLessThanOrEqual(12);
  expect(measuredBlockPadding).toEqual([10, 15, 19]);
  const palette = await page.evaluate(() => {
    const style = getComputedStyle(document.documentElement);
    const token = (name: string) => style.getPropertyValue(name).trim();
    return {
      text: token("--wes-color-text"),
      muted: token("--wes-color-muted"),
      surface: token("--wes-color-surface"),
      workspace: token("--wes-color-workspace"),
      primaryInk: token("--wes-color-primary-ink"),
      primary: token("--wes-color-primary"),
      danger: token("--wes-color-danger"),
      dangerSoft: token("--wes-color-danger-soft"),
      warning: token("--wes-color-warning"),
      warningSoft: token("--wes-color-warning-soft"),
      success: token("--wes-color-success"),
      successSoft: token("--wes-color-success-soft"),
      focus: token("--wes-color-focus"),
      border: token("--wes-color-border"),
    };
  });
  for (const [foreground, background] of [
    [palette.text, palette.surface], [palette.text, palette.workspace],
    [palette.muted, palette.surface], [palette.primaryInk, palette.primary],
    [palette.danger, palette.dangerSoft], [palette.warning, palette.warningSoft],
    [palette.success, palette.successSoft],
  ]) {
    expect(contrastRatio(foreground!, background!)).toBeGreaterThanOrEqual(4.5);
  }
  expect(contrastRatio(palette.focus, palette.surface)).toBeGreaterThanOrEqual(3);
  expect(contrastRatio(palette.focus, palette.workspace)).toBeGreaterThanOrEqual(3);
  expect(contrastRatio(palette.border, palette.surface)).toBeGreaterThanOrEqual(3);
  await page.emulateMedia({ reducedMotion: "reduce" });
  expect(await page.evaluate(() => matchMedia("(prefers-reduced-motion: reduce)").matches)).toBe(true);
  expect(await page.locator(".app-layout").evaluate(element => getComputedStyle(element).transitionDuration)).toBe("0s");
  await page.getByRole("navigation", { name: "管理导航" }).getByRole("link", { name: "用户与部门", exact: true }).click();
  await page.getByTestId("new-user").click();
  const form = page.getByTestId("user-form");
  await form.locator('[name="account"]').fill("browser-new");
  await form.locator('[name="userName"]').fill("Browser subject");
  await form.locator('[name="departmentId"]').selectOption({ index: 1 });
  const created = page.waitForResponse(value => value.url().endsWith("/api/platform/users") && value.request().method() === "POST");
  await form.getByRole("button", { name: "保存用户", exact: true }).click();
  expect((await created).status()).toBe(200);
  const row = page.getByRole("row").filter({ hasText: "browser-new" });
  await expect(row).toBeVisible();
  await row.getByTestId("credential-user").click();
  await page.locator('[name="password"]').fill("browser-created subject fixture password");
  const credential = page.waitForResponse(value => value.url().includes("/credential") && value.request().method() === "POST");
  await page.getByRole("button", { name: "确认创建凭据", exact: true }).click();
  expect((await credential).status()).toBe(200);
  await expect(row.getByTestId("roles-user")).toBeEnabled();
  await row.getByTestId("roles-user").click();
  await page.getByRole("checkbox", { name: "fixture-owner", exact: true }).check();
  const assigned = page.waitForResponse(value => value.url().includes("/roles") && value.request().method() === "PUT");
  await page.getByRole("button", { name: "确认替换角色", exact: true }).click();
  expect((await assigned).status()).toBe(200);
  await row.getByTestId("disable-credential").click();
  await expect(page.getByText(/该用户的全部现有会话将被撤销/)).toBeVisible();
  await expect(page.getByText(/停用后不能通过此页面重新启用凭据/)).toBeVisible();
  const disabled = page.waitForResponse(value =>
    value.url().includes("/api/platform/users/") && value.url().endsWith("/disable") && value.request().method() === "POST");
  await page.getByRole("button", { name: "确认停用凭据", exact: true }).click();
  expect((await disabled).status()).toBe(204);
  await expect(page.getByText("登录凭据已停用，现有会话已撤销；列表已重新读取。")).toBeVisible();
  await expect(row.getByTestId("disable-credential")).toHaveCount(0);
  expect(failures).toEqual([]);
  await page.getByRole("button", { name: "退出当前会话" }).click();
  await expect(page.getByRole("heading", { name: "登录运行管理" })).toBeVisible();
  const persistedPreferences = await page.evaluate(() => Object.entries(localStorage));
  expect(persistedPreferences).toContainEqual(densityPreference);
  expect(persistedPreferences.find(([key]) =>
    key.startsWith("wes-ops-web:columns:v1:") && key.endsWith(":platform-settings:settings"))?.[1])
    .toContain('"fixedPositions":{"key":"right"}');
  expect(persistedPreferences.every(([key]) =>
    key.startsWith("wes-ops-web:columns:v1:") || key.startsWith("wes-ops-web:density:"))).toBe(true);
  expect(JSON.stringify(persistedPreferences)).not.toContain("accessToken");
  expect(await page.evaluate(() => sessionStorage.length)).toBe(0);
});
