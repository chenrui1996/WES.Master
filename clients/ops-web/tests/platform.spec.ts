import { expect, test } from "@playwright/test";

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
  const failures: string[] = [];
  page.on("pageerror", error => failures.push(error.message));
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
  expect(await page.evaluate(() => ({ local: localStorage.length, session: sessionStorage.length }))).toEqual({ local: 0, session: 0 });
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
  const densityEntries = await page.evaluate(() => Object.entries(localStorage));
  expect(densityEntries).toHaveLength(1);
  expect(densityEntries[0]?.[0]).toContain("production");
  expect(densityEntries[0]?.[1]).toBe("comfortable");
  expect(JSON.stringify(densityEntries)).not.toContain("accessToken");
  for (const width of [375, 768, 1024, 1440]) {
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
    await expect(page.locator(".workspace .wes-page")).toHaveCount(1);
    const tables = page.locator(".table-scroll");
    expect(await tables.count()).toBeGreaterThan(0);
    await expect(tables.first()).toBeVisible();
    for (const width of [375, 768, 1024, 1440]) {
      await page.setViewportSize({ width, height: 900 });
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true);
    }
  }
  const auditMode = page.getByLabel("证据类型");
  await auditMode.selectOption("login-events");
  const loginEvents = page.waitForResponse(value => value.url().includes("/api/platform/login-events") && value.request().method() === "GET");
  const queryButton = page.getByRole("button", { name: "查询证据", exact: true });
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
  expect(await page.evaluate(() => Object.entries(localStorage))).toEqual(densityEntries);
  expect(await page.evaluate(() => sessionStorage.length)).toBe(0);
});
