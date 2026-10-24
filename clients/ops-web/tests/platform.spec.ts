import { expect, test } from "@playwright/test";

test("real HTTPS identity, authorized named pages and responsive frame", async ({ page }) => {
  const failures: string[] = [];
  page.on("pageerror", error => failures.push(error.message));
  await page.goto("/login");
  await expect(page.getByRole("heading", { name: "登录运行管理" })).toBeVisible();
  await page.getByLabel("租户标识").fill(process.env.WES_E2E_TENANT!);
  await page.getByLabel("账号", { exact: true }).fill("fixture");
  await page.getByLabel("密码", { exact: true }).fill("WES reference fixture password");
  await page.getByRole("button", { name: "登录", exact: true }).click();
  await expect(page.getByRole("heading", { name: "会话与安全", exact: true })).toBeVisible();
  expect(await page.evaluate(() => ({ local: localStorage.length, session: sessionStorage.length }))).toEqual({ local: 0, session: 0 });
  await page.setViewportSize({ width: 1440, height: 900 });
  const nav = page.getByRole("navigation", { name: "管理导航" });
  const toggle = page.getByTestId("navigation-toggle");
  await expect(toggle).toHaveAttribute("aria-expanded", "false");
  const collapsedWidth = await page.locator(".sidebar").evaluate(element => element.getBoundingClientRect().width);
  await toggle.click();
  await expect(toggle).toHaveAttribute("aria-expanded", "true");
  const expandedWidth = await page.locator(".sidebar").evaluate(element => element.getBoundingClientRect().width);
  expect(expandedWidth).toBeGreaterThan(collapsedWidth + 100);
  await expect(nav.getByRole("link", { name: "用户与部门", exact: true })).toBeVisible();
  await page.getByLabel("显示密度").selectOption("comfortable");
  await expect(page.locator(".app-layout")).toHaveAttribute("data-density", "comfortable");
  const densityEntries = await page.evaluate(() => Object.entries(localStorage));
  expect(densityEntries).toHaveLength(1);
  expect(densityEntries[0]?.[0]).toContain("production");
  expect(densityEntries[0]?.[1]).toBe("comfortable");
  expect(JSON.stringify(densityEntries)).not.toContain("accessToken");
  for (const width of [768, 1024, 1440]) {
    await page.setViewportSize({ width, height: 900 });
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true);
  }
  for (const [link, api] of [
    ["用户与部门", "/api/platform/users"], ["角色与权限", "/api/platform/roles"],
    ["租户", "/api/platform/tenants"], ["配置与语言", "/api/platform/settings"], ["审计与登录事件", "/api/platform/audit"],
  ]) {
    const response = page.waitForResponse(value => value.url().includes(api!) && value.request().method() === "GET");
    await page.getByRole("navigation", { name: "管理导航" }).getByRole("link", { name: link!, exact: true }).click();
    expect((await response).status()).toBe(200);
    await expect(page.getByRole("main").getByRole("heading", { level: 1 })).toBeVisible();
    for (const width of [768, 1024, 1440]) {
      await page.setViewportSize({ width, height: 900 });
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true);
    }
  }
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
  expect(failures).toEqual([]);
  await page.getByRole("button", { name: "退出当前会话" }).click();
  await expect(page.getByRole("heading", { name: "登录运行管理" })).toBeVisible();
  expect(await page.evaluate(() => Object.entries(localStorage))).toEqual(densityEntries);
  expect(await page.evaluate(() => sessionStorage.length)).toBe(0);
});
