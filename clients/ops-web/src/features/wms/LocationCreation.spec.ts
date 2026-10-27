import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import MasterDataCreation from "./MasterDataCreation.vue";
import { forgetIdentity, sessionState, setAccessToken } from "../identity/identity-api";
import { locationDraftError } from "./master-data";

const id = "017966cc-f1dd-412d-8e45-adb475608842";
const parent = "77966ccd-f1dd-412d-8e45-adb475608842";
const pack = "a029ce52-d810-405c-b349-c0ac9ebad74b";
const fields = ["Code", "Name", "WarehouseId", "PackId", "SchemaVersion"];
let wrapper: VueWrapper | undefined;
beforeEach(() => {
  setAccessToken("fixture", "2099-01-01T00:00:00Z");
  sessionState.subject = { tenantId: "t1", userId: "u1", sessionId: "s1", requiresPasswordChange: false,
    credentialVersion: null, grants: [
      { permission: "wms.locations.write", scope: 0, departments: [], fields: [...fields] },
      { permission: "wms.warehouses.read", scope: 0, departments: [], fields: ["Id", "Code", "Name", "Enabled", "Version", "CreatedAt"] }
    ] };
  vi.stubGlobal("fetch", vi.fn(async () => new Response(JSON.stringify({ decision: 0, id, version: pack }), { status: 201 })));
});
afterEach(() => { wrapper?.unmount(); wrapper = undefined; vi.useRealTimers(); vi.unstubAllGlobals(); vi.restoreAllMocks(); forgetIdentity(); });
function open() { wrapper = mount(MasterDataCreation, { props: { kind: "locations" } }); }
async function preview(overrides: Partial<{ locationCode: string; locationName: string; warehouseId: string; packId: string; schemaVersion: string }> = {}) {
  for (const [name, value] of Object.entries({ locationCode: "new-loc", locationName: "New location",
    warehouseId: parent, packId: pack, schemaVersion: "v1", ...overrides }))
    await wrapper!.get(`input[name="${name}"]`).setValue(value);
  await wrapper!.get("form").trigger("submit"); await flushPromises();
}
async function confirm() { await wrapper!.get('[data-test="confirm-location"]').trigger("click"); await flushPromises(); }

