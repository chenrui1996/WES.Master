import { expect, test } from "@playwright/test";
import { readFile, writeFile } from "node:fs/promises";
import { join } from "node:path";

test("real WMS definitions and explicitly enabled warehouse creation retain current authority", async ({ page, context }) => {
  test.skip(!process.env.WES_E2E_WMS_COORDINATION, "Requires the explicit WMS/current-role fixture, not the Platform-only fixture.");
  const tenant = process.env.WES_E2E_TENANT;
  const warehouse = process.env.WES_E2E_WAREHOUSE_ID;
  const location = process.env.WES_E2E_LOCATION_ID;
  const coordination = process.env.WES_E2E_WMS_COORDINATION;
  const creation = process.env.WES_E2E_WMS_CREATE === "true";
  if (!tenant || !warehouse || !location || !coordination) throw new Error("Requires the controlled WMS/MySQL revocation fixture.");
  const failures: string[] = [];
  const methods: string[] = [];
  page.on("pageerror", error => failures.push(error.message));
  page.on("request", request => { if (new URL(request.url()).pathname.startsWith("/api/wms/")) methods.push(request.method()); });
  await page.goto("/login");
  await page.getByLabel("租户标识").fill(tenant);
  await page.getByLabel("账号", { exact: true }).fill("fixture");
  await page.getByLabel("密码", { exact: true }).fill("WES reference fixture password");
  await page.getByRole("button", { name: "登录", exact: true }).click();
  await expect(page.getByRole("heading", { name: "会话与安全", exact: true })).toBeVisible();
  await page.getByTestId("navigation-toggle").click();
  await page.getByRole("navigation", { name: "管理导航" }).getByRole("link", { name: "仓库与库位详情", exact: true }).click();
  await expect(page.getByRole("heading", { name: "仓库与库位详情", exact: true })).toBeVisible();
  expect(methods).toEqual([]);
  const catalogWarehouse = page.waitForResponse(response => new URL(response.url()).pathname === "/api/wms/warehouses");
  await page.getByRole("button", { name: "读取目录", exact: true }).click();
  expect((await catalogWarehouse).status()).toBe(200);
  await expect(page.getByTestId("master-data-catalog")).toContainText("WH-HTTP");
  await page.getByLabel("资源 UUID", { exact: true }).fill(warehouse);
  await page.keyboard.press("Tab");
  const submit = page.getByRole("button", { name: "读取当前详情", exact: true });
  await expect(submit).toBeFocused();
  expect(await submit.evaluate(element => element.matches(":focus-visible"))).toBe(true);
  const readWarehouse = page.waitForResponse(response => response.url().endsWith(`/api/wms/warehouses/${warehouse}`));
  await page.keyboard.press("Enter");
  const warehouseResponse = await readWarehouse;
  expect(warehouseResponse.status()).toBe(200);
  expect(warehouseResponse.headers()["cache-control"]).toBe("no-store");
  const warehouseDefinition = await warehouseResponse.json() as { version: string; createdAt: string };
  const details = page.getByTestId("master-data-details");
  await expect(details).toContainText("HTTP warehouse");
  await expect(details).toContainText(warehouseDefinition.version);
  await expect(details).toContainText(warehouseDefinition.createdAt);
  await expect(details).toContainText("主数据禁用");
  await expect(page.getByTestId("master-data-catalog")).toBeVisible();
  await page.getByLabel("资源类型", { exact: true }).selectOption("locations");
  await expect(details).toHaveCount(0);
  await expect(page.getByTestId("master-data-catalog")).toHaveCount(0);
  const catalogLocation = page.waitForResponse(response => new URL(response.url()).pathname === "/api/wms/locations");
  await page.getByRole("button", { name: "读取目录", exact: true }).click();
  expect((await catalogLocation).status()).toBe(200);
  await expect(page.getByTestId("master-data-catalog")).toContainText("A1");
  const readLocation = page.waitForResponse(response => response.url().endsWith(`/api/wms/locations/${location}`));
  await page.getByLabel("固定仓库 UUID 筛选", { exact: true }).click();
  await page.keyboard.press("Tab");
  await page.keyboard.press("Tab");
  await expect(page.getByRole("button", { name: "读取库位 A1 当前详情", exact: true })).toBeFocused();
  expect(await page.getByRole("button", { name: "读取库位 A1 当前详情", exact: true }).evaluate(element => element.matches(":focus-visible"))).toBe(true);
  await page.keyboard.press("Enter");
  const locationResponse = await readLocation;
  expect(locationResponse.status()).toBe(200);
  const locationDefinition = await locationResponse.json() as { packId: string; schemaVersion: string; version: string };
  await expect(details).toContainText("HTTP location");
  await expect(details).toContainText(warehouse);
  await expect(details).toContainText(locationDefinition.packId);
  await expect(details).toContainText(locationDefinition.schemaVersion);
  await expect(details).toContainText(locationDefinition.version);
  await expect(page.getByLabel("资源 UUID", { exact: true })).toHaveValue(location);
  for (const width of [375, 768, 1440]) {
    await page.setViewportSize({ width, height: 900 });
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true);
    await expect(submit).toBeVisible();
  }
  await page.evaluate(() => { document.documentElement.style.fontSize = "200%"; });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true);
  await page.evaluate(() => { document.documentElement.style.fontSize = ""; });
  await context.setOffline(true);
  await expect(submit).toBeDisabled();
  await expect(page.getByRole("region", { name: "主数据详情", exact: true })).toContainText("陈旧");
  const beforeReconnect = methods.length;
  await context.setOffline(false);
  await expect(submit).toBeEnabled();
  expect(methods.length).toBe(beforeReconnect);
  await expect(page.getByRole("region", { name: "主数据详情", exact: true })).toContainText("陈旧");

  if (creation) {
    await page.getByRole("button", { name: "新建禁用仓库", exact: true }).click();
    await expect(details).toHaveCount(0);
    await page.getByLabel("新仓库代码", { exact: true }).fill("browser-wh");
    await page.getByLabel("新仓库名称", { exact: true }).fill("Browser warehouse");
    await page.keyboard.press("Tab");
    await expect(page.getByRole("button", { name: "预览禁用仓库创建", exact: true })).toBeFocused();
    await page.keyboard.press("Enter");
    const preview = page.getByTestId("warehouse-preview");
    await expect(preview).toContainText("BROWSER-WH");
    const operation = await preview.locator("dd").nth(3).innerText();
    expect(operation).toMatch(/^[a-f0-9-]{36}$/);
    for (const width of [375, 768, 1440]) {
      await page.setViewportSize({ width, height: 900 });
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true);
    }
    await page.evaluate(() => { document.documentElement.style.fontSize = "200%"; });
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true);
    await page.evaluate(() => { document.documentElement.style.fontSize = ""; });
    const createResponse = page.waitForResponse(response => new URL(response.url()).pathname === "/api/wms/warehouses" && response.request().method() === "POST");
    await page.getByRole("button", { name: "确认创建禁用仓库", exact: true }).click();
    const createdResponse = await createResponse;
    expect(createdResponse.status()).toBe(201);
    expect(createdResponse.headers()["cache-control"]).toBe("no-store");
    expect(createdResponse.request().headers()["idempotency-key"]).toBe(operation);
    expect(createdResponse.request().postDataJSON()).toEqual({ code: "BROWSER-WH", name: "Browser warehouse" });
    const created = await createdResponse.json() as { decision: number; id: string; version: string };
    expect(created.decision).toBe(0);
    await expect(page.getByTestId("warehouse-receipt")).toContainText("已确认禁用仓库");
    const readCreated = page.waitForResponse(response => response.url().endsWith(`/api/wms/warehouses/${created.id}`));
    await page.getByRole("button", { name: "用回执 UUID 读取当前仓库", exact: true }).click();
    expect((await readCreated).status()).toBe(200);
    await expect(details).toContainText("BROWSER-WH");
    await expect(details).toContainText("主数据禁用");
    await expect(details).toContainText(created.version);
    await page.getByLabel("资源类型", { exact: true }).selectOption("locations");
    await page.getByLabel("资源 UUID", { exact: true }).fill(location);
    const restore = page.waitForResponse(response => response.url().endsWith(`/api/wms/locations/${location}`));
    await submit.click();
    expect((await restore).status()).toBe(200);
  } else {
    await expect(page.getByRole("button", { name: "新建禁用仓库", exact: true })).toHaveCount(0);
  }

  // The host fixture changes the real role through a second DB owner, not a fake HTTP response.
  await writeFile(join(coordination, "revoke.request"), "revoke");
  await expect.poll(async () => readFile(join(coordination, "revoke.confirmed"), "utf8").catch((error: NodeJS.ErrnoException) => {
    if (error.code === "ENOENT") return "pending";
    throw error;
  })).toBe("revoked");
  const denied = page.waitForResponse(response => response.url().endsWith(`/api/wms/locations/${location}`));
  await submit.click();
  expect((await denied).status()).toBe(403);
  await expect(details).toHaveCount(0);
  await expect(page.getByTestId("master-data-catalog")).toHaveCount(0);
  await expect(page.getByRole("region", { name: "主数据详情", exact: true })).toContainText("PermissionDenied");
  await page.getByTestId("navigation-toggle").click();
  await page.getByRole("navigation", { name: "管理导航" }).getByRole("link", { name: "消息诊断", exact: true }).click();
  await expect(page.getByRole("heading", { name: "消息诊断 · WMS 创建事件", exact: true })).toBeVisible();
  const owner = page.getByLabel("消息 owner", { exact: true });
  await expect(owner).toHaveValue("Wms");
  await expect(owner.locator("option")).toHaveCount(1);
  await owner.focus(); await page.keyboard.press("Tab");
  const eventRead = page.getByRole("button", { name: "读取 WMS 创建事件", exact: true });
  await expect(eventRead).toBeFocused();
  expect(await eventRead.evaluate(element => element.matches(":focus-visible"))).toBe(true);
  const eventsResponse = page.waitForResponse(response => new URL(response.url()).pathname === "/api/wms/master-data-events");
  await page.keyboard.press("Enter");
  const events = await eventsResponse;
  expect(events.status()).toBe(200); expect(events.headers()["cache-control"]).toBe("no-store");
  const eventRows = (await events.json() as { rows: { eventId: string; aggregateId: string; occurredAt: string }[] }).rows;
  expect(eventRows).toHaveLength(creation ? 3 : 2);
  const eventList = page.getByTestId("wms-events");
  await expect(eventList).toContainText(warehouse); await expect(eventList).toContainText(location);
  for (const row of eventRows) {
    await expect(eventList).toContainText(row.eventId);
    await expect(eventList).toContainText(row.occurredAt);
  }
  for (const width of [375, 768, 1440]) {
    await page.setViewportSize({ width, height: 900 });
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true);
  }
  await page.evaluate(() => { document.documentElement.style.fontSize = "200%"; });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true);
  await page.evaluate(() => { document.documentElement.style.fontSize = ""; });
  await context.setOffline(true); await expect(eventRead).toBeDisabled();
  const beforeEventsReconnect = methods.length;
  await context.setOffline(false); await expect(eventRead).toBeEnabled();
  expect(methods.length).toBe(beforeEventsReconnect);
  await expect(page.getByRole("region", { name: "WMS 创建事件", exact: true })).toContainText("陈旧");
  await writeFile(join(coordination, "events-revoke.request"), "revoke");
  await expect.poll(async () => readFile(join(coordination, "events-revoke.confirmed"), "utf8").catch((error: NodeJS.ErrnoException) => {
    if (error.code === "ENOENT") return "pending";
    throw error;
  })).toBe("revoked");
  const deniedEvents = page.waitForResponse(response => new URL(response.url()).pathname === "/api/wms/master-data-events");
  await eventRead.click(); expect((await deniedEvents).status()).toBe(403);
  await expect(eventList).toHaveCount(0);
  await expect(page.getByRole("region", { name: "WMS 创建事件", exact: true })).toContainText("PermissionDenied");
  const stored = await page.evaluate(() => JSON.stringify([Object.entries(localStorage), Object.entries(sessionStorage)]));
  for (const secret of [warehouse, location, locationDefinition.packId, "HTTP location", "BROWSER-WH", "Browser warehouse", "accessToken"])
    expect(stored).not.toContain(secret);
  for (const row of eventRows) expect(stored).not.toContain(row.eventId);
  expect(methods.length).toBe(creation ? 10 : 7);
  expect(methods.filter(method => method === "POST")).toHaveLength(creation ? 1 : 0);
  expect(methods.every(method => method === "GET" || creation && method === "POST")).toBe(true);
  expect(failures).toEqual([]);
});
