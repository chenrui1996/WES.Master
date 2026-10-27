import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import WarehouseCreation from "./MasterDataCreation.vue";
import { forgetIdentity, sessionState, setAccessToken } from "../identity/identity-api";
import { warehouseDraftError } from "./master-data";

const id = "017966cc-f1dd-412d-8e45-adb475608842";
const version = "77966ccd-f1dd-412d-8e45-adb475608842";
let wrapper: VueWrapper | undefined;
beforeEach(() => {
  setAccessToken("fixture", "2099-01-01T00:00:00Z");
  sessionState.subject = { tenantId: "t1", userId: "u1", sessionId: "s1", requiresPasswordChange: false,
    credentialVersion: null, grants: [{ permission: "wms.warehouses.write", scope: 0, departments: [], fields: ["Code", "Name"] }] };
  vi.stubGlobal("fetch", vi.fn(async () => new Response(JSON.stringify({ decision: 0, id, version }), { status: 201 })));
});
afterEach(() => { wrapper?.unmount(); wrapper = undefined; vi.useRealTimers(); vi.unstubAllGlobals(); vi.restoreAllMocks(); forgetIdentity(); });
function open() { wrapper = mount(WarehouseCreation); }
async function preview(code = "new-wh", name = "New warehouse") {
  await wrapper!.get('input[name="warehouseCode"]').setValue(code);
  await wrapper!.get('input[name="warehouseName"]').setValue(name);
  await wrapper!.get("form").trigger("submit"); await flushPromises();
}
async function confirm() { await wrapper!.get('[data-test="confirm-warehouse"]').trigger("click"); await flushPromises(); }

