import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import MasterDataStateChange from "./MasterDataStateChange.vue";
import MasterDataPage from "./MasterDataPage.vue";
import MasterDataCatalog from "./MasterDataCatalog.vue";
import { forgetIdentity, sessionState, setAccessToken } from "../identity/identity-api";
import { locationFields, warehouseFields, type MasterData } from "./master-data";

const id = "77966ccd-f1dd-412d-8e45-adb475608842";
const parent = "319d21fd-930a-47cb-b6c1-d529f7c37a0d";
const version = "a029ce52-d810-405c-b349-c0ac9ebad74b";
const after = "fdf5ae5d-bab5-4692-be09-336916370e56";
const row = { id, code: "LOC-STATE", name: "库位", enabled: false, version, createdAt: "2026-10-28T00:00:00+00:00",
  warehouseId: parent, packId: id, schemaVersion: "v1" };
const resource: MasterData = { kind: "locations", definition: row };
const receipt = { decision: 0, id, version: after };
let wrapper: VueWrapper | undefined;
beforeEach(() => {
  setAccessToken("fixture", "2099-01-01T00:00:00Z");
  sessionState.subject = { tenantId: "t1", userId: "u1", sessionId: "s1", credentialVersion: null, requiresPasswordChange: false,
    grants: [
      { permission: "wms.warehouses.read", scope: 0, departments: [], fields: [...warehouseFields] },
      { permission: "wms.locations.read", scope: 0, departments: [], fields: [...locationFields] },
      { permission: "wms.locations.write", scope: 0, departments: [], fields: ["Enabled"] },
    ] };
  vi.spyOn(navigator, "onLine", "get").mockReturnValue(true);
  vi.stubGlobal("fetch", vi.fn(async () => new Response(JSON.stringify(receipt))));
});
afterEach(() => { wrapper?.unmount(); wrapper = undefined; vi.useRealTimers(); vi.restoreAllMocks(); vi.unstubAllGlobals(); forgetIdentity(); });
function panel() { wrapper = mount(MasterDataStateChange, { props: { resource } }); }
async function preview() {
  await wrapper!.get('[name="warehouseStateReason"]').setValue("Ready");
  await wrapper!.get("form").trigger("submit"); await flushPromises();
}
async function confirm() { await wrapper!.get('[data-test="confirm-warehouse-state"]').trigger("click"); await flushPromises(); }

it("previews pinned lineage without requests then PUTs the strict location tuple", async () => {
  panel(); expect(wrapper!.text()).toContain(parent); expect(wrapper!.text()).toContain("v1"); await preview();
  expect(wrapper!.get('[data-test="warehouse-state-preview"]').text()).toContain(parent);
  expect(wrapper!.get('[data-test="warehouse-state-preview"]').text()).toContain("schema：v1");
  expect(fetch).not.toHaveBeenCalled(); await confirm();
  const [url, options] = vi.mocked(fetch).mock.calls[0]!;
  expect(url).toBe(`/api/wms/locations/${id}/state`); expect(options!.method).toBe("PUT");
  expect(JSON.parse(String(options!.body))).toEqual({ expectedVersion: version, enabled: true, reason: "Ready" });
  expect(wrapper!.text()).toContain(after); expect(wrapper!.text()).toContain("不是当前状态");
});

it.each(["write:Enabled", ...locationFields, ...warehouseFields.map(field => `parent:${field}`)])("requires each current location and parent field: %s", async field => {
  if (field.startsWith("write:")) sessionState.subject!.grants[2]!.fields = ["Code", "Name", "WarehouseId", "PackId", "SchemaVersion"];
  else if (field.startsWith("parent:")) sessionState.subject!.grants[0]!.fields = warehouseFields.filter(value => `parent:${value}` !== field);
  else sessionState.subject!.grants[1]!.fields = locationFields.filter(value => value !== field);
  panel(); expect(wrapper!.get('[role="alert"]').text()).toContain("字段权限"); expect(wrapper!.find("form").exists()).toBe(false);
  expect(fetch).not.toHaveBeenCalled();
});

it("keeps the original location tuple after unknown and does not retry online without confirmation", async () => {
  vi.mocked(fetch).mockResolvedValueOnce(new Response("", { status: 503 }));
  panel(); await preview(); await confirm();
  const first = vi.mocked(fetch).mock.calls[0]!;
  vi.spyOn(navigator, "onLine", "get").mockReturnValue(false); dispatchEvent(new Event("offline")); await flushPromises();
  expect(wrapper!.get('[data-test="retry-warehouse-state"]').attributes("disabled")).toBeDefined();
  vi.spyOn(navigator, "onLine", "get").mockReturnValue(true); dispatchEvent(new Event("online")); await flushPromises();
  expect(fetch).toHaveBeenCalledTimes(1);
  await wrapper!.get('[data-test="retry-warehouse-state"]').trigger("click"); await flushPromises();
  const second = vi.mocked(fetch).mock.calls[1]!;
  expect(second[0]).toBe(first[0]); expect(second[1]!.body).toBe(first[1]!.body);
  expect(new Headers(second[1]!.headers).get("Idempotency-Key")).toBe(new Headers(first[1]!.headers).get("Idempotency-Key"));
});

