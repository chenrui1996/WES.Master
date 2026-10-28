import { expect, test } from "@playwright/test";
import type { TransferDetail } from "../src/features/wms/transfers";

test("multi-line transfer recovers original arrival, closes only safe remainder and completes each durable unit", async ({ page, context }) => {
  test.skip(process.env.WES_E2E_TRANSFER_FIXTURE !== "1", "Requires the isolated TransferOrder/MySQL HTTPS fixture.");
  test.setTimeout(180000);
  const tenant = process.env.WES_E2E_TENANT; const warehouse = process.env.WES_E2E_TRANSFER_WAREHOUSE;
  const sourceA = process.env.WES_E2E_TRANSFER_SOURCE_A; const targetA = process.env.WES_E2E_TRANSFER_TARGET_A;
  const sourceB = process.env.WES_E2E_TRANSFER_SOURCE_B; const targetB = process.env.WES_E2E_TRANSFER_TARGET_B;
  if (!tenant || !warehouse || !sourceA || !targetA || !sourceB || !targetB) throw new Error("Missing isolated transfer identities.");
  const errors: string[] = []; page.on("pageerror", error => errors.push(error.message));
  await page.goto("/login");
  await page.getByLabel("租户标识").fill(tenant);
  await page.getByLabel("账号", { exact: true }).fill("fixture");
  await page.getByLabel("密码", { exact: true }).fill("WES reference fixture password");
  await page.getByRole("button", { name: "登录", exact: true }).click();
  await expect(page.getByRole("heading", { name: "会话与安全", exact: true })).toBeVisible();
  await page.getByTestId("navigation-toggle").click();
  await page.getByRole("navigation", { name: "管理导航" }).getByRole("link", { name: "多行移库与补账", exact: true }).click();
  async function confirm(path: string, status = 201) {
    const pending = page.waitForResponse(response => new URL(response.url()).pathname === path && response.request().method() === "POST");
    await page.getByTestId("confirm-transfer").focus(); await page.keyboard.press("Enter");
    const response = await pending; expect(response.status()).toBe(status);
    return response;
  }
  async function readAfter(order: string): Promise<TransferDetail> {
    const pending = page.waitForResponse(response => new URL(response.url()).pathname === `/api/wms/transfers/${order}` && response.request().method() === "GET");
    await page.getByTestId("read-transfer-after-write").click();
    const response = await pending; expect(response.status()).toBe(200);
    const result: { decision: number; record: TransferDetail } = await response.json();
    expect(result.decision).toBe(0); expect(result.record.id).toBe(order);
    await expect(page.getByTestId("transfer-detail")).toContainText(result.record.version);
    return result.record;
  }
  async function create(code: string, reverseA: boolean) {
    await page.getByTestId("prepare-transfer-create").click();
    await page.getByLabel("单号", { exact: true }).fill(code);
    await page.getByLabel("仓库 UUID", { exact: true }).fill(warehouse!);
    await page.getByTestId("transfer-add-line").click();
    const sources = page.getByLabel("来源账维度 UUID", { exact: true });
    const targets = page.getByLabel("目标零余额账维度 UUID", { exact: true });
    const quantities = page.getByLabel("完整单元数量（十进制 string）", { exact: true });
    await sources.nth(0).fill(reverseA ? targetA! : sourceA!); await targets.nth(0).fill(reverseA ? sourceA! : targetA!);
    await quantities.nth(0).fill("3.000"); await sources.nth(1).fill(sourceB!); await targets.nth(1).fill(targetB!);
    await quantities.nth(1).fill("1");
    await page.getByRole("button", { name: "预览全部移库草稿行", exact: true }).click();
    const response = await confirm("/api/wms/transfers");
    const { id } = await response.json();
    if (typeof id !== "string") throw new Error("Invalid transfer creation receipt.");
    const draft = await readAfter(id); expect(draft.state).toBe(0); expect(draft.lines).toHaveLength(2);
    await page.getByTestId("prepare-transfer-approval").click();
    await page.getByRole("button", { name: "预览全行原子审核", exact: true }).click();
    const preview = page.getByTestId("transfer-preview-body");
    for (const line of draft.lines) { await expect(preview).toContainText(line.sourceVersion); await expect(preview).toContainText(line.targetVersion); }
    await confirm(`/api/wms/transfers/${id}/approval`);
    const approved = await readAfter(id); expect(approved.state).toBe(1);
    return approved;
  }
  async function move(order: TransferDetail, index: number, loseResponse = false): Promise<TransferDetail> {
    const line = order.lines[index]; if (!line) throw new Error("Missing transfer line.");
    await page.getByTestId("prepare-transfer-dispatch").click();
    await page.getByLabel("固定执行行", { exact: true }).selectOption(line.id);
    await page.getByRole("button", { name: "预览确认人工开始搬运", exact: true }).click();
    await confirm(`/api/wms/transfers/${order.id}/dispatch`);
    const transit = await readAfter(order.id); expect(transit.lines.find(item => item.id === line.id)?.executionStatus).toBe(1);
    await page.getByTestId("prepare-transfer-arrivals").click();
    await page.getByLabel("固定执行行", { exact: true }).selectOption(line.id);
    await page.getByLabel("扫码来源库位 UUID", { exact: true }).fill(line.sourceLocationId);
    await page.getByLabel("扫码目标库位 UUID", { exact: true }).fill(line.targetLocationId);
    await page.getByLabel("扫码容器 UUID", { exact: true }).fill(line.containerId);
    await page.getByLabel("完整到达数量", { exact: true }).fill(line.plannedQuantity);
    await page.getByLabel("原因 / 确认说明", { exact: true }).fill("隔离软件夹具扫描确认");
    await page.getByRole("button", { name: "预览扫描确认人工到达", exact: true }).click();
    const path = `/api/wms/transfers/${order.id}/arrivals`;
    if (loseResponse) {
      for (const width of [375, 768, 1440]) {
        await page.setViewportSize({ width, height: 900 });
        expect(await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true);
      }
      await page.setViewportSize({ width: 375, height: 900 });
      await page.evaluate(() => { document.documentElement.style.fontSize = "200%"; });
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true);
      await page.evaluate(() => { document.documentElement.style.fontSize = ""; });
      let key = ""; let body = ""; let calls = 0;
      await page.route(`**${path}`, async route => {
        ++calls;
        if (key) { await route.continue(); return; }
        key = route.request().headers()["idempotency-key"]!; body = route.request().postData()!;
        const response = await route.fetch(); expect(response.status()).toBe(201);
        await route.fulfill({ status: 503, contentType: "application/json", body: "{}" });
      });
      await confirm(path, 503); const retry = page.getByTestId("retry-transfer"); await expect(retry).toBeVisible();
      await context.setOffline(true); await expect(retry).toBeDisabled();
      await context.setOffline(false); await expect(retry).toBeEnabled(); expect(calls).toBe(1);
      const recovered = page.waitForResponse(response => new URL(response.url()).pathname === path && response.status() === 200);
      await retry.click(); const response = await recovered;
      expect(response.request().headers()["idempotency-key"]).toBe(key); expect(response.request().postData()).toBe(body);
      expect((await response.json()).decision).toBe(1); expect(calls).toBe(2);
      await page.unroute(`**${path}`);
    } else await confirm(path);
    const current = await readAfter(order.id);
    expect(current.lines.find(item => item.id === line.id)).toMatchObject({
      executionStatus: 2, postingStatus: 2, reservedQuantity: "0.000000", movedQuantity: line.plannedQuantity,
    });
    return current;
  }
  let partial = await create("TR-BROWSER-PARTIAL", false);
  partial = await move(partial, 0, true); expect(partial.state).toBe(2);
  await page.getByTestId("prepare-transfer-closure").click();
  await page.getByLabel("原因 / 确认说明", { exact: true }).fill("确认B尚未执行，安全关闭余量");
  await page.getByRole("button", { name: "预览安全关闭未执行余量", exact: true }).click();
  await confirm(`/api/wms/transfers/${partial.id}/closure`);
  const closed = await readAfter(partial.id); expect(closed.state).toBe(5);
  expect(closed.lines[0]).toMatchObject({ movedQuantity: "3.000000", closedQuantity: "0.000000" });
  expect(closed.lines[1]).toMatchObject({ movedQuantity: "0.000000", closedQuantity: "1.000000", executionStatus: 4 });
  let complete = await create("TR-BROWSER-COMPLETE", true);
  complete = await move(complete, 0); expect(complete.state).toBe(2);
  complete = await move(complete, 1); expect(complete.state).toBe(3);
  await expect(page.getByTestId("prepare-transfer-closure")).toHaveCount(0);
  expect(errors).toEqual([]);
});
