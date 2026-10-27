import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import InboundCatalog from "./InboundCatalog.vue";
import InboundPage from "./InboundPage.vue";
import { forgetIdentity, sessionState, setAccessToken } from "../identity/identity-api";
import { inboundFields, queryInbound } from "./inbound";

const id = "77966ccd-f1dd-412d-8e45-adb475608842";
const other = "77966ccd-f1dd-412d-8e45-adb475608843";
const row = { id, code: "IN-001", warehouseId: id, state: 0, version: id, createdAt: "2026-10-27T00:00:00Z" };
const page = (rows: unknown[] = [row], offset = 0, limit = 25) => new Response(JSON.stringify({ rows, offset, limit }));
let wrapper: VueWrapper | undefined;
async function load() { await wrapper!.get("form").trigger("submit"); await flushPromises(); }
beforeEach(() => {
  setAccessToken("fixture", "2099-01-01T00:00:00Z");
  sessionState.subject = { tenantId: "t1", userId: "u1", sessionId: "s1", requiresPasswordChange: false,
    credentialVersion: null, grants: [{ permission: "wms.inbound.read", scope: 0, departments: [], fields: [...inboundFields] }] };
  vi.stubGlobal("fetch", vi.fn(async () => page()));
});
afterEach(() => { wrapper?.unmount(); wrapper = undefined; vi.unstubAllGlobals(); vi.restoreAllMocks(); forgetIdentity(); });
it("queries explicitly and emits only the selected real ID", async () => {
  wrapper = mount(InboundCatalog, { props: { readable: true, blocked: false } });
  expect(fetch).not.toHaveBeenCalled(); await load();
  expect(fetch).toHaveBeenCalledWith("/api/wms/inbound?offset=0&limit=25", expect.objectContaining({ cache: "no-store" }));
  await wrapper.get('[aria-label="读取入库单 IN-001 当前详情"]').trigger("click");
  expect(wrapper.emitted("select")).toEqual([[id]]);
});
it.each(["readable", "blocked"])("does not query when %s blocks access", async prop => {
  wrapper = mount(InboundCatalog, { props: { readable: prop !== "readable", blocked: prop === "blocked" } });
  await load(); expect(fetch).not.toHaveBeenCalled();
});
it("sends exact normalized filters and clears on filter edits", async () => {
  wrapper = mount(InboundCatalog, { props: { readable: true, blocked: false } }); await load();
  await wrapper.get('[name="inboundCode"]').setValue("in-001");
  expect(wrapper.find('[data-test="inbound-catalog"]').exists()).toBe(false);
  await wrapper.get('[name="inboundWarehouse"]').setValue(id); await wrapper.get('[name="inboundState"]').setValue("0");
  await load();
  expect(fetch).toHaveBeenLastCalledWith(`/api/wms/inbound?offset=0&limit=25&code=IN-001&warehouseId=${id}&state=0`, expect.anything());
});
it.each([
  [-1, 25, {}], [10001, 25, {}], [0, 1, {}], [0, 25, { code: " bad" }],
  [0, 25, { warehouseId: "bad" }], [0, 25, { state: 4 }], [0, 25, { state: 0.1 }],
] as const)("rejects invalid query %# before HTTP", async (offset, limit, filters) => {
  await expect(queryInbound(offset, limit, filters, new AbortController().signal)).rejects.toThrow();
  expect(fetch).not.toHaveBeenCalled();
});
it.each([
  { ...row, state: 4 }, { ...row, creator: id }, { ...row, code: "lower" }, { ...row, version: "bad" },
  { ...row, warehouseId: "bad" }, { ...row, createdAt: "2026-02-31T00:00:00Z" },
])("rejects malformed or expanded metadata %#", async invalid => {
  vi.mocked(fetch).mockResolvedValueOnce(page([invalid]));
  wrapper = mount(InboundCatalog, { props: { readable: true, blocked: false } }); await load();
  expect(wrapper.find('[data-test="inbound-catalog"]').exists()).toBe(false);
  expect(wrapper.text()).toContain("无效");
});
it.each([
  () => page([row, row]), () => page([row, { ...row, id: other, code: "A" }]), () => page([], 25),
  () => new Response(JSON.stringify({ rows: [], offset: 0, limit: 25, total: 0 })), () => new Response("{"),
])("rejects invalid whole page %#", async result => {
  vi.mocked(fetch).mockResolvedValueOnce(result());
  wrapper = mount(InboundCatalog, { props: { readable: true, blocked: false } }); await load();
  expect(wrapper.text()).toContain("无效");
});
it("rejects metadata outside the exact filters", async () => {
  wrapper = mount(InboundCatalog, { props: { readable: true, blocked: false } });
  await wrapper.get('[name="inboundState"]').setValue("1"); await load();
  expect(wrapper.text()).toContain("无效"); expect(wrapper.find('[data-test="inbound-catalog"]').exists()).toBe(false);
});
it("marks unavailable refresh stale and blocks stale selection", async () => {
  wrapper = mount(InboundCatalog, { props: { readable: true, blocked: false } }); await load();
  vi.mocked(fetch).mockResolvedValueOnce(new Response(JSON.stringify({ code: "Unavailable", correlationId: "safe-id" }), { status: 503 }));
  await load(); expect(wrapper.text()).toContain("陈旧目录");
  await wrapper.get('[aria-label="读取入库单 IN-001 当前详情"]').trigger("click");
  expect(wrapper.emitted("select")).toBeUndefined();
});
it("clears forbidden rows and notifies the parent", async () => {
  wrapper = mount(InboundCatalog, { props: { readable: true, blocked: false } }); await load();
  vi.mocked(fetch).mockResolvedValueOnce(new Response(JSON.stringify({ code: "Denied", correlationId: "safe-id" }), { status: 403 }));
  await load(); expect(wrapper.find('[data-test="inbound-catalog"]').exists()).toBe(false);
  expect(wrapper.emitted("denied")).toHaveLength(1);
});
it("does not infer global zero work from an authorized empty filter", async () => {
  vi.mocked(fetch).mockResolvedValueOnce(page([]));
  wrapper = mount(InboundCatalog, { props: { readable: true, blocked: false } }); await load();
  expect(wrapper.text()).toContain("当前授权范围及筛选下没有入库单");
});
it.each(["subject", "filter", "offline", "unmount"])("cancels %s and rejects late catalog responses", async change => {
  let finish!: (value: Response) => void; let signal: AbortSignal | undefined;
  vi.mocked(fetch).mockImplementationOnce((_, options) => {
    signal = options?.signal ?? undefined; return new Promise(resolve => { finish = resolve; });
  });
  wrapper = mount(InboundCatalog, { props: { readable: true, blocked: false } }); await wrapper.get("form").trigger("submit");
  if (change === "subject") sessionState.subject!.sessionId = "s2";
  if (change === "filter") await wrapper.get('[name="inboundCode"]').setValue("OTHER");
  if (change === "offline") { vi.spyOn(navigator, "onLine", "get").mockReturnValue(false); dispatchEvent(new Event("offline")); }
  if (change === "unmount") { wrapper.unmount(); wrapper = undefined; }
  await flushPromises(); expect(signal?.aborted).toBe(true);
  finish(page()); await flushPromises(); expect(wrapper?.find('[data-test="inbound-catalog"]').exists() ?? false).toBe(false);
});
it("selects through the real parent and re-reads current detail instead of trusting metadata", async () => {
  vi.mocked(fetch).mockResolvedValueOnce(page()).mockResolvedValueOnce(new Response(JSON.stringify({ ...row,
    lines: [{ id, itemId: id, unitId: id, batch: "", precision: 0, planned: "5.000000", received: "0.000000", remaining: "5.000000" }],
    receipts: [] })));
  wrapper = mount(InboundPage);
  await wrapper.findAll("form")[1]!.trigger("submit"); await flushPromises();
  await wrapper.get('[aria-label="读取入库单 IN-001 当前详情"]').trigger("click"); await flushPromises();
  expect(fetch).toHaveBeenLastCalledWith(`/api/wms/inbound/${id}`, expect.anything());
  expect(wrapper.get('[data-test="inbound-detail"]').text()).toContain("5.000000");
  vi.mocked(fetch).mockResolvedValueOnce(new Response(JSON.stringify({ code: "Denied", correlationId: "safe-id" }), { status: 403 }));
  await wrapper.findAll("form")[1]!.trigger("submit"); await flushPromises();
  expect(wrapper.find('[data-test="inbound-detail"]').exists()).toBe(false);
  expect(wrapper.find('[data-test="inbound-catalog"]').exists()).toBe(false);
});
it("clears the catalog too when a detail reread is forbidden", async () => {
  wrapper = mount(InboundPage);
  await wrapper.findAll("form")[1]!.trigger("submit"); await flushPromises();
  vi.mocked(fetch).mockResolvedValueOnce(new Response(JSON.stringify({ code: "Denied", correlationId: "safe-id" }), { status: 403 }));
  await wrapper.get('[aria-label="读取入库单 IN-001 当前详情"]').trigger("click"); await flushPromises();
  expect(wrapper.find('[data-test="inbound-catalog"]').exists()).toBe(false);
});