it("treats schema rejection as definite without retrying", async () => {
  vi.mocked(fetch).mockResolvedValue(new Response("", { status: 422 }));
  panel(); await preview(); await confirm();
  expect(wrapper!.text()).toContain("schema 校验拒绝"); expect(wrapper!.find('[data-test="retry-warehouse-state"]').exists()).toBe(false);
});

it("keeps timeout unknown at the exact deadline and ignores late location success", async () => {
  vi.useFakeTimers();
  let resolve!: (value: Response) => void;
  vi.mocked(fetch).mockImplementationOnce(() => new Promise<Response>(done => { resolve = done; }));
  panel(); await preview(); await confirm();
  await vi.advanceTimersByTimeAsync(9999); expect(wrapper!.find('[data-test="retry-warehouse-state"]').exists()).toBe(false);
  await vi.advanceTimersByTimeAsync(1); await flushPromises();
  expect(wrapper!.find('[data-test="retry-warehouse-state"]').exists()).toBe(true);
  resolve(new Response(JSON.stringify(receipt))); await flushPromises();
  expect(wrapper!.find('[data-test="read-state-warehouse"]').exists()).toBe(false);
  expect(fetch).toHaveBeenCalledTimes(1);
});

it.each([{ ...receipt, id: parent }, { ...receipt, version }, { ...receipt, decision: 2 }, { ...receipt, warehouseId: parent }])(
  "rejects a malformed location success receipt as unknown: %j", async malformed => {
    vi.mocked(fetch).mockResolvedValue(new Response(JSON.stringify(malformed)));
    panel(); await preview(); await confirm();
    expect(wrapper!.find('[data-test="read-state-warehouse"]').exists()).toBe(false);
    expect(wrapper!.find('[data-test="retry-warehouse-state"]').exists()).toBe(true);
  });

it("invalidates the original tuple when pinned lineage changes even if the id/version are unchanged", async () => {
  panel(); await preview();
  await wrapper!.setProps({ resource: { ...resource, definition: { ...row, schemaVersion: "v2" } } }); await flushPromises();
  expect(wrapper!.find('[data-test="warehouse-state-preview"]').exists()).toBe(false);
  expect(wrapper!.get('[role="alert"]').text()).toContain("已改变"); expect(fetch).not.toHaveBeenCalled();
});

it("preserves actual parent location panel through unknown, same operation recovery and explicit location GET", async () => {
  vi.mocked(fetch).mockResolvedValueOnce(new Response(JSON.stringify(row)))
    .mockResolvedValueOnce(new Response("", { status: 503 }))
    .mockResolvedValueOnce(new Response(JSON.stringify(receipt)))
    .mockResolvedValueOnce(new Response(JSON.stringify({ ...row, enabled: true, version: after })));
  wrapper = mount(MasterDataPage);
  await wrapper.get('[name="resourceKind"]').setValue("locations");
  await wrapper.get('[name="resourceId"]').setValue(id); await wrapper.get("form").trigger("submit"); await flushPromises();
  await wrapper.get('[data-test="prepare-warehouse-state"]').trigger("click"); await flushPromises();
  const child = wrapper.getComponent(MasterDataStateChange);
  await child.get('[name="warehouseStateReason"]').setValue("Ready"); await child.get("form").trigger("submit");
  await child.get('[data-test="confirm-warehouse-state"]').trigger("click"); await flushPromises();
  expect(wrapper.findComponent(MasterDataStateChange).exists()).toBe(true);
  expect(wrapper.get('[name="resourceKind"]').attributes("disabled")).toBeDefined();
  expect(wrapper.getComponent(MasterDataCatalog).props("readable")).toBe(false);
  await child.get('[data-test="retry-warehouse-state"]').trigger("click"); await flushPromises();
  expect(fetch).toHaveBeenCalledTimes(3);
  await child.get('[data-test="read-state-warehouse"]').trigger("click"); await flushPromises();
  expect(fetch).toHaveBeenCalledTimes(4); expect(vi.mocked(fetch).mock.calls[3]![0]).toBe(`/api/wms/locations/${id}`);
  expect(wrapper.findComponent(MasterDataStateChange).exists()).toBe(false); expect(wrapper.text()).toContain(after);
});