it("freezes the full location tuple before a second confirmation sends exact body and permanent key", async () => {
  open(); await preview();
  expect(fetch).not.toHaveBeenCalled();
  const text = wrapper!.get('[data-test="location-preview"]').text();
  for (const value of ["NEW-LOC", parent, pack, "v1", "禁用"]) expect(text).toContain(value);
  await confirm();
  const [url, options] = vi.mocked(fetch).mock.calls[0]!;
  expect(url).toBe("/api/wms/locations");
  expect(JSON.parse(String(options?.body))).toEqual({ code: "NEW-LOC", name: "New location", warehouseId: parent, packId: pack, schemaVersion: "v1" });
  expect(new Headers(options?.headers).get("Idempotency-Key")).toMatch(/^[a-f0-9-]{36}$/);
  expect(wrapper!.text()).toContain("已确认禁用库位");
  await wrapper!.get('[data-test="read-created-location"]').trigger("click");
  expect(wrapper!.emitted("read")).toEqual([[id]]);
  expect(fetch).toHaveBeenCalledTimes(1);
});
it.each([...fields, "parent-read"])("requires current write fields and parent read: %s", async missing => {
  if (missing === "parent-read") sessionState.subject!.grants[1]!.fields = [];
  else sessionState.subject!.grants[0]!.fields = fields.filter(field => field !== missing);
  open();
  expect(wrapper!.find("form").exists()).toBe(false);
  expect(fetch).not.toHaveBeenCalled();
});
it.each([
  { warehouseId: "" }, { warehouseId: "00000000-0000-0000-0000-000000000000" }, { warehouseId: ` ${parent}` },
  { packId: "../admin" }, { packId: "00000000-0000-0000-0000-000000000000" },
  { schemaVersion: "" }, { schemaVersion: "v".repeat(65) }, { schemaVersion: " v1" },
  { locationCode: " a" }, { locationName: "Name " },
])("rejects invalid full tuple %# without sending", async values => {
  open(); await preview(values);
  expect(wrapper!.find('[data-test="confirm-location"]').exists()).toBe(false);
  expect(fetch).not.toHaveBeenCalled();
  expect(wrapper!.find('[role="alert"]').exists()).toBe(true);
});
it("shares exact max bounds and preserves case-sensitive schema version", async () => {
  expect(locationDraftError("a".repeat(64), "N".repeat(128), parent, pack, "V".repeat(64))).toBe("");
  expect(locationDraftError("A", "bad\nname", parent, pack, "v1")).not.toBe("");
  open(); await preview({ schemaVersion: "V1" }); await confirm();
  expect(JSON.parse(String(vi.mocked(fetch).mock.calls[0]![1]?.body)).schemaVersion).toBe("V1");
});
it.each([400, 403, 404, 405, 409, 422])("treats definite refusal %i without generating a replacement operation", async status => {
  vi.mocked(fetch).mockResolvedValueOnce(new Response("{}", { status }));
  open(); await preview(); await confirm();
  expect(wrapper!.text()).toContain("本次请求被拒绝");
  expect(wrapper!.find('[data-test="retry-location"]').exists()).toBe(false);
  expect(fetch).toHaveBeenCalledTimes(1);
});
it("retries schema unavailability only manually with the same frozen tuple and operation", async () => {
  vi.mocked(fetch).mockResolvedValueOnce(new Response("{}", { status: 503 }));
  open(); await preview(); await confirm();
  expect(wrapper!.text()).toContain("结果不明");
  expect(wrapper!.find("form").exists()).toBe(false);
  await wrapper!.get('[data-test="retry-location"]').trigger("click"); await flushPromises();
  const calls = vi.mocked(fetch).mock.calls;
  expect(calls[0]![1]?.body).toBe(calls[1]![1]?.body);
  expect(new Headers(calls[0]![1]?.headers).get("Idempotency-Key")).toBe(new Headers(calls[1]![1]?.headers).get("Idempotency-Key"));
  expect(wrapper!.text()).toContain("已确认禁用库位");
});
it.each([
  { decision: 0, id, version: pack, enabled: true }, { decision: 2, id, version: pack },
  { decision: 0, id: "", version: pack }, { decision: 0, id, version: null },
])("rejects malformed success replies %# as unknown", async body => {
  vi.mocked(fetch).mockResolvedValueOnce(new Response(JSON.stringify(body)));
  open(); await preview(); await confirm();
  expect(wrapper!.text()).toContain("结果不明");
  expect(wrapper!.text()).not.toContain("已确认禁用库位");
});
it("clears every input and ignores late success after current fields change", async () => {
  let finish!: (response: Response) => void;
  vi.mocked(fetch).mockImplementationOnce(() => new Promise(resolve => { finish = resolve; }));
  open(); await preview(); await wrapper!.get('[data-test="confirm-location"]').trigger("click");
  sessionState.subject!.grants = []; await flushPromises();
  expect(vi.mocked(fetch).mock.calls[0]![1]?.signal?.aborted).toBe(true);
  finish(new Response(JSON.stringify({ decision: 0, id, version: pack }))); await flushPromises();
  expect(wrapper!.find('[data-test="location-preview"]').exists()).toBe(false);
  expect(wrapper!.text()).not.toContain(parent);
  expect(wrapper!.text()).not.toContain("已确认禁用库位");
});
it("marks the exact 10 second deadline unknown and never automatically retries", async () => {
  vi.useFakeTimers();
  vi.mocked(fetch).mockImplementationOnce(() => new Promise(() => {}));
  open(); await preview(); await wrapper!.get('[data-test="confirm-location"]').trigger("click");
  await vi.advanceTimersByTimeAsync(9999); expect(wrapper!.text()).toContain("提交中");
  await vi.advanceTimersByTimeAsync(1); expect(wrapper!.text()).toContain("结果不明");
  expect(fetch).toHaveBeenCalledTimes(1);
  expect(vi.mocked(fetch).mock.calls[0]![1]?.signal?.aborted).toBe(true);
  expect(vi.getTimerCount()).toBe(0);
});
it("preserves earlier uncertainty when a same-key retry is later rejected by schema", async () => {
  vi.mocked(fetch).mockResolvedValueOnce(new Response("{}", { status: 503 }));
  vi.mocked(fetch).mockResolvedValueOnce(new Response("{}", { status: 422 }));
  open(); await preview(); await confirm();
  await wrapper!.get('[data-test="retry-location"]').trigger("click"); await flushPromises();
  expect(wrapper!.text()).toContain("先前不明结果仍需查证");
});
it("never queues a location operation when offline or automatically sends on reconnect", async () => {
  open(); await preview();
  vi.spyOn(navigator, "onLine", "get").mockReturnValue(false);
  dispatchEvent(new Event("offline")); await flushPromises();
  await confirm(); expect(fetch).not.toHaveBeenCalled();
  vi.spyOn(navigator, "onLine", "get").mockReturnValue(true);
  dispatchEvent(new Event("online")); await flushPromises();
  expect(fetch).not.toHaveBeenCalled();
});
it("invalidates the full frozen tuple when kind changes instead of posting it to another boundary", async () => {
  open(); await preview();
  await wrapper!.setProps({ kind: "warehouses" });
  expect(wrapper!.find('[data-test="warehouse-preview"]').exists()).toBe(false);
  expect(wrapper!.text()).not.toContain(parent);
  expect(wrapper!.text()).toContain("当前主体或字段权限已改变");
  expect(fetch).not.toHaveBeenCalled();
});
