import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import MessageReplayPanel from "./MessageReplayPanel.vue";
import { forgetIdentity, sessionState, setAccessToken } from "../identity/identity-api";
import type { MessageRow } from "./messages";

const row: MessageRow = { eventId: "77966ccd-f1dd-412d-8e45-adb475608842", owner: "Platform",
  status: 3, contractName: "platform.audit", schemaVersion: 1, attempts: 2,
  createdAt: "2026-10-27T00:00:00Z", nextAttemptAt: null, publishedAt: null, lastErrorCategory: "InvalidContract" };
let wrapper: VueWrapper | undefined;
beforeEach(() => {
  setAccessToken("fixture", "2099-01-01T00:00:00Z");
  sessionState.subject = { tenantId: "t1", userId: "u1", sessionId: "s1", requiresPasswordChange: false,
    credentialVersion: null, grants: [{ permission: "platform.messages.replay", scope: 2, departments: [],
      fields: ["EventId", "Status", "Attempts", "Reason"] }] };
  vi.stubGlobal("fetch", vi.fn(async () => new Response(JSON.stringify({ decision: 5, attempts: 0 }))));
});
afterEach(() => { wrapper?.unmount(); wrapper = undefined; vi.unstubAllGlobals(); vi.restoreAllMocks(); forgetIdentity(); });
function open() { wrapper = mount(MessageReplayPanel, { props: { row } }); }
async function preview(reason = "repair contract reviewed") {
  await wrapper!.get('textarea').setValue(reason);
  await wrapper!.get('form').trigger("submit"); await flushPromises();
}
async function confirm() { await wrapper!.get('[data-test="confirm-replay"]').trigger("click"); await flushPromises(); }

