import { expect, test, type Page, type Response } from "@playwright/test";

test("blind count keeps a durable local lock, separates review and recovers the original atomic adjustment", async ({ page, browser, baseURL }) => {
  test.skip(process.env.WES_E2E_COUNT_FIXTURE !== "1", "Requires the isolated count/MySQL HTTPS fixture.");
  test.setTimeout(180000);
  const tenant = process.env.WES_E2E_TENANT; const warehouse = process.env.WES_E2E_COUNT_WAREHOUSE;
  const location = process.env.WES_E2E_COUNT_LOCATION;
  if (!tenant || !warehouse || !location || !baseURL) throw new Error("Missing isolated count identities.");
  const failures: string[] = [];
  page.on("pageerror", error => failures.push(error.message));
  async function login(target: Page, account: string) {
    await target.goto("/login");
    await target.getByLabel("租户标识").fill(tenant!);
    await target.getByLabel("账号", { exact: true }).fill(account);
    await target.getByLabel("密码", { exact: true }).fill("WES reference fixture password");
    await target.getByRole("button", { name: "登录", exact: true }).click();
    await expect(target.getByRole("heading", { name: "会话与安全", exact: true })).toBeVisible();
    await target.getByTestId("navigation-toggle").click();
    await target.getByRole("navigation", { name: "管理导航" }).getByRole("link", { name: "盲盘与差异调整", exact: true }).click();
  }
  async function confirm(target: Page, path: string, status = 201): Promise<Response> {
    const response = target.waitForResponse(item => new URL(item.url()).pathname === path && item.request().method() === "POST");
    await target.getByTestId("confirm-count").focus(); await target.keyboard.press("Enter");
    const result = await response; expect(result.status()).toBe(status);
    return result;
  }
  async function readAfter(target: Page, plan: string) {
    const response = target.waitForResponse(item => new URL(item.url()).pathname === `/api/wms/counts/${plan}` && item.request().method() === "GET");
    await target.getByTestId("read-count-after-write").click();
    const result = await response; expect(result.status()).toBe(200);
    const value = await result.json();
    for (const line of value.record.lines) {
      expect(Object.keys(line).sort()).toEqual(["batch", "containerId", "countedAt", "countedBy", "id", "itemId", "precision", "quantity", "unitId"].sort());
    }
    return value;
  }
  await login(page, "fixture");
  await page.getByTestId("prepare-count-create").click();
  await page.getByLabel("单号", { exact: true }).fill("COUNT-BROWSER");
  await page.getByLabel("仓库 UUID", { exact: true }).fill(warehouse);
  await page.getByLabel("盘点库位 UUID", { exact: true }).fill(location);
  await page.getByRole("button", { name: "预览创建盘点计划", exact: true }).click();
  const created = await confirm(page, "/api/wms/counts");
  const plan = (await created.json()).id as string;
  expect((await readAfter(page, plan)).record.state).toBe(0);
  await page.getByTestId("prepare-count-start").click();
  await page.getByRole("button", { name: "预览开始盲盘并锁定库位", exact: true }).click();
  await confirm(page, `/api/wms/counts/${plan}/start`);
  const started = (await readAfter(page, plan)).record;
  expect(started.state).toBe(1); expect(started.lines).toHaveLength(3);
  expect(started.reminderDue).toBe(false); expect(started.policy).toBe("WES-RULES-1.0/RULE-06/1800s");
  const quantities = ["12", "8", "10"];
  for (const [index, line] of started.lines.entries()) {
    expect(line.quantity).toBeNull(); expect(line.countedBy).toBeNull(); expect(line.countedAt).toBeNull();
    await page.getByTestId("prepare-count-records").click();
    await page.getByLabel("应盘行", { exact: true }).selectOption(line.id);
    await page.getByLabel("扫码库位 UUID", { exact: true }).fill(location);
    await page.getByLabel("扫码物料 UUID", { exact: true }).fill(line.itemId);
    await page.getByLabel("扫码容器 UUID", { exact: true }).fill(line.containerId);
    await page.getByLabel("实盘数量（十进制 string，可为0）", { exact: true }).fill(quantities[index]!);
    await page.getByLabel("原因 / 实盘修订说明", { exact: true }).fill(`真实界面实盘第${index + 1}行`);
    await page.getByRole("button", { name: "预览记录实盘或修订", exact: true }).click();
    const preview = page.getByTestId("count-preview-body");
    await expect(preview).toContainText(line.id); await expect(preview).not.toContainText("bookQuantity");
    await confirm(page, `/api/wms/counts/${plan}/records`);
    const counted = (await readAfter(page, plan)).record;
    expect(counted.state).toBe(index === 2 ? 2 : 1);
    expect(counted.lines.find((candidate: { id: string }) => candidate.id === line.id).quantity).toBe(`${quantities[index]}.000000`);
  }
  await expect(page.getByTestId("count-review-detail")).toHaveCount(0);
  await expect(page.getByTestId("prepare-count-adjustment")).toHaveCount(0);
  const ownReview = page.waitForResponse(item => new URL(item.url()).pathname === `/api/wms/counts/${plan}/review`);
  await page.getByTestId("count-review").click(); expect((await ownReview).status()).toBe(200);
  await expect(page.getByText("你是某个非零差异的最终盘点人，不能自行批准；请由另一授权人员复核。", { exact: true })).toBeVisible();
  await expect(page.getByTestId("prepare-count-adjustment")).toHaveCount(0);
  const reviewerContext = await browser.newContext({ baseURL, ignoreHTTPSErrors: true });
  try {
    const reviewer = await reviewerContext.newPage(); reviewer.on("pageerror", error => failures.push(error.message));
    await login(reviewer, "count-reviewer");
    await reviewer.getByLabel("盘点单 UUID", { exact: true }).fill(plan);
    const currentResponse = reviewer.waitForResponse(item => new URL(item.url()).pathname === `/api/wms/counts/${plan}`);
    await reviewer.getByTestId("count-query-button").click(); expect((await currentResponse).status()).toBe(200);
    await expect(reviewer.getByTestId("count-review-detail")).toHaveCount(0);
    const reviewResponse = reviewer.waitForResponse(item => new URL(item.url()).pathname === `/api/wms/counts/${plan}/review`);
    await reviewer.getByTestId("count-review").click();
    const review = await reviewResponse; expect(review.status()).toBe(200);
    const reviewed = (await review.json()).record;
    expect(reviewed.lines.map((line: { difference: string }) => line.difference).sort()).toEqual(["-2.000000", "0.000000", "2.000000"]);
    await reviewer.getByTestId("prepare-count-adjustment").click();
    await reviewer.getByLabel("原因 / 实盘修订说明", { exact: true }).fill("另一受权人员复核全部差异");
    await reviewer.getByRole("button", { name: "预览授权确认全部差异调整", exact: true }).click();
    await expect(reviewer.getByTestId("count-review-preview")).toContainText("-2.000000");
    for (const width of [375, 768, 1440]) {
      await reviewer.setViewportSize({ width, height: 900 });
      expect(await reviewer.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true);
    }
    await reviewer.setViewportSize({ width: 375, height: 900 });
    await reviewer.evaluate(() => { document.documentElement.style.fontSize = "200%"; });
    expect(await reviewer.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true);
    await reviewer.evaluate(() => { document.documentElement.style.fontSize = ""; });
    let originalKey = ""; let originalBody = ""; let attempts = 0;
    const adjustmentPath = `/api/wms/counts/${plan}/adjustment`;
    await reviewer.route(`**${adjustmentPath}`, async route => {
      attempts++;
      if (originalKey) { await route.continue(); return; }
      originalKey = route.request().headers()["idempotency-key"]!; originalBody = route.request().postData()!;
      const response = await route.fetch(); expect(response.status()).toBe(201);
      await route.fulfill({ status: 503, contentType: "application/json", body: "{}" });
    });
    await confirm(reviewer, adjustmentPath, 503);
    const retry = reviewer.getByTestId("retry-count"); await expect(retry).toBeVisible();
    await expect(reviewer.getByTestId("count-query-button")).toBeDisabled();
    await reviewerContext.setOffline(true); await expect(retry).toBeDisabled();
    await reviewerContext.setOffline(false); await expect(retry).toBeEnabled(); expect(attempts).toBe(1);
    const recoveredResponse = reviewer.waitForResponse(item => new URL(item.url()).pathname === adjustmentPath && item.status() === 200);
    await retry.click(); const recovered = await recoveredResponse;
    expect(recovered.request().headers()["idempotency-key"]).toBe(originalKey);
    expect(recovered.request().postData()).toBe(originalBody); expect((await recovered.json()).decision).toBe(1);
    const completed = (await readAfter(reviewer, plan)).record;
    expect(completed.state).toBe(3); expect(completed.reminderDue).toBe(false);
    await expect(reviewer.getByTestId("prepare-count-records")).toHaveCount(0);
    await expect(reviewer.getByTestId("count-review-detail")).toHaveCount(0);
    expect(await reviewer.evaluate(() => Object.keys(localStorage).filter(key => /count|adjust|business-write/i.test(key)))).toEqual([]);
    expect(await reviewer.evaluate(() => Object.keys(sessionStorage).filter(key => /count|adjust|business-write/i.test(key)))).toEqual([]);
  } finally { await reviewerContext.close(); }
  expect(failures).toEqual([]);
});
