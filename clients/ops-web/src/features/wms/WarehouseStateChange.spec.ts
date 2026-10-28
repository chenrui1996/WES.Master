import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import WarehouseStateChange from "./MasterDataStateChange.vue";
import MasterDataPage from "./MasterDataPage.vue";
import EntityAttachments from "./EntityAttachments.vue";
import MasterDataCatalog from "./MasterDataCatalog.vue";
import { forgetIdentity, sessionState, setAccessToken } from "../identity/identity-api";
import { readFields } from "../platform/staging-file";
import { warehouseFields, type MasterData } from "./master-data";
import { changeMasterDataState as changeWarehouseState } from "./master-data-state";

const id = "77966ccd-f1dd-412d-8e45-adb475608842";
const version = "a029ce52-d810-405c-b349-c0ac9ebad74b";
const after = "fdf5ae5d-bab5-4692-be09-336916370e56";
const row = { id, code: "WH-STATE", name: "仓库", enabled: false, version, createdAt: "2026-10-27T00:00:00+00:00" };
const warehouse: Extract<MasterData, { kind: "warehouses" }> = { kind: "warehouses", definition: row };
const receipt = { decision: 0, id, version: after };
let wrapper: VueWrapper | undefined;
beforeEach(() => {
  setAccessToken("fixture", "2099-01-01T00:00:00Z");
  sessionState.subject = { tenantId: "t1", userId: "u1", sessionId: "s1", credentialVersion: null, requiresPasswordChange: false,
    grants: [
      { permission: "wms.warehouses.read", scope: 0, departments: [], fields: [...warehouseFields] },
      { permission: "wms.warehouses.write", scope: 0, departments: [], fields: ["Enabled"] },
    ] };
  vi.spyOn(navigator, "onLine", "get").mockReturnValue(true);
  vi.stubGlobal("fetch", vi.fn(async () => new Response(JSON.stringify(receipt))));
});
afterEach(() => { wrapper?.unmount(); wrapper = undefined; vi.useRealTimers(); vi.restoreAllMocks(); vi.unstubAllGlobals(); forgetIdentity(); });
function panel() { wrapper = mount(WarehouseStateChange, { props: { resource: warehouse } }); return wrapper; }
async function preview() {
  await wrapper!.get('[name="warehouseStateReason"]').setValue("Ready");
  await wrapper!.get("form").trigger("submit"); await flushPromises();
}
async function confirm() { await wrapper!.get('[data-test="confirm-warehouse-state"]').trigger("click"); await flushPromises(); }

it("previews the complete immutable tuple without requests then PUTs only the strict command", async () => {
  panel(); await preview();
  expect(fetch).not.toHaveBeenCalled();
  expect(wrapper!.get('[data-test="warehouse-state-preview"]').text()).toContain(id);
  expect(wrapper!.text()).toContain(version); expect(wrapper!.text()).toContain("Ready");
  await confirm();
  const [url, options] = vi.mocked(fetch).mock.calls[0]!;
  expect(url).toBe(`/api/wms/warehouses/${id}/state`);
  expect(options!.method).toBe("PUT");
  expect(JSON.parse(String(options!.body))).toEqual({ expectedVersion: version, enabled: true, reason: "Ready" });
  expect(new Headers(options!.headers).get("Idempotency-Key")).toMatch(/^[a-f0-9-]{36}$/);
  expect(wrapper!.emitted("attempted")).toHaveLength(1);
  expect(wrapper!.text()).toContain(after); expect(wrapper!.text()).toContain("不是当前状态");
  expect(fetch).toHaveBeenCalledTimes(1);
  await wrapper!.get('[data-test="read-state-warehouse"]').trigger("click");
  expect(wrapper!.emitted("read")).toEqual([[id]]); expect(fetch).toHaveBeenCalledTimes(1);
});

it.each(["", " Ready", "Ready ", "Bad\nreason", "x".repeat(257)])("rejects invalid reason before preview: %j", async reason => {
  panel(); await wrapper!.get('[name="warehouseStateReason"]').setValue(reason);
  await wrapper!.get("form").trigger("submit"); await flushPromises();
  expect(wrapper!.get('[role="alert"]').text()).toContain("原因");
  expect(wrapper!.find('[data-test="confirm-warehouse-state"]').exists()).toBe(false); expect(fetch).not.toHaveBeenCalled();
});

it.each(["write:Enabled", ...warehouseFields])("requires every current field, not create privileges: %s", async field => {
  if (field.startsWith("write:")) sessionState.subject!.grants[1]!.fields = ["Code", "Name"];
  else sessionState.subject!.grants[0]!.fields = warehouseFields.filter(value => value !== field);
  panel(); expect(wrapper!.get('[role="alert"]').text()).toContain("字段权限");
  expect(wrapper!.find("form").exists()).toBe(false); expect(fetch).not.toHaveBeenCalled();
});

it("prepares disable only for a currently enabled warehouse", async () => {
  wrapper = mount(WarehouseStateChange, { props: { resource: { ...warehouse, definition: { ...row, enabled: true } } } });
  await preview(); await confirm();
  expect(JSON.parse(String(vi.mocked(fetch).mock.calls[0]![1]!.body)).enabled).toBe(false);
});

