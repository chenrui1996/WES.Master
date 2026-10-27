import { expect, test } from "@playwright/test";
import { readFile, writeFile } from "node:fs/promises";
import { join } from "node:path";

test("real WMS definitions and explicitly enabled creation retain current authority", async ({ page, context }) => {
  test.skip(!process.env.WES_E2E_WMS_COORDINATION, "Requires the explicit WMS/current-role fixture, not the Platform-only fixture.");
  const tenant = process.env.WES_E2E_TENANT;
  const warehouse = process.env.WES_E2E_WAREHOUSE_ID;
  const location = process.env.WES_E2E_LOCATION_ID;
  const coordination = process.env.WES_E2E_WMS_COORDINATION;
  const creation = process.env.WES_E2E_WMS_CREATE === "true";
  const locationCreation = process.env.WES_E2E_WMS_LOCATION_CREATE === "true";
  const locationPack = process.env.WES_E2E_WMS_LOCATION_PACK;
  const inventoryId = process.env.WES_E2E_INVENTORY_ID;
  const inboundId = process.env.WES_E2E_INBOUND_ID;
  const delivery = process.env.WES_E2E_WMS_DELIVERY;
  if (!["pending", "leased", "completed"].includes(delivery ?? "")) throw new Error("Requires explicit durable delivery-state fixture.");
  const pendingCount = delivery === "pending" ? 3 : delivery === "leased" ? 1 : 0;
  if (!tenant || !warehouse || !location || !coordination) throw new Error("Requires the controlled WMS/MySQL revocation fixture.");
  const failures: string[] = [];
  const methods: string[] = [];
  page.on("pageerror", error => failures.push(error.message));
  page.on("request", request => { if (new URL(request.url()).pathname.startsWith("/api/wms/")) methods.push(request.method()); });
  if (inboundId) {
    // Create before this scenario deliberately revokes parent warehouse read authority.
    const draftPage = await context.newPage();
    const draftMethods: string[] = [];
    draftPage.on("pageerror", error => failures.push(error.message));
    draftPage.on("request", request => { if (new URL(request.url()).pathname.startsWith("/api/wms/")) draftMethods.push(request.method()); });
    await draftPage.goto("/login");
    await draftPage.getByLabel("租户标识").fill(tenant);
    await draftPage.getByLabel("账号", { exact: true }).fill("fixture");
    await draftPage.getByLabel("密码", { exact: true }).fill("WES reference fixture password");
    await draftPage.getByRole("button", { name: "登录", exact: true }).click();
    await expect(draftPage.getByRole("heading", { name: "会话与安全", exact: true })).toBeVisible();
    await draftPage.getByTestId("navigation-toggle").click();
    await draftPage.getByRole("navigation", { name: "管理导航" }).getByRole("link", { name: "入库收货详情", exact: true }).click();
    await draftPage.getByRole("button", { name: "准备入库草稿", exact: true }).click();
    await draftPage.getByLabel("新入库单号", { exact: true }).fill("browser-in");
    await draftPage.getByLabel("计划仓库 UUID", { exact: true }).fill(warehouse);
    await draftPage.getByLabel("物料 UUID", { exact: true }).fill(warehouse);
    await draftPage.getByLabel("基础单位 UUID", { exact: true }).fill(location);
    await draftPage.getByLabel("计划数量", { exact: true }).fill("5");
    await draftPage.getByRole("button", { name: "增加计划行", exact: true }).click();
    const secondLine = draftPage.getByRole("group", { name: "计划行 2", exact: true });
    await secondLine.getByLabel("物料 UUID", { exact: true }).fill(location);
    await secondLine.getByLabel("基础单位 UUID", { exact: true }).fill(warehouse);
    await secondLine.getByLabel("单位精度", { exact: true }).selectOption("6");
    await secondLine.getByLabel("计划数量", { exact: true }).fill("0.000001");
    await draftPage.getByRole("button", { name: "预览入库草稿", exact: true }).click();
    await expect(draftPage.getByTestId("inbound-draft-preview")).toContainText("BROWSER-IN");
    expect(draftMethods).toEqual([]);
    for (const width of [375, 768, 1440]) {
      await draftPage.setViewportSize({ width, height: 900 });
      expect(await draftPage.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true);
    }
    await draftPage.setViewportSize({ width: 375, height: 900 });
    await draftPage.evaluate(() => { document.documentElement.style.fontSize = "200%"; });
    expect(await draftPage.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true);
    await draftPage.evaluate(() => { document.documentElement.style.fontSize = ""; });
    const createdResponse = draftPage.waitForResponse(response => new URL(response.url()).pathname === "/api/wms/inbound" && response.request().method() === "POST");
    await draftPage.getByTestId("confirm-inbound-draft").focus(); await draftPage.keyboard.press("Enter");
    const createdDraft = await createdResponse;
    expect(createdDraft.status()).toBe(201);
    const draftReceipt = await createdDraft.json();
    expect(Object.keys(draftReceipt)).toEqual(["decision", "id"]);
    const draftRead = draftPage.waitForResponse(response => new URL(response.url()).pathname === `/api/wms/inbound/${draftReceipt.id}`);
    await draftPage.getByTestId("read-created-inbound").click(); expect((await draftRead).status()).toBe(200);
    await expect(draftPage.getByTestId("inbound-detail")).toContainText("BROWSER-IN");
    await expect(draftPage.getByTestId("inbound-detail")).toContainText("0.000001");
    expect(draftMethods).toEqual(["POST", "GET"]);
    await draftPage.getByRole("button", { name: "关闭草稿面板", exact: true }).click();
    await draftPage.getByRole("button", { name: "准备修订入库草稿", exact: true }).click();
    const revisionFirst = draftPage.getByRole("group", { name: "计划行 1", exact: true });
    await revisionFirst.getByLabel("计划数量", { exact: true }).fill("7");
    await draftPage.getByRole("button", { name: "预览入库草稿", exact: true }).click();
    await expect(draftPage.getByTestId("inbound-draft-preview")).toContainText(draftReceipt.id);
    expect(draftMethods).toEqual(["POST", "GET"]);
    for (const width of [375, 768, 1440]) {
      await draftPage.setViewportSize({ width, height: 900 });
      expect(await draftPage.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true);
    }
    await draftPage.setViewportSize({ width: 375, height: 900 });
    await draftPage.evaluate(() => { document.documentElement.style.fontSize = "200%"; });
    expect(await draftPage.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true);
    await draftPage.evaluate(() => { document.documentElement.style.fontSize = ""; });
    const revisionResponse = draftPage.waitForResponse(response => new URL(response.url()).pathname === `/api/wms/inbound/${draftReceipt.id}/draft`
      && response.request().method() === "PUT");
    await draftPage.getByTestId("confirm-inbound-draft").focus(); await draftPage.keyboard.press("Enter");
    const revision = await revisionResponse;
    expect(revision.status()).toBe(200);
    const revisionReceipt = await revision.json();
    expect(Object.keys(revisionReceipt)).toEqual(["decision", "id", "version"]);
    expect(revisionReceipt.id).toBe(draftReceipt.id);
    await expect(draftPage.getByText(`原修订版本：${revisionReceipt.version}`, { exact: false })).toBeVisible();
    const revisionRead = draftPage.waitForResponse(response => new URL(response.url()).pathname === `/api/wms/inbound/${draftReceipt.id}`
      && response.request().method() === "GET");
    await draftPage.getByTestId("read-created-inbound").click(); expect((await revisionRead).status()).toBe(200);
    await expect(draftPage.getByTestId("inbound-detail")).toContainText("7.000000");
    await expect(draftPage.getByTestId("inbound-detail")).toContainText("0.000001");
    expect(draftMethods).toEqual(["POST", "GET", "PUT", "GET"]);
    const draftStored = await draftPage.evaluate(() => JSON.stringify([Object.entries(localStorage), Object.entries(sessionStorage)]));
    expect(draftStored).not.toContain("BROWSER-IN"); expect(draftStored).not.toContain(draftReceipt.id);
    await draftPage.close();
  }
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
  if (locationCreation) {
    if (!locationPack) throw new Error("Requires explicitly assembled test-only location schema.");
    await page.getByRole("button", { name: "新建禁用库位", exact: true }).click();
    await expect(details).toHaveCount(0);
    await page.getByLabel("新库位代码", { exact: true }).fill("browser-loc");
    await page.getByLabel("新库位名称", { exact: true }).fill("Browser location");
    await page.getByLabel("父仓 UUID（提交时复验当前读取权）", { exact: true }).fill(warehouse);
    await page.getByLabel("固定 Pack UUID", { exact: true }).fill(locationPack);
    await page.getByLabel("固定 schema 版本", { exact: true }).fill("v1");
    await page.keyboard.press("Tab");
    await expect(page.getByRole("button", { name: "预览禁用库位创建", exact: true })).toBeFocused();
    expect(await page.getByRole("button", { name: "预览禁用库位创建", exact: true }).evaluate(element => element.matches(":focus-visible"))).toBe(true);
    await page.keyboard.press("Enter");
    const preview = page.getByTestId("location-preview");
    const operation = await preview.locator("dd").nth(3).innerText();
    await expect(preview).toContainText(warehouse);
    await expect(preview).toContainText(locationPack);
    for (const width of [375, 768, 1440]) {
      await page.setViewportSize({ width, height: 900 });
      await page.evaluate(() => { document.documentElement.style.fontSize = "200%"; });
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true);
      await page.evaluate(() => { document.documentElement.style.fontSize = ""; });
    }
    const response = page.waitForResponse(item => new URL(item.url()).pathname === "/api/wms/locations" && item.request().method() === "POST");
    await page.getByRole("button", { name: "确认创建禁用库位", exact: true }).click();
    const createdResponse = await response;
    expect(createdResponse.status()).toBe(201);
    expect(createdResponse.headers()["cache-control"]).toBe("no-store");
    expect(createdResponse.request().headers()["idempotency-key"]).toBe(operation);
    expect(createdResponse.request().postDataJSON()).toEqual({ code: "BROWSER-LOC", name: "Browser location",
      warehouseId: warehouse, packId: locationPack, schemaVersion: "v1" });
    const created = await createdResponse.json() as { decision: number; id: string; version: string };
    expect(created.decision).toBe(0);
    await expect(page.getByTestId("location-receipt")).toContainText("已确认禁用库位");
    const readCreated = page.waitForResponse(item => item.url().endsWith(`/api/wms/locations/${created.id}`));
    await page.getByRole("button", { name: "用回执 UUID 读取当前库位", exact: true }).click();
    expect((await readCreated).status()).toBe(200);
    await expect(details).toContainText("BROWSER-LOC");
    await expect(details).toContainText(created.version);
    await expect(details).toContainText("主数据禁用");
    await page.getByLabel("资源 UUID", { exact: true }).fill(location);
    const restore = page.waitForResponse(item => item.url().endsWith(`/api/wms/locations/${location}`));
    await submit.click(); expect((await restore).status()).toBe(200);
  } else {
    await expect(page.getByRole("button", { name: "新建禁用库位", exact: true })).toHaveCount(0);
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
  const countRead = page.getByRole("button", { name: "读取 Wms 计数", exact: true });
  await expect(countRead).toBeFocused();
  const countResponse = page.waitForResponse(response => new URL(response.url()).pathname === "/api/wms/master-data-events/summary");
  await page.keyboard.press("Enter");
  const count = await countResponse;
  expect(count.status()).toBe(200); expect(count.headers()["cache-control"]).toBe("no-store");
  const summary = await count.json() as { owner: string; observedAt: string; pendingCount: number };
  expect(Object.keys(summary)).toEqual(["owner", "observedAt", "pendingCount"]);
  expect(summary.owner).toBe("Wms"); expect(summary.pendingCount).toBe(pendingCount);
  const countDetails = page.getByTestId("message-counts");
  await expect(countDetails).toContainText(summary.observedAt);
  await expect(countDetails).toContainText(`待发布${pendingCount}`);
  await expect(countDetails).not.toContainText("Inbox");
  await page.keyboard.press("Tab");
  const eventRead = page.getByRole("button", { name: "读取 WMS 创建事件", exact: true });
  await expect(eventRead).toBeFocused();
  expect(await eventRead.evaluate(element => element.matches(":focus-visible"))).toBe(true);
  const eventsResponse = page.waitForResponse(response => new URL(response.url()).pathname === "/api/wms/master-data-events");
  await page.keyboard.press("Enter");
  const events = await eventsResponse;
  expect(events.status()).toBe(200); expect(events.headers()["cache-control"]).toBe("no-store");
  const eventRows = (await events.json() as { rows: { eventId: string; aggregateId: string; occurredAt: string; status: number }[] }).rows;
  expect(eventRows).toHaveLength(creation || locationCreation ? 3 : 2);
  expect(eventRows.map(row => row.status).sort()).toEqual(delivery === "pending" ? [0, 0, 0] : delivery === "leased" ? [0, 1] : [2, 3]);
  const eventList = page.getByTestId("wms-events");
  await expect(eventList).toContainText(warehouse); await expect(eventList).toContainText(location);
  for (const row of eventRows) {
    expect(Object.keys(row)).toEqual(["eventId", "owner", "contractName", "schemaVersion", "aggregateId", "aggregateVersion", "correlationId", "occurredAt", "status"]);
    await expect(eventList).toContainText(row.eventId);
    await expect(eventList).toContainText(row.occurredAt);
    await expect(eventList.locator("li").filter({ hasText: row.eventId }).getByRole("heading")).toHaveText(
      ["待发布（Pending）", "投递中（Leased）", "已确认投递（Published）", "已隔离（Quarantined）"][row.status]!);
  }
  await expect(page.getByText("状态只反映耐久投递", { exact: false })).toBeVisible();
  for (const width of [375, 768, 1440]) {
    await page.setViewportSize({ width, height: 900 });
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true);
  }
  await page.evaluate(() => { document.documentElement.style.fontSize = "200%"; });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true);
  await page.evaluate(() => { document.documentElement.style.fontSize = ""; });
  await context.setOffline(true); await expect(eventRead).toBeDisabled(); await expect(countRead).toBeDisabled();
  const beforeEventsReconnect = methods.length;
  await context.setOffline(false); await expect(eventRead).toBeEnabled();
  expect(methods.length).toBe(beforeEventsReconnect);
  await expect(page.getByRole("region", { name: "WMS 创建事件", exact: true })).toContainText("陈旧");
  await expect(page.getByRole("region", { name: "Wms 消息计数", exact: true })).toContainText("陈旧");
  await writeFile(join(coordination, "events-revoke.request"), "revoke");
  await expect.poll(async () => readFile(join(coordination, "events-revoke.confirmed"), "utf8").catch((error: NodeJS.ErrnoException) => {
    if (error.code === "ENOENT") return "pending";
    throw error;
  })).toBe("revoked");
  const deniedEvents = page.waitForResponse(response => new URL(response.url()).pathname === "/api/wms/master-data-events");
  await eventRead.click(); expect((await deniedEvents).status()).toBe(403);
  await expect(eventList).toHaveCount(0);
  await expect(page.getByRole("region", { name: "WMS 创建事件", exact: true })).toContainText("PermissionDenied");
  const deniedCounts = page.waitForResponse(response => new URL(response.url()).pathname === "/api/wms/master-data-events/summary");
  await countRead.click(); expect((await deniedCounts).status()).toBe(403);
  await expect(countDetails).toHaveCount(0);
  await expect(page.getByRole("region", { name: "Wms 消息计数", exact: true })).toContainText("PermissionDenied");
  if (inventoryId) {
    await page.getByTestId("navigation-toggle").click();
    await page.getByRole("navigation", { name: "管理导航" }).getByRole("link", { name: "库存与保护量", exact: true }).click();
    await expect(page.getByRole("heading", { name: "库存与保护量", exact: true })).toBeVisible();
    const inventoryRead = page.getByRole("button", { name: "读取当前库存", exact: true });
    await page.getByLabel("批次筛选", { exact: true }).focus();
    await page.keyboard.press("Tab"); await expect(inventoryRead).toBeFocused();
    expect(await inventoryRead.evaluate(element => element.matches(":focus-visible"))).toBe(true);
    const readInventory = page.waitForResponse(response => new URL(response.url()).pathname === "/api/wms/inventory");
    await page.keyboard.press("Enter");
    const response = await readInventory;
    expect(response.status()).toBe(200); expect(response.headers()["cache-control"]).toBe("no-store");
    const inventoryRows = page.getByTestId("inventory-rows");
    await expect(inventoryRows).toContainText(inventoryId);
    await expect(inventoryRows).toContainText("999999999.999999");
    const json = await response.json() as { rows: { quantity: string; available: string; protected: string }[] };
    expect(json.rows[0]?.quantity).toBe("999999999.999999");
    expect(json.rows[0]?.available).toBe("999999999.999999");
    expect(json.rows[0]?.protected).toBe("0.000000");
    for (const width of [375, 768, 1440]) {
      await page.setViewportSize({ width, height: 900 });
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true);
    }
    await page.evaluate(() => { document.documentElement.style.fontSize = "200%"; });
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true);
    await page.evaluate(() => { document.documentElement.style.fontSize = ""; });
    await context.setOffline(true); await expect(inventoryRead).toBeDisabled();
    const beforeInventoryReconnect = methods.length;
    await context.setOffline(false); await expect(inventoryRead).toBeEnabled();
    expect(methods.length).toBe(beforeInventoryReconnect);
    await expect(page.getByRole("region", { name: "当前库存", exact: true })).toContainText("陈旧");
    await writeFile(join(coordination, "inventory-revoke.request"), "revoke");
    await expect.poll(async () => readFile(join(coordination, "inventory-revoke.confirmed"), "utf8").catch((error: NodeJS.ErrnoException) => {
      if (error.code === "ENOENT") return "pending";
      throw error;
    })).toBe("revoked");
    const deniedInventory = page.waitForResponse(value => new URL(value.url()).pathname === "/api/wms/inventory");
    await inventoryRead.click(); expect((await deniedInventory).status()).toBe(403);
    await expect(inventoryRows).toHaveCount(0);
    await expect(page.getByRole("region", { name: "当前库存", exact: true })).toContainText("PermissionDenied");
    const beforeLedger = methods.length;
    await page.getByTestId("navigation-toggle").click();
    await page.getByRole("navigation", { name: "管理导航" }).getByRole("link", { name: "库存流水", exact: true }).click();
    await expect(page.getByRole("heading", { name: "库存流水", exact: true })).toBeVisible();
    expect(methods.length).toBe(beforeLedger);
    const ledgerRead = page.getByRole("button", { name: "读取当前流水", exact: true });
    await page.getByLabel("结束 UTC（不包含）", { exact: true }).focus();
    await page.keyboard.press("Tab"); await expect(ledgerRead).toBeFocused();
    expect(await ledgerRead.evaluate(element => element.matches(":focus-visible"))).toBe(true);
    const readLedger = page.waitForResponse(value => new URL(value.url()).pathname === "/api/wms/ledger");
    await page.keyboard.press("Enter");
    const ledgerResponse = await readLedger;
    expect(ledgerResponse.status()).toBe(200); expect(ledgerResponse.headers()["cache-control"]).toBe("no-store");
    const ledgerJson = await ledgerResponse.json() as { rows: Record<string, unknown>[]; offset: number; limit: number };
    expect(Object.keys(ledgerJson)).toEqual(["rows", "offset", "limit"]);
    expect(ledgerJson.rows).toHaveLength(1);
    const fact = ledgerJson.rows[0]!;
    expect(Object.keys(fact)).toHaveLength(19);
    expect(fact.balanceId).toBe(inventoryId); expect(fact.kind).toBe(0);
    expect(fact.quantity).toBe("999999999.999999"); expect(fact.before).toBe("0.000000"); expect(fact.after).toBe("999999999.999999");
    const ledgerRows = page.getByTestId("stock-ledger-rows");
    for (const key of ["id", "postingId", "balanceId", "sourceDocumentId", "sourceLineId", "executionUnitId", "correlationId", "occurredAt"])
      await expect(ledgerRows).toContainText(String(fact[key]));
    for (const width of [375, 768, 1440]) {
      await page.setViewportSize({ width, height: 900 });
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true);
      await page.evaluate(() => { document.documentElement.style.fontSize = "200%"; });
      const overflow = await page.evaluate(() => [...document.querySelectorAll("*")].filter(element =>
        element.getBoundingClientRect().right > document.documentElement.clientWidth + 1).map(element => ({
          tag: element.tagName, class: element.className, name: element.getAttribute("name"),
          right: element.getBoundingClientRect().right, width: element.getBoundingClientRect().width,
        })).slice(0, 20));
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth), JSON.stringify({ width, overflow })).toBe(true);
      await page.evaluate(() => { document.documentElement.style.fontSize = ""; });
    }
    await page.evaluate(() => { document.documentElement.style.fontSize = "200%"; });
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true);
    await page.evaluate(() => { document.documentElement.style.fontSize = ""; });
    await context.setOffline(true); await expect(ledgerRead).toBeDisabled();
    const beforeLedgerReconnect = methods.length;
    await context.setOffline(false); await expect(ledgerRead).toBeEnabled();
    expect(methods.length).toBe(beforeLedgerReconnect);
    await expect(page.getByRole("region", { name: "当前库存流水", exact: true })).toContainText("陈旧");
    await writeFile(join(coordination, "ledger-revoke.request"), "revoke");
    await expect.poll(async () => readFile(join(coordination, "ledger-revoke.confirmed"), "utf8").catch((error: NodeJS.ErrnoException) => {
      if (error.code === "ENOENT") return "pending";
      throw error;
    })).toBe("revoked");
    const deniedLedger = page.waitForResponse(value => new URL(value.url()).pathname === "/api/wms/ledger");
    await ledgerRead.click(); expect((await deniedLedger).status()).toBe(403);
    await expect(ledgerRows).toHaveCount(0);
    await expect(page.getByRole("region", { name: "当前库存流水", exact: true })).toContainText("PermissionDenied");
    const saved = await page.evaluate(() => JSON.stringify([Object.entries(localStorage), Object.entries(sessionStorage)]));
    for (const key of ["id", "postingId", "sourceDocumentId", "sourceLineId", "executionUnitId", "correlationId"])
      expect(saved).not.toContain(String(fact[key]));
  }
  if (inboundId) {
    const navigation = page.getByRole("navigation", { name: "管理导航" });
    if (!await navigation.isVisible()) await page.getByTestId("navigation-toggle").click();
    await navigation.getByRole("link", { name: "入库收货详情", exact: true }).click();
    await expect(page.getByRole("heading", { name: "入库收货详情", exact: true })).toBeVisible();
    const read = page.getByRole("button", { name: "读取当前入库单", exact: true });
    await page.getByLabel("入库状态筛选", { exact: true }).focus(); await page.keyboard.press("Tab");
    const catalogRead = page.getByRole("button", { name: "读取入库目录", exact: true });
    await expect(catalogRead).toBeFocused();
    const catalogResponse = page.waitForResponse(response => new URL(response.url()).pathname === "/api/wms/inbound" && response.request().method() === "GET");
    await page.keyboard.press("Enter"); expect((await catalogResponse).status()).toBe(200);
    await expect(page.getByTestId("inbound-catalog")).toContainText("IN-HTTP");
    const select = page.getByRole("button", { name: "读取入库单 IN-HTTP 当前详情", exact: true });
    await page.keyboard.press("Tab"); await page.keyboard.press("Tab");
    await expect(select).toBeFocused();
    expect(await select.evaluate(element => element.matches(":focus-visible"))).toBe(true);
    const readResponse = page.waitForResponse(response => new URL(response.url()).pathname === `/api/wms/inbound/${inboundId}`);
    await page.keyboard.press("Enter");
    const actual = await readResponse;
    expect(actual.status()).toBe(200);
    expect(actual.headers()["cache-control"]).toBe("no-store");
    await expect(page.getByLabel("入库单 UUID", { exact: true })).toHaveValue(inboundId);
    const inbound = page.getByTestId("inbound-detail");
    for (const text of ["IN-HTTP", "999999999.999999", "0.000001", "999999999.999998", "尚未关联，待运输", "不能视为已到位"])
      await expect(inbound).toContainText(text);
    for (const width of [375, 768, 1440]) {
      await page.setViewportSize({ width, height: 900 });
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true);
    }
    await page.setViewportSize({ width: 375, height: 900 });
    await page.evaluate(() => { document.documentElement.style.fontSize = "200%"; });
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true);
    await page.evaluate(() => { document.documentElement.style.fontSize = ""; });
    await context.setOffline(true); await expect(read).toBeDisabled();
    await expect(page.getByRole("region", { name: "入库详情", exact: true })).toContainText("陈旧");
    const before = methods.length;
    await context.setOffline(false); await expect(read).toBeEnabled(); expect(methods.length).toBe(before);
    await writeFile(join(coordination, "inbound-revoke.request"), "revoke");
    await expect.poll(async () => readFile(join(coordination, "inbound-revoke.confirmed"), "utf8").catch((error: NodeJS.ErrnoException) => {
      if (error.code === "ENOENT") return "pending";
      throw error;
    })).toBe("revoked");
    const denied = page.waitForResponse(response => new URL(response.url()).pathname === `/api/wms/inbound/${inboundId}`);
    await read.click(); expect((await denied).status()).toBe(403);
    await expect(inbound).toHaveCount(0);
    await expect(page.getByRole("region", { name: "入库详情", exact: true })).toContainText("PermissionDenied");
  }
  const stored = await page.evaluate(() => JSON.stringify([Object.entries(localStorage), Object.entries(sessionStorage)]));
  for (const secret of [warehouse, location, locationDefinition.packId, "HTTP location", "BROWSER-WH", "Browser warehouse", "accessToken"])
    expect(stored).not.toContain(secret);
  for (const row of eventRows) expect(stored).not.toContain(row.eventId);
  if (inventoryId) expect(stored).not.toContain(inventoryId);
  if (inboundId) expect(stored).not.toContain(inboundId);
  expect(methods.length).toBe(creation || locationCreation ? 12 : inventoryId ? 13 : inboundId ? 12 : 9);
  expect(methods.filter(method => method === "POST")).toHaveLength(creation || locationCreation ? 1 : 0);
  expect(methods.every(method => method === "GET" || (creation || locationCreation) && method === "POST")).toBe(true);
  expect(failures).toEqual([]);
});
