import { expect, test, type Page } from "@playwright/test";

test("direct outbound reserves atomically, records shortage, requires another reviewer and recovers original shipment", async ({ page, context, browser, baseURL }) => {
  test.skip(process.env.WES_E2E_OUTBOUND_FIXTURE !== "1", "Requires the isolated outbound/MySQL HTTPS fixture.");
  test.setTimeout(180000);
  const tenant = process.env.WES_E2E_TENANT; const warehouse = process.env.WES_E2E_OUTBOUND_WAREHOUSE;
  const balance = process.env.WES_E2E_OUTBOUND_BALANCE; const balanceVersion = process.env.WES_E2E_OUTBOUND_BALANCE_VERSION;
  if (!tenant || !warehouse || !balance || !balanceVersion || !baseURL) throw new Error("Missing isolated outbound identities.");
  const failures: string[] = [];
  page.on("pageerror", error => failures.push(error.message));
  async function login(target: Page, account: string, navigation: string) {
    await target.goto("/login");
    await target.getByLabel("租户标识").fill(tenant!);
    await target.getByLabel("账号", { exact: true }).fill(account);
    await target.getByLabel("密码", { exact: true }).fill("WES reference fixture password");
    await target.getByRole("button", { name: "登录", exact: true }).click();
    await expect(target.getByRole("heading", { name: "会话与安全", exact: true })).toBeVisible();
    await target.getByTestId("navigation-toggle").click();
    await target.getByRole("navigation", { name: "管理导航" }).getByRole("link", { name: navigation, exact: true }).click();
  }
  async function confirm(target: Page, path: string, status = 201) {
    const response = target.waitForResponse(item => new URL(item.url()).pathname === path && item.request().method() === "POST");
    await target.getByTestId("confirm-outbound").focus(); await target.keyboard.press("Enter");
    const result = await response; expect(result.status()).toBe(status);
    return result;
  }
  async function readAfter(target: Page, order: string) {
    const response = target.waitForResponse(item => new URL(item.url()).pathname === `/api/wms/outbound/${order}` && item.request().method() === "GET");
    await target.getByTestId("read-outbound-after-write").click();
    const result = await response; expect(result.status()).toBe(200);
    return result.json();
  }
  await login(page, "fixture", "出库预留与发运");
  await page.getByTestId("prepare-outbound-create").click();
  await page.getByLabel("出库单号", { exact: true }).fill("OUT-BROWSER");
  await page.getByLabel("仓库 UUID", { exact: true }).fill(warehouse);
  await page.getByLabel("合法来源余额 UUID", { exact: true }).fill(balance);
  await page.getByLabel("计划数量", { exact: true }).fill("7");
  await page.getByRole("button", { name: "预览创建出库草稿", exact: true }).click();
  await expect(page.getByTestId("outbound-preview-body")).toContainText(balance);
  const created = await confirm(page, "/api/wms/outbound");
  const order = (await created.json()).id as string;
  const original = await readAfter(page, order);
  expect(original.record.state).toBe(0);
  const line = original.record.lines[0].id as string;
  await page.getByTestId("prepare-outbound-approval").click();
  await page.getByLabel(`来源余额 ${balance} 原版本 UUID`, { exact: true }).fill(balanceVersion);
  await page.getByRole("button", { name: "预览审核并全行预留", exact: true }).click();
  await confirm(page, `/api/wms/outbound/${order}/approval`);
  const approved = await readAfter(page, order);
  expect(approved.record.lines[0].reservedQuantity).toBe("7.000000");
  await page.getByTestId("prepare-outbound-picking").click();
  await page.getByLabel("本次新增实拣数量", { exact: true }).fill("5");
  await page.getByLabel("本次新增短缺数量", { exact: true }).fill("2");
  await page.getByLabel("事实说明 / 原因", { exact: true }).fill("实拣5，另2未执行待复核");
  await page.getByRole("button", { name: "预览确认实拣与短缺", exact: true }).click();
  await confirm(page, `/api/wms/outbound/${order}/picking`);
  const picked = await readAfter(page, order);
  expect(picked.record.lines[0]).toMatchObject({ plannedQuantity: "7.000000", reservedQuantity: "7.000000", pickedQuantity: "5.000000", shortageQuantity: "2.000000", shippedQuantity: "0.000000" });
  const reviewerContext = await browser.newContext({ baseURL, ignoreHTTPSErrors: true });
  try {
    const reviewer = await reviewerContext.newPage(); reviewer.on("pageerror", error => failures.push(error.message));
    await login(reviewer, "outbound-reviewer", "拣选与短缺复核");
    await reviewer.getByLabel("出库单 UUID", { exact: true }).fill(order);
    const reviewed = reviewer.waitForResponse(item => new URL(item.url()).pathname === `/api/wms/outbound/${order}`);
    await reviewer.getByRole("button", { name: "读取当前出库单", exact: true }).click(); expect((await reviewed).status()).toBe(200);
    await reviewer.getByTestId("prepare-outbound-shortage-release").click();
    await reviewer.getByLabel("确认未执行的短缺释放数量", { exact: true }).fill("2");
    await reviewer.getByLabel("事实说明 / 原因", { exact: true }).fill("非原操作人确认2件未执行");
    await reviewer.getByRole("button", { name: "预览复核未执行短缺释放", exact: true }).click();
    await confirm(reviewer, `/api/wms/outbound/${order}/shortage-release`);
    const released = await readAfter(reviewer, order);
    expect(released.record.lines[0]).toMatchObject({ plannedQuantity: "7.000000", reservedQuantity: "5.000000", releasedQuantity: "2.000000", shippedQuantity: "0.000000" });
  } finally { await reviewerContext.close(); }
  const currentResponse = page.waitForResponse(item => new URL(item.url()).pathname === `/api/wms/outbound/${order}`);
  await page.getByRole("button", { name: "读取当前出库单", exact: true }).click(); expect((await currentResponse).status()).toBe(200);
  await page.getByTestId("prepare-outbound-shipment").click();
  await page.getByLabel("本行一次直发数量", { exact: true }).fill("5.000");
  await page.getByRole("button", { name: "预览确认无运输直发", exact: true }).click();
  for (const width of [375, 768, 1440]) {
    await page.setViewportSize({ width, height: 900 });
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true);
  }
  await page.setViewportSize({ width: 375, height: 900 });
  await page.evaluate(() => { document.documentElement.style.fontSize = "200%"; });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true);
  await page.evaluate(() => { document.documentElement.style.fontSize = ""; });
  let originalKey = ""; let originalBody = "";
  const shipmentPath = `/api/wms/outbound/${order}/shipment`;
  await page.route(`**${shipmentPath}`, async route => {
    if (originalKey) { await route.continue(); return; }
    originalKey = route.request().headers()["idempotency-key"]!; originalBody = route.request().postData()!;
    const response = await route.fetch(); expect(response.status()).toBe(201);
    await route.fulfill({ status: 503, contentType: "application/json", body: "{}" });
  });
  await confirm(page, shipmentPath, 503);
  const retry = page.getByTestId("retry-outbound"); await expect(retry).toBeVisible();
  await expect(page.getByLabel("出库单 UUID", { exact: true })).toHaveCount(0);
  await context.setOffline(true); await expect(retry).toBeDisabled();
  await context.setOffline(false); await expect(retry).toBeEnabled();
  const recoveredResponse = page.waitForResponse(item => new URL(item.url()).pathname === shipmentPath && item.status() === 200);
  await retry.click(); const recovered = await recoveredResponse;
  expect(recovered.request().headers()["idempotency-key"]).toBe(originalKey);
  expect(recovered.request().postData()).toBe(originalBody);
  expect((await recovered.json()).decision).toBe(1);
  const shipped = await readAfter(page, order);
  expect(shipped.record.state).toBe(2);
  expect(shipped.record.lines[0]).toMatchObject({ id: line, plannedQuantity: "7.000000", reservedQuantity: "0.000000", shippedQuantity: "5.000000" });
  await page.getByTestId("prepare-outbound-close-remainder").click();
  await page.getByLabel("事实说明 / 原因", { exact: true }).fill("部分已发5，关闭未发2");
  await page.getByRole("button", { name: "预览关闭部分发运余量", exact: true }).click();
  await confirm(page, `/api/wms/outbound/${order}/close-remainder`);
  const closed = await readAfter(page, order); expect(closed.record.state).toBe(5);
  expect(closed.record.lines[0].plannedQuantity).toBe("7.000000");
  await expect(page.getByTestId("outbound-detail")).toContainText("部分完成并关闭余量");
  const stored = await page.evaluate(() => JSON.stringify([Object.entries(localStorage), Object.entries(sessionStorage)]));
  for (const value of [order, balance, balanceVersion, originalKey, "accessToken"]) expect(stored).not.toContain(value);
  expect(failures).toEqual([]);
});