it("retains the original key/body after unknown and never automatically retries online", async () => {
  vi.mocked(fetch).mockResolvedValueOnce(new Response("", { status: 503 }));
  panel(); await preview(); await confirm();
  expect(wrapper!.text()).toContain("结果不明");
  const first = vi.mocked(fetch).mock.calls[0]![1]!;
  vi.spyOn(navigator, "onLine", "get").mockReturnValue(false); dispatchEvent(new Event("offline")); await flushPromises();
  expect(wrapper!.get('[data-test="retry-warehouse-state"]').attributes("disabled")).toBeDefined();
  vi.spyOn(navigator, "onLine", "get").mockReturnValue(true); dispatchEvent(new Event("online")); await flushPromises();
  expect(fetch).toHaveBeenCalledTimes(1);
  await wrapper!.get('[data-test="retry-warehouse-state"]').trigger("click"); await flushPromises();
  const second = vi.mocked(fetch).mock.calls[1]![1]!;
  expect(second.body).toBe(first.body);
  expect(new Headers(second.headers).get("Idempotency-Key")).toBe(new Headers(first.headers).get("Idempotency-Key"));
  expect(wrapper!.text()).toContain(after);
});

it("times out at exactly ten seconds and discards a late successful response", async () => {
  vi.useFakeTimers();
  let resolve!: (response: Response) => void;
  vi.mocked(fetch).mockImplementationOnce(() => new Promise<Response>(done => { resolve = done; }));
  panel(); await preview(); await confirm();
  await vi.advanceTimersByTimeAsync(9999); expect(wrapper!.find('[data-test="retry-warehouse-state"]').exists()).toBe(false);
  await vi.advanceTimersByTimeAsync(1); await flushPromises();
  expect(wrapper!.text()).toContain("10秒"); expect(wrapper!.find('[data-test="retry-warehouse-state"]').exists()).toBe(true);
  resolve(new Response(JSON.stringify(receipt))); await flushPromises();
  expect(wrapper!.find('[data-test="read-state-warehouse"]').exists()).toBe(false); expect(fetch).toHaveBeenCalledTimes(1);
});

it("drops a pending result and private tuple when identity changes", async () => {
  let resolve!: (response: Response) => void;
  vi.mocked(fetch).mockImplementationOnce(() => new Promise<Response>(done => { resolve = done; }));
  panel(); await preview(); await confirm();
  sessionState.subject!.sessionId = "other"; await flushPromises();
  expect(wrapper!.find('[data-test="warehouse-state-preview"]').exists()).toBe(false);
  resolve(new Response(JSON.stringify(receipt))); await flushPromises();
  expect(wrapper!.text()).not.toContain(after); expect(wrapper!.text()).not.toContain("Ready");
  expect(wrapper!.get('[role="alert"]').text()).toContain("已改变");
});

it("invalidates the frozen tuple when the source version changes", async () => {
  panel(); await preview();
  await wrapper!.setProps({ resource: { ...warehouse, definition: { ...row, version: after } } }); await flushPromises();
  expect(wrapper!.find('[data-test="warehouse-state-preview"]').exists()).toBe(false);
  expect(wrapper!.text()).not.toContain("Ready"); expect(wrapper!.get('[role="alert"]').text()).toContain("已改变");
  expect(fetch).not.toHaveBeenCalled();
});

it("treats a disconnected in-flight write as unknown and ignores its late success", async () => {
  let resolve!: (response: Response) => void;
  vi.mocked(fetch).mockImplementationOnce(() => new Promise<Response>(done => { resolve = done; }));
  panel(); await preview(); await confirm();
  vi.spyOn(navigator, "onLine", "get").mockReturnValue(false); dispatchEvent(new Event("offline")); await flushPromises();
  expect(wrapper!.text()).toContain("连接中断");
  resolve(new Response(JSON.stringify(receipt))); await flushPromises();
  expect(wrapper!.find('[data-test="read-state-warehouse"]').exists()).toBe(false);
  expect(wrapper!.get('[data-test="retry-warehouse-state"]').attributes("disabled")).toBeDefined();
  vi.spyOn(navigator, "onLine", "get").mockReturnValue(true); dispatchEvent(new Event("online")); await flushPromises();
  expect(fetch).toHaveBeenCalledTimes(1);
});

it("does not mistake a denied recovery attempt for rollback of the earlier unknown request", async () => {
  vi.mocked(fetch).mockResolvedValueOnce(new Response("", { status: 503 })).mockResolvedValueOnce(new Response("", { status: 403 }));
  panel(); await preview(); await confirm();
  await wrapper!.get('[data-test="retry-warehouse-state"]').trigger("click"); await flushPromises();
  expect(wrapper!.text()).toContain("先前不明结果仍需查证");
  expect(wrapper!.find('[data-test="retry-warehouse-state"]').exists()).toBe(false);
  expect(wrapper!.find('[data-test="read-state-warehouse"]').exists()).toBe(false);
  expect(fetch).toHaveBeenCalledTimes(2);
});

