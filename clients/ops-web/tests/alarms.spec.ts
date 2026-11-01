import { expect, test } from "@playwright/test";

test("manual alarm preserves every fact, recovers acknowledgement and shows independent delivery", async ({ page }) => {
  test.skip(process.env.WES_E2E_NOTIFICATION_FIXTURE !== "1", "Requires the isolated alarm/MySQL HTTPS fixture.");
  test.setTimeout(180000);
  const tenant = process.env.WES_E2E_TENANT;
  if (!tenant) throw new Error("Missing isolated notification tenant.");
  const errors: string[] = [];
  page.on("pageerror", error => errors.push(error.message));
  await page.goto("/login");
  await page.getByLabel("租户标识").fill(tenant);
  await page.getByLabel("账号", { exact: true }).fill("fixture");
  await page.getByLabel("密码", { exact: true }).fill("WES reference fixture password");
  await page.getByRole("button", { name: "登录", exact: true }).click();
  await expect(page.getByRole("heading", { name: "会话与安全", exact: true })).toBeVisible();
  await page.getByTestId("navigation-toggle").click();
  await page.getByRole("navigation", { name: "管理导航" }).getByRole("link", { name: "告警与原始事实", exact: true }).click();
  const resource = crypto.randomUUID();
  await page.getByTestId("manual").click();
  await page.getByLabel("资源 UUID", { exact: true }).fill(resource);
  await page.getByLabel("告警业务码", { exact: true }).fill("Browser.ManualAlarm");
  await page.getByLabel("等级", { exact: true }).selectOption("High");
  await page.getByLabel("脱敏摘要（不填凭据）", { exact: true }).fill("Controlled manual observation <script>not executable</script>");
  await page.getByRole("button", { name: "预览此操作", exact: true }).click();
  const creation = page.waitForResponse(response => new URL(response.url()).pathname === "/api/notifications/alarms/manual"
    && response.request().method() === "POST");
  await page.getByTestId("confirm").focus(); await page.keyboard.press("Enter");
  const created = await creation;
  expect(created.status()).toBe(200);
  const receipt = await created.json();
  expect(receipt.decision).toBe("Applied");
  const alarmId: string = receipt.value.id;
  expect(typeof alarmId).toBe("string");
  await page.getByRole("button", { name: "已记录操作，关闭面板", exact: true }).click();
  await page.getByTestId("read").click();
  await expect(page.getByTestId("alarm-rows")).toContainText("Browser.ManualAlarm");
  await page.getByTestId("alarm-detail").click();
  await expect(page.getByTestId("occurrences")).toContainText("<script>not executable</script>");
  expect(await page.locator("script").filter({ hasText: "not executable" }).count()).toBe(0);
  await expect(page.getByTestId("silence")).toBeDisabled();
  await page.getByTestId("acknowledge").click();
  await page.getByLabel("操作理由", { exact: true }).fill("Reviewed the isolated manual observation");
  await page.getByRole("button", { name: "预览此操作", exact: true }).click();
  for (const width of [375, 768, 1440]) {
    await page.setViewportSize({ width, height: 900 });
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true);
  }
  await page.setViewportSize({ width: 375, height: 900 });
  await page.evaluate(() => { document.documentElement.style.fontSize = "200%"; });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true);
  await page.evaluate(() => { document.documentElement.style.fontSize = ""; });
  const path = `/api/notifications/alarms/${alarmId}/acknowledge`;
  let original = ""; let calls = 0;
  await page.route(`**${path}`, async route => {
    const body = route.request().postData() ?? "";
    if (++calls === 1) {
      original = body;
      const response = await route.fetch();
      expect(response.status()).toBe(200);
      await route.abort("connectionfailed");
    } else {
      expect(body).toBe(original);
      await route.continue();
    }
  });
  await page.getByTestId("confirm").click();
  await expect(page.getByTestId("recover")).toBeVisible();
  await page.getByTestId("recover").click();
  await expect(page.getByRole("status").filter({ hasText: "已确认或识别原操作" })).toBeVisible();
  expect(calls).toBe(2);
  await page.unroute(`**${path}`);
  await page.getByRole("button", { name: "已记录操作，关闭面板", exact: true }).click();
  await page.getByTestId("read").click();
  await expect(page.getByTestId("alarm-rows")).toContainText("Acknowledged");
  await page.getByTestId("alarm-detail").click();
  await page.getByTestId("clear").click();
  await page.getByLabel("操作理由", { exact: true }).fill("Close only the isolated manual-source alarm");
  await page.getByRole("button", { name: "预览此操作", exact: true }).click();
  await page.getByTestId("confirm").click();
  await expect(page.getByRole("status").filter({ hasText: "已确认或识别原操作" })).toBeVisible();
  await page.getByRole("button", { name: "已记录操作，关闭面板", exact: true }).click();
  await page.getByTestId("read").click();
  await expect(page.getByTestId("alarm-rows")).toContainText("Cleared");
  await page.getByRole("navigation", { name: "管理导航" }).getByRole("link", { name: "独立通知投递", exact: true }).click();
  await page.getByTestId("read").click();
  await expect(page.getByTestId("delivery-rows")).toContainText(alarmId);
  await page.getByTestId("delivery-detail").click();
  await expect(page.getByTestId("attempts")).toBeVisible();
  expect(errors).toEqual([]);
});
