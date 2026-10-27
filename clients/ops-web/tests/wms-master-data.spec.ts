import { expect, test } from "@playwright/test";
import { readFile, writeFile } from "node:fs/promises";
import { join } from "node:path";

test("real WMS definitions remain read-only and disappear after current role revocation", async ({ page, context }) => {
  test.skip(!process.env.WES_E2E_WMS_COORDINATION, "Requires the explicit WMS/current-role fixture, not the Platform-only fixture.");
  const tenant = process.env.WES_E2E_TENANT;
  const warehouse = process.env.WES_E2E_WAREHOUSE_ID;
  const location = process.env.WES_E2E_LOCATION_ID;
  const coordination = process.env.WES_E2E_WMS_COORDINATION;
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
  await page.getByLabel("资源类型", { exact: true }).selectOption("locations");
  await expect(details).toHaveCount(0);
  await page.getByLabel("资源 UUID", { exact: true }).fill(location);
  const readLocation = page.waitForResponse(response => response.url().endsWith(`/api/wms/locations/${location}`));
  await submit.click();
  const locationResponse = await readLocation;
  expect(locationResponse.status()).toBe(200);
  const locationDefinition = await locationResponse.json() as { packId: string; schemaVersion: string; version: string };
  await expect(details).toContainText("HTTP location");
  await expect(details).toContainText(warehouse);
  await expect(details).toContainText(locationDefinition.packId);
  await expect(details).toContainText(locationDefinition.schemaVersion);
  await expect(details).toContainText(locationDefinition.version);
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
  await expect(page.getByRole("region", { name: "主数据详情", exact: true })).toContainText("PermissionDenied");
  const stored = await page.evaluate(() => JSON.stringify([Object.entries(localStorage), Object.entries(sessionStorage)]));
  for (const secret of [warehouse, location, locationDefinition.packId, "HTTP location", "accessToken"])
    expect(stored).not.toContain(secret);
  expect(methods.length).toBe(3);
  expect(methods.every(method => method === "GET")).toBe(true);
  expect(failures).toEqual([]);
});
