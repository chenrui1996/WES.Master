import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import InventoryPage from "./InventoryPage.vue";
import { forgetIdentity, sessionState, setAccessToken } from "../identity/identity-api";
import { inventoryFields, queryInventory } from "./inventory";

const id = "77966ccd-f1dd-412d-8e45-adb475608842";
const row = { id, warehouseId: id, locationId: id, itemId: id, batch: "", containerId: id, unitId: id, precision: 6,
  quantity: "9999999999999999999999.999999", reserved: "3.000001", inTransit: "3.000001", unknown: "3.000001",
  protected: "3.000001", available: "9999999999999999999996.999998", version: id };
const page = (rows: unknown[] = [row], offset = 0, limit = 25) => new Response(JSON.stringify({ rows, offset, limit }));
let wrapper: VueWrapper | undefined;
async function read() { await wrapper!.get("form").trigger("submit"); await flushPromises(); }
beforeEach(() => {
  setAccessToken("fixture", "2099-01-01T00:00:00Z");
  sessionState.subject = { tenantId: "t1", userId: "u1", sessionId: "s1", requiresPasswordChange: false,
    credentialVersion: null, grants: [{ permission: "wms.inventory.read", scope: 2, departments: [], fields: [...inventoryFields] }] };
  vi.stubGlobal("fetch", vi.fn(async () => page()));
});
afterEach(() => { wrapper?.unmount(); wrapper = undefined; vi.unstubAllGlobals(); vi.restoreAllMocks(); forgetIdentity(); });
it("reads explicit lossless quantities without inventing allocation authority or a cross-unit sum", async () => {
  wrapper = mount(InventoryPage); expect(fetch).not.toHaveBeenCalled(); await read();
  expect(fetch).toHaveBeenCalledWith("/api/wms/inventory?offset=0&limit=25", expect.objectContaining({ cache: "no-store" }));
  expect(wrapper.get('[data-test="inventory-rows"]').text()).toContain(row.quantity);
  expect(wrapper.get('[data-test="inventory-rows"]').text()).toContain(row.available);
  expect(wrapper.text()).toContain("不跨单位求和"); expect(wrapper.text()).toContain("不是分配许可");
  expect(wrapper.find('[type="number"]').exists()).toBe(false);
});
it.each(inventoryFields)("requires current All field %s", async field => {
  sessionState.subject!.grants[0]!.fields = inventoryFields.filter(value => value !== field);
  wrapper = mount(InventoryPage); await read(); expect(fetch).not.toHaveBeenCalled();
});
it.each([0, 1])("rejects scope %s for tenant-owned inventory", async scope => {
  sessionState.subject!.grants[0]!.scope = scope; wrapper = mount(InventoryPage); await read(); expect(fetch).not.toHaveBeenCalled();
});
it.each([
  { ...row, quantity: 1 }, { ...row, quantity: "1e10" }, { ...row, quantity: "01.000000" },
  { ...row, available: "-1.000000" }, { ...row, protected: "3.000002" },
  { ...row, reserved: "4.000000" }, { ...row, quantity: "10000000000000000000000.000000" },
  { ...row, precision: 7 }, { ...row, precision: 0 }, { ...row, id: "bad" },
  { ...row, warehouseId: "" }, { ...row, version: "bad" }, { ...row, batch: " bad" },
  { ...row, observedLocation: "invented" },
])("rejects invalid or success-shaped expanded stock %#", async invalid => {
  wrapper = mount(InventoryPage); await read();
  vi.mocked(fetch).mockResolvedValueOnce(page([invalid])); await read();
  expect(wrapper.find('[data-test="inventory-rows"]').exists()).toBe(false);
  expect(wrapper.text()).toContain("无效");
});
it.each([
  () => page([row, row]), () => page([], 25), () => page([], 0, 50), () => new Response("{"),
  () => new Response(JSON.stringify({ rows: [], offset: 0, limit: 25, total: 0 })),
])("rejects invalid whole page %#", async response => {
  vi.mocked(fetch).mockResolvedValueOnce(response()); wrapper = mount(InventoryPage); await read();
  expect(wrapper.text()).toContain("无效");
});
it("clears on filters, sends exact dimensions and supports the explicit no-batch filter", async () => {
  wrapper = mount(InventoryPage); await read();
  await wrapper.get('[name="warehouseId"]').setValue(id);
  expect(wrapper.find('[data-test="inventory-rows"]').exists()).toBe(false);
  await wrapper.get('[name="batchMode"]').setValue("exact");
  await read();
  expect(fetch).toHaveBeenLastCalledWith(`/api/wms/inventory?offset=0&limit=25&warehouseId=${id}&batch=`, expect.anything());
  expect(fetch).toHaveBeenCalledTimes(2);
});
it("rejects invalid UUID and batch locally without trimming", async () => {
  await expect(queryInventory(0, 25, { warehouseId: "bad" }, new AbortController().signal)).rejects.toThrow("UUID");
  await expect(queryInventory(0, 25, { batch: " bad" }, new AbortController().signal)).rejects.toThrow("批次");
  expect(fetch).not.toHaveBeenCalled();
});
it("rejects a response outside the requested dimension", async () => {
  wrapper = mount(InventoryPage);
  await wrapper.get('[name="warehouseId"]').setValue("77966ccd-f1dd-412d-8e45-adb475608843"); await read();
  expect(wrapper.text()).toContain("无效"); expect(wrapper.find('[data-test="inventory-rows"]').exists()).toBe(false);
});
it.each([401, 403, 404])("clears old inventory on HTTP %i", async status => {
  wrapper = mount(InventoryPage); await read();
  vi.mocked(fetch).mockResolvedValueOnce(new Response(JSON.stringify({ code: "Denied", correlationId: "safe-id" }), { status }));
  await read(); expect(wrapper.find('[data-test="inventory-rows"]').exists()).toBe(false);
});
it("keeps a failed identical query explicitly stale, not a zero balance", async () => {
  wrapper = mount(InventoryPage); await read();
  vi.mocked(fetch).mockResolvedValueOnce(new Response(JSON.stringify({ code: "Unavailable", correlationId: "safe-id" }), { status: 503 }));
  await read(); expect(wrapper.get('[data-test="inventory-rows"]').text()).toContain(row.quantity);
  expect(wrapper.text()).toContain("陈旧"); expect(wrapper.text()).toContain("safe-id");
});
it.each(["scope", "filter", "offline", "unmount"])("cancels %s and ignores late inventory", async change => {
  let finish!: (value: Response) => void; let signal: AbortSignal | undefined;
  vi.mocked(fetch).mockImplementationOnce((_, options) => {
    signal = options?.signal ?? undefined; return new Promise(resolve => { finish = resolve; });
  });
  wrapper = mount(InventoryPage); await wrapper.get("form").trigger("submit");
  if (change === "scope") sessionState.subject!.sessionId = "s2";
  if (change === "filter") await wrapper.get('[name="warehouseId"]').setValue(id);
  if (change === "offline") { vi.spyOn(navigator, "onLine", "get").mockReturnValue(false); dispatchEvent(new Event("offline")); }
  if (change === "unmount") { wrapper.unmount(); wrapper = undefined; }
  await flushPromises(); expect(signal?.aborted).toBe(true);
  finish(page()); await flushPromises(); expect(wrapper?.find('[data-test="inventory-rows"]').exists() ?? false).toBe(false);
});
it("does not automatically refresh on reconnect", async () => {
  wrapper = mount(InventoryPage); await read();
  vi.spyOn(navigator, "onLine", "get").mockReturnValue(false); dispatchEvent(new Event("offline")); await flushPromises();
  await read(); expect(fetch).toHaveBeenCalledTimes(1);
  vi.spyOn(navigator, "onLine", "get").mockReturnValue(true); dispatchEvent(new Event("online")); await flushPromises();
  expect(fetch).toHaveBeenCalledTimes(1);
});
it("presents an authorized empty page without creating stock or hiding an error", async () => {
  vi.mocked(fetch).mockResolvedValueOnce(page([])); wrapper = mount(InventoryPage); await read();
  expect(wrapper.text()).toContain("当前授权筛选没有库存维度"); expect(wrapper.text()).not.toContain(row.quantity);
});
it.each([[-1, 25], [10001, 25], [0.5, 25], [0, 1]])("rejects bounds %s/%s before HTTP", async (offset, limit) => {
  await expect(queryInventory(offset, limit, {}, new AbortController().signal)).rejects.toThrow("范围");
  expect(fetch).not.toHaveBeenCalled();
});
it("clears loaded inventory immediately on field revocation", async () => {
  wrapper = mount(InventoryPage); await read(); sessionState.subject!.grants[0]!.fields = ["Id"];
  await flushPromises(); expect(wrapper.find('[data-test="inventory-rows"]').exists()).toBe(false);
  await read(); expect(fetch).toHaveBeenCalledTimes(1);
});
it("uses the current filters for paging and returns to the first page when resizing", async () => {
  vi.mocked(fetch).mockResolvedValueOnce(page(Array.from({ length: 25 }, (_, index) => ({
    ...row, id: `77966ccd-f1dd-412d-8e45-${String(index).padStart(12, "0")}`,
  }))));
  wrapper = mount(InventoryPage); await wrapper.get('[name="itemId"]').setValue(id); await read();
  vi.mocked(fetch).mockResolvedValueOnce(page([], 25));
  await wrapper.get('[data-test="next-page"]').trigger("click"); await flushPromises();
  expect(fetch).toHaveBeenLastCalledWith(`/api/wms/inventory?offset=25&limit=25&itemId=${id}`, expect.anything());
  vi.mocked(fetch).mockResolvedValueOnce(page([], 0, 50));
  await wrapper.get('nav select').setValue("50"); await flushPromises();
  expect(fetch).toHaveBeenLastCalledWith(`/api/wms/inventory?offset=0&limit=50&itemId=${id}`, expect.anything());
});