it("previews event, expected attempts and reason before sending an explicitly keyed original-event request", async () => {
  open(); await preview("  repair contract reviewed  ");
  expect(fetch).not.toHaveBeenCalled();
  expect(wrapper!.get('[data-test="replay-preview"]').text()).toContain(row.eventId);
  expect(wrapper!.get('[data-test="replay-preview"]').text()).toContain("repair contract reviewed");
  await confirm();
  const [url, options] = vi.mocked(fetch).mock.calls[0]!;
  expect(url).toBe(`/api/platform/messages/${row.eventId}/replay`);
  expect(options?.body).toBe(JSON.stringify({ expectedAttempts: 2, reason: "repair contract reviewed" }));
  expect(new Headers(options?.headers).get("Idempotency-Key")).toMatch(/^[a-f0-9-]{36}$/);
  expect(wrapper!.text()).toContain("已提交或已识别原操作");
  expect(wrapper!.text()).toContain("不代表投递或业务完成");
});
it.each(["", "ab", "a".repeat(257), "bad\nreason", "bad\u007freason"])("rejects invalid reason %#", async reason => {
  open(); await preview(reason);
  expect(wrapper!.find('[data-test="confirm-replay"]').exists()).toBe(false);
  expect(fetch).not.toHaveBeenCalled();
  expect(wrapper!.text()).toContain("3 至 256");
});
it("retries an unconfirmed outcome with exactly the same body and operation and no automatic send", async () => {
  vi.mocked(fetch).mockResolvedValueOnce(new Response(JSON.stringify({ code: "Unavailable", correlationId: "c1" }), { status: 503 }));
  open(); await preview(); await confirm();
  expect(wrapper!.text()).toContain("结果不明");
  expect(wrapper!.find('textarea').exists()).toBe(false);
  expect(fetch).toHaveBeenCalledTimes(1);
  await wrapper!.get('[data-test="retry-replay"]').trigger("click"); await flushPromises();
  const calls = vi.mocked(fetch).mock.calls;
  expect(calls[0]![1]?.body).toBe(calls[1]![1]?.body);
  expect(new Headers(calls[0]![1]?.headers).get("Idempotency-Key")).toBe(new Headers(calls[1]![1]?.headers).get("Idempotency-Key"));
  expect(wrapper!.text()).toContain("已提交或已识别原操作");
});
it.each([
  { decision: 0, attempts: null }, { decision: 5, attempts: 1 }, { decision: 5, attempts: 0, payload: "bad" },
])("does not mistake malformed success-shaped replies for confirmation %#", async response => {
  vi.mocked(fetch).mockResolvedValueOnce(new Response(JSON.stringify(response)));
  open(); await preview(); await confirm();
  expect(wrapper!.text()).toContain("结果不明");
  expect(wrapper!.text()).not.toContain("已提交或已识别原操作");
});
it.each([400, 403, 404, 409])("requires a fresh read on a definite current request rejection %i", async status => {
  vi.mocked(fetch).mockResolvedValueOnce(new Response(JSON.stringify({ code: "Rejected", correlationId: "c1" }), { status }));
  open(); await preview(); await confirm();
  expect(wrapper!.text()).toContain("本次请求被拒绝");
  expect(wrapper!.find('[data-test="retry-replay"]').exists()).toBe(false);
});
it("does not send after the current replay grant is revoked", async () => {
  open(); await preview();
  sessionState.subject!.grants = []; await flushPromises();
  expect(wrapper!.find('[data-test="confirm-replay"]').exists()).toBe(false);
  expect(fetch).not.toHaveBeenCalled();
});
it("discards a late successful acknowledgement after the actor changes", async () => {
  let finish!: (value: Response) => void;
  vi.mocked(fetch).mockImplementationOnce(() => new Promise(resolve => { finish = resolve; }));
  open(); await preview(); await wrapper!.get('[data-test="confirm-replay"]').trigger("click");
  sessionState.subject = { ...sessionState.subject!, userId: "new-actor" }; await flushPromises();
  finish(new Response(JSON.stringify({ decision: 5, attempts: 0 }))); await flushPromises();
  expect(wrapper!.text()).not.toContain("已提交或已识别原操作");
});
it("does not queue or automatically retry after being offline", async () => {
  open(); await preview();
  vi.spyOn(navigator, "onLine", "get").mockReturnValue(false);
  dispatchEvent(new Event("offline")); await flushPromises();
  await confirm(); expect(fetch).not.toHaveBeenCalled();
  vi.spyOn(navigator, "onLine", "get").mockReturnValue(true);
  dispatchEvent(new Event("online")); await flushPromises();
  expect(fetch).not.toHaveBeenCalled();
});
it("keeps the original operation after an in-flight offline cancellation and ignores its late success", async () => {
  let finish!: (value: Response) => void;
  vi.mocked(fetch).mockImplementationOnce(() => new Promise(resolve => { finish = resolve; }));
  open(); await preview(); await wrapper!.get('[data-test="confirm-replay"]').trigger("click"); await flushPromises();
  const operation = new Headers(vi.mocked(fetch).mock.calls[0]![1]?.headers).get("Idempotency-Key");
  vi.spyOn(navigator, "onLine", "get").mockReturnValue(false);
  dispatchEvent(new Event("offline")); await flushPromises();
  finish(new Response(JSON.stringify({ decision: 5, attempts: 0 }))); await flushPromises();
  expect(wrapper!.text()).toContain("结果不明");
  expect(wrapper!.text()).not.toContain("已提交或已识别原操作");
  vi.spyOn(navigator, "onLine", "get").mockReturnValue(true);
  dispatchEvent(new Event("online")); await flushPromises();
  expect(fetch).toHaveBeenCalledTimes(1);
  await wrapper!.get('[data-test="retry-replay"]').trigger("click"); await flushPromises();
  expect(new Headers(vi.mocked(fetch).mock.calls[1]![1]?.headers).get("Idempotency-Key")).toBe(operation);
});
it("does not reinterpret a current rejection after an unknown outcome as a rollback", async () => {
  vi.mocked(fetch).mockRejectedValueOnce(new TypeError("network interrupted"))
    .mockResolvedValueOnce(new Response(JSON.stringify({ code: "Conflict", correlationId: "c1" }), { status: 409 }));
  open(); await preview(); await confirm();
  await wrapper!.get('[data-test="retry-replay"]').trigger("click"); await flushPromises();
  expect(wrapper!.text()).toContain("此前不明结果仍需查证");
});
it("renders an HTML-looking reason as text without injecting markup", async () => {
  open(); await preview("<img src=x onerror=alert(1)>");
  expect(wrapper!.get('[data-test="replay-preview"]').text()).toContain("<img src=x");
  expect(wrapper!.find('img').exists()).toBe(false);
});
it("recognizes an exact idempotent retry acknowledgement without an attempt value", async () => {
  vi.mocked(fetch).mockResolvedValueOnce(new Response(JSON.stringify({ decision: 5, attempts: null })));
  open(); await preview(); await confirm();
  expect(wrapper!.text()).toContain("已提交或已识别原操作");
});
