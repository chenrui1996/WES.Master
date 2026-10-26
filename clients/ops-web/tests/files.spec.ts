import { expect, test } from "@playwright/test";
import { readFile } from "node:fs/promises";

test("real staging files upload quarantine scan failure and authorized download", async ({ page }) => {
  test.skip(!process.env.WES_E2E_FILES, "Requires the explicit private-storage/scanner-refusal fixture.");
  const failures: string[] = [];
  page.on("pageerror", error => failures.push(error.message));
  await page.goto("/login");
  await page.getByLabel("租户标识").fill(process.env.WES_E2E_TENANT!);
  await page.getByLabel("账号", { exact: true }).fill("fixture");
  await page.getByLabel("密码", { exact: true }).fill("WES reference fixture password");
  await page.getByRole("button", { name: "登录", exact: true }).click();
  await expect(page.getByRole("heading", { name: "会话与安全", exact: true })).toBeVisible();
  await page.getByTestId("navigation-toggle").click();
  await page.getByRole("navigation", { name: "管理导航" }).getByRole("link", { name: "暂存文件", exact: true }).click();
  await expect(page.getByRole("heading", { name: "文件隔离与处理" })).toBeVisible();
  await page.getByLabel("文件（PDF / PNG / JPEG / TXT，最多 20 MiB）").setInputFiles({
    name: "浏览器 检验.txt", mimeType: "text/plain", buffer: Buffer.alloc(1024 * 1024, "a"),
  });
  await page.evaluate(() => {
    const samples: number[] = [];
    const observer = new MutationObserver(() => {
      const progress = document.querySelector<HTMLProgressElement>('progress[aria-label="上传字节进度"]');
      if (progress) samples.push(progress.value);
    });
    observer.observe(document.body, { subtree: true, attributes: true, childList: true, characterData: true });
    Object.assign(window, { fileProgressSamples: samples, fileProgressObserver: observer });
  });
  const uploaded = page.waitForResponse(response => response.url().endsWith("/api/platform/files/staging")
    && response.request().method() === "POST");
  await page.getByRole("button", { name: "上传到隔离区", exact: true }).click();
  const upload = await uploaded;
  expect(upload.status()).toBe(200);
  expect(upload.request().headers()["content-type"]).toBe("text/plain");
  expect(upload.request().headers()["x-wes-file-name"]).toBe("%E6%B5%8F%E8%A7%88%E5%99%A8%20%E6%A3%80%E9%AA%8C.txt");
  const file = await upload.json() as { id: string };
  await expect(page.getByTestId("file-details")).toContainText("浏览器 检验.txt");
  await expect(page.getByTestId("file-details")).toContainText("1048576 字节");
  await expect(page.getByTestId("file-details")).toContainText("隔离待处理");
  await expect(page.getByTestId("file-download")).toBeDisabled();
  const samples = await page.evaluate(() => {
    const progressWindow = window as Window & { fileProgressSamples?: number[]; fileProgressObserver?: MutationObserver };
    progressWindow.fileProgressObserver?.disconnect();
    return progressWindow.fileProgressSamples ?? [];
  });
  expect(samples.some(value => value > 0 && value <= 1048576)).toBe(true);
  const scanned = page.waitForResponse(response => response.url().endsWith(`/${file.id}/scan`));
  await page.getByTestId("file-scan").click();
  expect((await scanned).status()).toBe(200);
  await expect(page.getByTestId("file-details")).toContainText("隔离待处理");
  await expect(page.getByTestId("file-download")).toBeDisabled();
  await page.getByTestId("file-delete").click();
  await expect(page.getByTestId("file-delete-confirm")).toContainText("24 小时");
  await page.getByRole("button", { name: "取消删除", exact: true }).click();
  await expect(page.getByTestId("file-delete-confirm")).toHaveCount(0);
  await page.getByRole("button", { name: "读取暂存目录", exact: true }).click();
  await expect(page.getByTestId("file-catalog")).toContainText("浏览器 检验.txt");
  for (const width of [375, 768, 1440]) {
    await page.setViewportSize({ width, height: 900 });
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true);
  }
  const chooseAvailable = page.getByRole("button", { name: "读取文件 note.txt", exact: true });
  await chooseAvailable.focus();
  await page.keyboard.press("Enter");
  await expect(page.getByLabel("暂存文件 UUID")).toHaveValue(process.env.WES_E2E_DOWNLOAD_ID!);
  await expect(page.getByTestId("file-details")).toContainText("可用（未绑定）");
  await page.getByRole("button", { name: "读取当前状态", exact: true }).focus();
  await page.keyboard.press("Tab");
  await expect(page.getByTestId("file-download")).toBeFocused();
  expect(await page.getByTestId("file-download").evaluate(element => element.matches(":focus-visible"))).toBe(true);
  const downloaded = page.waitForEvent("download");
  await page.keyboard.press("Enter");
  const download = await downloaded;
  expect(download.suggestedFilename()).toBe("note.txt");
  const path = await download.path();
  expect(path).not.toBeNull();
  expect((await readFile(path!)).toString("utf8")).toBe("safe");
  const stored = await page.evaluate(() => JSON.stringify([Object.entries(localStorage), Object.entries(sessionStorage)]));
  expect(stored).not.toContain(file.id);
  expect(stored).not.toContain("浏览器");
  expect(stored).not.toContain("accessToken");
  expect(failures).toEqual([]);
});
