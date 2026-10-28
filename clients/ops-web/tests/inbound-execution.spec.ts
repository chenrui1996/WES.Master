import { expect, test } from "@playwright/test";
import { readFile, writeFile } from "node:fs/promises";
import { join } from "node:path";

test("authorized approval and real receipt keep original Unknown identity and pending transport", async ({ page, context }) => {
  test.skip(!process.env.WES_E2E_EXECUTION_COORDINATION, "Requires the isolated authorized execution/MySQL fixture.");
  const tenant = process.env.WES_E2E_TENANT; const order = process.env.WES_E2E_EXECUTION_ORDER;
  const container = process.env.WES_E2E_EXECUTION_CONTAINER; const source = process.env.WES_E2E_EXECUTION_SOURCE;
  const target = process.env.WES_E2E_EXECUTION_TARGET; const balance = process.env.WES_E2E_EXECUTION_BALANCE;
  const balanceVersion = process.env.WES_E2E_EXECUTION_BALANCE_VERSION; const coordination = process.env.WES_E2E_EXECUTION_COORDINATION;
  if (!tenant || !order || !container || !source || !target || !balance || !balanceVersion || !coordination)
    throw new Error("Requires complete isolated execution identities and current role revocation fixture.");
  const failures: string[] = []; const methods: string[] = [];
  page.on("pageerror", error => failures.push(error.message));
  page.on("request", request => { if (new URL(request.url()).pathname.startsWith("/api/wms/")) methods.push(request.method()); });
  await page.goto("/login");
  await page.getByLabel("租户标识").fill(tenant); await page.getByLabel("账号", { exact: true }).fill("fixture");
  await page.getByLabel("密码", { exact: true }).fill("WES reference fixture password");
  await page.getByRole("button", { name: "登录", exact: true }).click();
  await expect(page.getByRole("heading", { name: "会话与安全", exact: true })).toBeVisible();
  await page.getByTestId("navigation-toggle").click();
  await page.getByRole("navigation", { name: "管理导航" }).getByRole("link", { name: "入库收货详情", exact: true }).click();
  await page.getByLabel("入库单 UUID", { exact: true }).fill(order);
  const initialRead = page.waitForResponse(response => new URL(response.url()).pathname === `/api/wms/inbound/${order}`);
  await page.getByRole("button", { name: "读取当前入库单", exact: true }).click();
  const original = await initialRead; expect(original.status()).toBe(200);
  const initial = await original.json();
  await page.getByTestId("prepare-inbound-approval").click();
  await page.getByRole("button", { name: "预览入库审核", exact: true }).click();
  await expect(page.getByTestId("inbound-execution-preview")).toContainText(initial.version);
  expect(methods).toEqual(["GET"]);
  const approvalResponse = page.waitForResponse(response => new URL(response.url()).pathname.endsWith("/approval"));
  await page.getByTestId("confirm-inbound-execution").focus(); await page.keyboard.press("Enter");
  const approval = await approvalResponse; expect(approval.status()).toBe(201);
  expect(approval.request().postDataJSON()).toEqual({ expectedVersion: initial.version });
  const approved = await approval.json(); expect(Object.keys(approved)).toEqual(["decision", "id", "version"]);
  const approvedRead = page.waitForResponse(response => new URL(response.url()).pathname === `/api/wms/inbound/${order}`);
  await page.getByTestId("read-executed-inbound").click(); expect((await approvedRead).status()).toBe(200);
  await page.getByTestId("prepare-inbound-receipt").click();
  await page.getByLabel("本次收货数量", { exact: true }).fill("5.000");
  for (const [label, value] of [["合法容器 UUID", container], ["收货位 UUID", source], ["目标位 UUID", target],
    ["合法收货余额 UUID", balance], ["原余额版本 UUID", balanceVersion]] as const) await page.getByLabel(label, { exact: true }).fill(value);
  await page.getByRole("button", { name: "预览入库收货", exact: true }).click();
  const preview = page.getByTestId("inbound-execution-preview");
  for (const text of [order, initial.lines[0].id, initial.lines[0].itemId, initial.lines[0].unitId, approved.version,
    balanceVersion, container, source, target, balance, "5.000", "仍待运输"]) await expect(preview).toContainText(text);
  expect(methods).toEqual(["GET", "POST", "GET"]);
  for (const width of [375, 768, 1440]) {
    await page.setViewportSize({ width, height: 900 });
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true);
  }
  await page.setViewportSize({ width: 375, height: 900 });
  await page.evaluate(() => { document.documentElement.style.fontSize = "200%"; });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true);
  await page.evaluate(() => { document.documentElement.style.fontSize = ""; });
  let originalKey = ""; let originalBody = "";
  await page.route(`**/api/wms/inbound/${order}/receipts`, async route => {
    if (originalKey) { await route.continue(); return; }
    originalKey = route.request().headers()["idempotency-key"]!; originalBody = route.request().postData()!;
    const response = await route.fetch(); expect(response.status()).toBe(201);
    await route.fulfill({ status: 503, contentType: "application/json", body: "{}" });
  });
  const interruptedReceipt = page.waitForResponse(response => new URL(response.url()).pathname.endsWith("/receipts") && response.status() === 503);
  await page.getByTestId("confirm-inbound-execution").focus(); await page.keyboard.press("Enter");
  expect((await interruptedReceipt).status()).toBe(503);
  const retry = page.getByTestId("retry-inbound-execution");
  await expect(retry).toBeVisible();
  await expect(page.getByLabel("入库单 UUID", { exact: true })).toHaveCount(0);
  await context.setOffline(true); await expect(retry).toBeDisabled();
  const before = methods.length;
  await context.setOffline(false); await expect(retry).toBeEnabled(); expect(methods.length).toBe(before);
  const recoveryResponse = page.waitForResponse(response => new URL(response.url()).pathname.endsWith("/receipts") && response.status() === 200);
  await retry.click(); const recovery = await recoveryResponse;
  expect(recovery.request().headers()["idempotency-key"]).toBe(originalKey);
  expect(recovery.request().postData()).toBe(originalBody); expect((await recovery.json()).decision).toBe(1);
  const receiptRead = page.waitForResponse(response => new URL(response.url()).pathname === `/api/wms/inbound/${order}`);
  await page.getByTestId("read-executed-inbound").click();
  const received = await receiptRead; expect(received.status()).toBe(200);
  const current = await received.json();
  expect(current.state).toBe(2); expect(current.lines[0].received).toBe("5.000000"); expect(current.lines[0].remaining).toBe("0.000000");
  expect(current.receipts).toHaveLength(1);
  expect(current.receipts[0]).toMatchObject({ containerId: container, receiptLocationId: source, targetLocationId: target,
    quantity: "5.000000", requiresTransport: true, arrived: false, transportId: null });
  await expect(page.getByTestId("inbound-detail")).toContainText("尚未关联，待运输");
  await writeFile(join(coordination, "revoke.request"), "revoke");
  await expect.poll(async () => readFile(join(coordination, "revoke.confirmed"), "utf8").catch((error: NodeJS.ErrnoException) => {
    if (error.code === "ENOENT") return "pending"; throw error;
  })).toBe("revoked");
  const denied = page.waitForResponse(response => new URL(response.url()).pathname === `/api/wms/inbound/${order}`);
  await page.getByRole("button", { name: "读取当前入库单", exact: true }).click(); expect((await denied).status()).toBe(403);
  await expect(page.getByTestId("inbound-detail")).toHaveCount(0);
  const stored = await page.evaluate(() => JSON.stringify([Object.entries(localStorage), Object.entries(sessionStorage)]));
  for (const value of [order, container, source, target, balance, balanceVersion, originalKey, "accessToken"]) expect(stored).not.toContain(value);
  expect(methods).toEqual(["GET", "POST", "GET", "POST", "POST", "GET", "GET"]);
  expect(failures).toEqual([]);
});