it("previews a disabled warehouse before sending a canonical frozen body and explicit operation key", async () => {
  open(); await preview();
  expect(fetch).not.toHaveBeenCalled();
  expect(wrapper!.get('[data-test="warehouse-preview"]').text()).toContain("NEW-WH");
  await confirm();
  const [url, options] = vi.mocked(fetch).mock.calls[0]!;
  expect(url).toBe("/api/wms/warehouses");
  expect(options?.body).toBe(JSON.stringify({ code: "NEW-WH", name: "New warehouse" }));
  expect(new Headers(options?.headers).get("Idempotency-Key")).toMatch(/^[a-f0-9-]{36}$/);
  expect(wrapper!.text()).toContain("已确认禁用仓库");
  expect(wrapper!.text()).toContain(id);
  expect(wrapper!.text()).toContain("不代表可分配");
  expect(wrapper!.emitted("attempted")).toHaveLength(1);
  await wrapper!.get('[data-test="read-created-warehouse"]').trigger("click");
  expect(wrapper!.emitted("read")).toEqual([[id]]);
  expect(fetch).toHaveBeenCalledTimes(1);
});
it.each([
  { code: "", name: "Name" }, { code: " A", name: "Name" }, { code: "a".repeat(65), name: "Name" },
  { code: "A", name: "" }, { code: "A", name: "N".repeat(129) }, { code: "A", name: " Name" },
  { code: "A", name: "Name " }, { code: "A", name: "bad\u007fname" }, { code: "汉字", name: "Name" },
])("rejects invalid bounded draft %#", async draft => {
  open(); await preview(draft.code, draft.name);
  expect(wrapper!.find('[data-test="confirm-warehouse"]').exists()).toBe(false);
  expect(fetch).not.toHaveBeenCalled();
  expect(wrapper!.find('[role="alert"]').exists()).toBe(true);
});
it("rejects newline names at the shared boundary even though single-line inputs strip line breaks", () => {
  expect(warehouseDraftError("A", "bad\nname")).not.toBe("");
});
it("accepts the exact maximum code/name lengths without trimming or truncation", async () => {
  open(); await preview("a".repeat(64), "N".repeat(128)); await confirm();
  expect(JSON.parse(String(vi.mocked(fetch).mock.calls[0]![1]?.body))).toEqual({ code: "A".repeat(64), name: "N".repeat(128) });
});
it.each(["Code", "Name"])("does not allow a draft or send when current write field %s is missing", async field => {
  sessionState.subject!.grants[0]!.fields = ["Code", "Name"].filter(value => value !== field);
  open();
  expect(wrapper!.find("form").exists()).toBe(false);
  expect(fetch).not.toHaveBeenCalled();
});
it("retries an uncertain outcome only manually with the same body and key", async () => {
  vi.mocked(fetch).mockResolvedValueOnce(new Response("{}", { status: 503 }));
  open(); await preview(); await confirm();
  expect(wrapper!.text()).toContain("结果不明");
  expect(wrapper!.find("form").exists()).toBe(false);
  expect(fetch).toHaveBeenCalledTimes(1);
  await wrapper!.get('[data-test="retry-warehouse"]').trigger("click"); await flushPromises();
  const calls = vi.mocked(fetch).mock.calls;
  expect(calls[0]![1]?.body).toBe(calls[1]![1]?.body);
  expect(new Headers(calls[0]![1]?.headers).get("Idempotency-Key")).toBe(new Headers(calls[1]![1]?.headers).get("Idempotency-Key"));
  expect(wrapper!.text()).toContain("已确认禁用仓库");
});
it.each([
  { decision: 2, id, version }, { decision: 0, id: null, version }, { decision: 0, id, version: "" },
  { decision: 0, id, version, enabled: true }, { decision: 0, id, version, actor: "foreign" },
])("does not confirm malformed success-shaped replies %#", async response => {
  vi.mocked(fetch).mockResolvedValueOnce(new Response(JSON.stringify(response)));
  open(); await preview(); await confirm();
  expect(wrapper!.text()).toContain("结果不明");
  expect(wrapper!.text()).not.toContain("已确认禁用仓库");
});
it.each([400, 403, 404, 405, 409])("shows a definite refusal %i without creating a replacement operation", async status => {
  vi.mocked(fetch).mockResolvedValueOnce(new Response("{}", { status }));
  open(); await preview(); await confirm();
  expect(wrapper!.text()).toContain("本次请求被拒绝");
  expect(wrapper!.find('[data-test="retry-warehouse"]').exists()).toBe(false);
  expect(fetch).toHaveBeenCalledTimes(1);
});
it("keeps a previous uncertain outcome explicit when the same-key retry is later refused", async () => {
  vi.mocked(fetch).mockResolvedValueOnce(new Response("{}", { status: 503 }));
  vi.mocked(fetch).mockResolvedValueOnce(new Response("{}", { status: 409 }));
  open(); await preview(); await confirm();
  await wrapper!.get('[data-test="retry-warehouse"]').trigger("click"); await flushPromises();
  expect(wrapper!.text()).toContain("先前不明结果仍需查证");
});
it("invalidates the frozen command and draft when the current actor or fields change", async () => {
  open(); await preview();
  sessionState.subject!.grants = []; await flushPromises();
  expect(wrapper!.find('[data-test="warehouse-preview"]').exists()).toBe(false);
  expect(wrapper!.text()).not.toContain("New warehouse");
  expect(fetch).not.toHaveBeenCalled();
});
it("discards a late successful acknowledgement after an identity change", async () => {
  let finish!: (response: Response) => void;
  vi.mocked(fetch).mockImplementationOnce(() => new Promise(resolve => { finish = resolve; }));
  open(); await preview(); await wrapper!.get('[data-test="confirm-warehouse"]').trigger("click");
  sessionState.subject = { ...sessionState.subject!, userId: "another" }; await flushPromises();
  finish(new Response(JSON.stringify({ decision: 0, id, version }))); await flushPromises();
  expect(wrapper!.text()).not.toContain(id);
  expect(wrapper!.text()).not.toContain("已确认禁用仓库");
});
it("does not queue or automatically retry after going offline", async () => {
  open(); await preview();
  vi.spyOn(navigator, "onLine", "get").mockReturnValue(false);
  dispatchEvent(new Event("offline")); await flushPromises();
  await confirm(); expect(fetch).not.toHaveBeenCalled();
  vi.spyOn(navigator, "onLine", "get").mockReturnValue(true);
  dispatchEvent(new Event("online")); await flushPromises();
  expect(fetch).not.toHaveBeenCalled();
});
it("marks an interrupted submit unknown, retains its operation, and ignores the late reply", async () => {
  let finish!: (response: Response) => void;
  vi.mocked(fetch).mockImplementationOnce(() => new Promise(resolve => { finish = resolve; }));
  open(); await preview(); await wrapper!.get('[data-test="confirm-warehouse"]').trigger("click"); await flushPromises();
  const operation = new Headers(vi.mocked(fetch).mock.calls[0]![1]?.headers).get("Idempotency-Key");
  vi.spyOn(navigator, "onLine", "get").mockReturnValue(false);
  dispatchEvent(new Event("offline")); await flushPromises();
  finish(new Response(JSON.stringify({ decision: 0, id, version }))); await flushPromises();
  expect(wrapper!.text()).toContain("结果不明");
  expect(wrapper!.text()).toContain(operation);
  expect(wrapper!.text()).not.toContain("已确认禁用仓库");
  vi.spyOn(navigator, "onLine", "get").mockReturnValue(true);
  dispatchEvent(new Event("online")); await flushPromises();
  expect(fetch).toHaveBeenCalledTimes(1);
});
it("treats a 10-second confirmation deadline as unknown and does not send automatically", async () => {
  vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
  let finish!: (response: Response) => void;
  vi.mocked(fetch).mockImplementationOnce(() => new Promise(resolve => { finish = resolve; }));
  open(); await preview(); await wrapper!.get('[data-test="confirm-warehouse"]').trigger("click"); await flushPromises();
  await vi.advanceTimersByTimeAsync(9999);
  expect(wrapper!.text()).not.toContain("10 秒内");
  await vi.advanceTimersByTimeAsync(1);
  expect(wrapper!.text()).toContain("10 秒内");
  expect(vi.mocked(fetch).mock.calls[0]![1]?.signal?.aborted).toBe(true);
  finish(new Response(JSON.stringify({ decision: 0, id, version }))); await flushPromises();
  expect(wrapper!.text()).not.toContain("已确认禁用仓库");
  expect(fetch).toHaveBeenCalledTimes(1);
});
it("accepts a strictly validated replay receipt without silently issuing a resource read", async () => {
  vi.mocked(fetch).mockResolvedValueOnce(new Response(JSON.stringify({ decision: 1, id, version })));
  open(); await preview(); await confirm();
  expect(wrapper!.text()).toContain("已识别原操作");
  expect(fetch).toHaveBeenCalledTimes(1);
});
it("keeps malformed JSON confirmation unknown", async () => {
  vi.mocked(fetch).mockResolvedValueOnce(new Response("not-json"));
  open(); await preview(); await confirm();
  expect(wrapper!.text()).toContain("结果不明");
  expect(wrapper!.text()).not.toContain("已确认禁用仓库");
});
it("cancels the request and its deadline when unmounted", async () => {
  vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
  vi.mocked(fetch).mockImplementationOnce(() => new Promise(() => {}));
  open(); await preview(); await wrapper!.get('[data-test="confirm-warehouse"]').trigger("click"); await flushPromises();
  const signal = vi.mocked(fetch).mock.calls[0]![1]?.signal;
  wrapper!.unmount(); wrapper = undefined;
  expect(signal?.aborted).toBe(true);
  expect(vi.getTimerCount()).toBe(0);
});