it.each([400, 403, 404, 405, 409])("makes definite rejection terminal without blind retries: %d", async status => {
  vi.mocked(fetch).mockResolvedValue(new Response("", { status }));
  panel(); await preview(); await confirm();
  expect(wrapper!.text()).toContain("本次请求被拒绝");
  expect(wrapper!.find('[data-test="retry-warehouse-state"]').exists()).toBe(false); expect(fetch).toHaveBeenCalledTimes(1);
});

it.each([
  { ...receipt, version }, { ...receipt, id: version }, { ...receipt, version: "bad" },
  { ...receipt, decision: 2 }, { ...receipt, extra: true }, { decision: 0, id },
])("rejects malformed or wrong-resource receipt as unknown: %j", async response => {
  vi.mocked(fetch).mockResolvedValue(new Response(JSON.stringify(response)));
  panel(); await preview(); await confirm();
  expect(wrapper!.text()).toContain("响应无效"); expect(wrapper!.find('[data-test="retry-warehouse-state"]').exists()).toBe(true);
});

it("does not fetch invalid command identities", async () => {
  await expect(changeWarehouseState({ id: "", expectedVersion: version, operation: after, enabled: true, reason: "Ready" },
    new AbortController().signal)).rejects.toThrow("UUID");
  expect(fetch).not.toHaveBeenCalled();
});

it("keeps the actual parent panel alive after attempted clears detail, unknown recovery and explicit GET", async () => {
  vi.mocked(fetch).mockResolvedValueOnce(new Response(JSON.stringify(row)))
    .mockResolvedValueOnce(new Response("", { status: 503 }))
    .mockResolvedValueOnce(new Response(JSON.stringify(receipt)))
    .mockResolvedValueOnce(new Response(JSON.stringify({ ...row, enabled: true, version: after })));
  sessionState.subject!.grants.push({ permission: "platform.files.read", scope: 0, departments: [], fields: [...readFields] });
  wrapper = mount(MasterDataPage);
  await wrapper.get('[name="resourceId"]').setValue(id); await wrapper.get("form").trigger("submit"); await flushPromises();
  await wrapper.get('[data-test="prepare-warehouse-state"]').trigger("click"); await flushPromises();
  expect(wrapper.findComponent(EntityAttachments).exists()).toBe(false);
  const child = wrapper.getComponent(WarehouseStateChange);
  await child.get('[name="warehouseStateReason"]').setValue("Ready"); await child.get("form").trigger("submit");
  await child.get('[data-test="confirm-warehouse-state"]').trigger("click"); await flushPromises();
  expect(wrapper.findComponent(WarehouseStateChange).exists()).toBe(true);
  expect(wrapper.get('[name="resourceId"]').attributes("disabled")).toBeDefined();
  expect(wrapper.getComponent(MasterDataCatalog).props("readable")).toBe(false);
  wrapper.getComponent(MasterDataCatalog).vm.$emit("select", version); await flushPromises(); expect(fetch).toHaveBeenCalledTimes(2);
  await child.get('[data-test="retry-warehouse-state"]').trigger("click"); await flushPromises();
  expect(fetch).toHaveBeenCalledTimes(3); expect(wrapper.findComponent(WarehouseStateChange).exists()).toBe(true);
  await child.get('[data-test="read-state-warehouse"]').trigger("click"); await flushPromises();
  expect(fetch).toHaveBeenCalledTimes(4);
  expect(wrapper.findComponent(WarehouseStateChange).exists()).toBe(false);
  expect(wrapper.text()).toContain(after);
  expect(wrapper.getComponent(EntityAttachments).props("entityId")).toBe(id);
});

it("cannot prepare from stale detail or while attachment work is locked", async () => {
  vi.mocked(fetch).mockResolvedValue(new Response(JSON.stringify(row)));
  sessionState.subject!.grants.push({ permission: "platform.files.read", scope: 0, departments: [], fields: [...readFields] });
  wrapper = mount(MasterDataPage);
  await wrapper.get('[name="resourceId"]').setValue(id); await wrapper.get("form").trigger("submit"); await flushPromises();
  wrapper.getComponent(EntityAttachments).vm.$emit("locked", true); await flushPromises();
  expect(wrapper.get('[data-test="prepare-warehouse-state"]').attributes("disabled")).toBeDefined();
  vi.spyOn(navigator, "onLine", "get").mockReturnValue(false); dispatchEvent(new Event("offline")); await flushPromises();
  expect(wrapper.get('[data-test="prepare-warehouse-state"]').attributes("disabled")).toBeDefined();
  vi.spyOn(navigator, "onLine", "get").mockReturnValue(true); dispatchEvent(new Event("online")); await flushPromises();
  wrapper.getComponent(EntityAttachments).vm.$emit("locked", false); await flushPromises();
  expect(wrapper.get('[data-test="prepare-warehouse-state"]').attributes("disabled")).toBeDefined();
  expect(fetch).toHaveBeenCalledTimes(1);
});
