import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import MasterDataCatalog from "./MasterDataCatalog.vue";
import { forgetIdentity, sessionState, setAccessToken } from "../identity/identity-api";

const id = "a897b598-39f8-45b0-a533-c1b60814796f";
const second = "a949a8dd-2d46-469b-8adf-07251415a20c";
const row = { id, code: "WH-01", name: "目录仓库", enabled: false, version: second, createdAt: "2026-10-27T00:00:00Z" };
let wrapper: VueWrapper | undefined;
beforeEach(() => {
  setAccessToken("fixture", "2099-01-01T00:00:00Z");
  sessionState.subject = { tenantId: "t", userId: "u", sessionId: "s", credentialVersion: null, requiresPasswordChange: false, grants: [] };
  vi.stubGlobal("fetch", vi.fn(async () => new Response(JSON.stringify({ rows: [row], offset: 0, limit: 25 }))));
});
afterEach(() => { wrapper?.unmount(); wrapper = undefined; vi.restoreAllMocks(); vi.unstubAllGlobals(); forgetIdentity(); });
function open() { wrapper = mount(MasterDataCatalog, { props: { kind: "warehouses", readable: true } }); return wrapper; }
async function load() { await wrapper!.get("form").trigger("submit"); await flushPromises(); }

it("loads only explicitly and emits only the identity of a currently confirmed row", async () => {
  open(); expect(fetch).not.toHaveBeenCalled(); await load();
  expect(vi.mocked(fetch).mock.calls[0]?.[0]).toBe("/api/wms/warehouses?offset=0&limit=25");
  expect(wrapper!.get('[data-test="master-data-catalog"]').text()).toContain("目录仓库");
  await wrapper!.get('[data-test="select-master-data"]').trigger("click");
  expect(wrapper!.emitted("select")).toEqual([[id]]);
});

it("sends explicit exact code/status filters and location warehouse scope", async () => {
  open(); await wrapper!.setProps({ kind: "locations" });
  await wrapper!.get('[name="catalogCode"]').setValue("a1");
  await wrapper!.get('[name="catalogEnabled"]').setValue("false");
  await wrapper!.get('[name="catalogWarehouse"]').setValue(second);
  vi.stubGlobal("fetch", vi.fn(async () => new Response(JSON.stringify({ rows: [], offset: 0, limit: 25 }))));
  await load();
  expect(vi.mocked(fetch).mock.calls[0]?.[0]).toBe(`/api/wms/locations?offset=0&limit=25&code=A1&enabled=false&warehouseId=${second}`);
  expect(wrapper!.text()).toContain("当前授权范围");
});

it.each(["kind", "filter", "scope"])("cancels a request and discards late results after changing %s", async changed => {
  let finish!: (response: Response) => void;
  const request = vi.fn((_url: string, _options: RequestInit) => new Promise<Response>(resolve => { finish = resolve; }));
  vi.stubGlobal("fetch", request); open(); await load();
  if (changed === "kind") await wrapper!.setProps({ kind: "locations" });
  else if (changed === "filter") await wrapper!.get('[name="catalogCode"]').setValue("OTHER");
  else sessionState.subject!.userId = "other";
  await flushPromises(); expect(request.mock.calls[0]![1].signal?.aborted).toBe(true);
  finish(new Response(JSON.stringify({ rows: [row], offset: 0, limit: 25 }))); await flushPromises();
  expect(wrapper!.find('[data-test="select-master-data"]').exists()).toBe(false);
});

it("retains a failed same-query refresh as stale but disables row selection", async () => {
  vi.stubGlobal("fetch", vi.fn().mockResolvedValueOnce(new Response(JSON.stringify({ rows: [row], offset: 0, limit: 25 })))
    .mockResolvedValueOnce(new Response(JSON.stringify({ code: "Unavailable", correlationId: "catalog-correlation" }), { status: 503 })));
  open(); await load(); await load();
  expect(wrapper!.text()).toContain("陈旧");
  expect(wrapper!.text()).toContain("catalog-correlation");
  expect(wrapper!.get('[data-test="select-master-data"]').attributes("disabled")).toBeDefined();
});

it.each([401, 403, 404])("clears previous catalog on HTTP %s", async status => {
  vi.stubGlobal("fetch", vi.fn().mockResolvedValueOnce(new Response(JSON.stringify({ rows: [row], offset: 0, limit: 25 })))
    .mockResolvedValueOnce(new Response("{}", { status })));
  open(); await load(); await load();
  expect(wrapper!.find('[data-test="select-master-data"]').exists()).toBe(false);
});

it.each([
  { rows: [row, row], offset: 0, limit: 25 }, { rows: [row], offset: 25, limit: 25 },
  { rows: [{ ...row, enabled: 0 }], offset: 0, limit: 25 }, { rows: [row], offset: 0, limit: 25, total: 1 },
  { rows: [{ ...row, id: second, code: "Z" }, row], offset: 0, limit: 25 },
])("rejects malformed, duplicate or wrong-page data %#", async value => {
  vi.stubGlobal("fetch", vi.fn(async () => new Response(JSON.stringify(value))));
  open(); await load();
  expect(wrapper!.find('[data-test="select-master-data"]').exists()).toBe(false);
  expect(wrapper!.text()).toContain("响应无效");
});

it("uses the shared realtime pager and does not reuse old rows for a different page", async () => {
  const rows = Array.from({ length: 25 }, (_, i) => ({ ...row, id: `${i.toString(16).padStart(8, "0")}-39f8-45b0-a533-c1b60814796f`, code: `WH-${i.toString().padStart(2, "0")}` }));
  vi.stubGlobal("fetch", vi.fn().mockResolvedValueOnce(new Response(JSON.stringify({ rows, offset: 0, limit: 25 })))
    .mockResolvedValueOnce(new Response(JSON.stringify({ rows: [], offset: 25, limit: 25 }))));
  open(); await load();
  await wrapper!.get('[data-test="next-page"]').trigger("click"); await flushPromises();
  expect(vi.mocked(fetch).mock.calls[1]?.[0]).toBe("/api/wms/warehouses?offset=25&limit=25");
  expect(wrapper!.findAll('[data-test="select-master-data"]')).toHaveLength(0);
  expect(wrapper!.text()).toContain("实时");
});

it("keeps loaded rows stale offline and does not send or replay after reconnect", async () => {
  let online = true; vi.spyOn(navigator, "onLine", "get").mockImplementation(() => online);
  open(); await load();
  online = false; window.dispatchEvent(new Event("offline")); await flushPromises(); await load();
  expect(wrapper!.get('[data-test="select-master-data"]').attributes("disabled")).toBeDefined();
  expect(wrapper!.text()).toContain("陈旧");
  online = true; window.dispatchEvent(new Event("online")); await flushPromises();
  expect(fetch).toHaveBeenCalledTimes(1);
  expect(wrapper!.get('[data-test="select-master-data"]').attributes("disabled")).toBeDefined();
});

it("does not fetch without the complete current projection permission", async () => {
  open(); await wrapper!.setProps({ readable: false }); await load();
  expect(fetch).not.toHaveBeenCalled();
});

it("discards a late failure after a filter change and a successful different query", async () => {
  let fail!: (error: Error) => void;
  vi.stubGlobal("fetch", vi.fn().mockImplementationOnce(() => new Promise<Response>((_, reject) => { fail = reject; }))
    .mockResolvedValueOnce(new Response(JSON.stringify({ rows: [{ ...row, code: "OTHER" }], offset: 0, limit: 25 }))));
  open(); await load();
  await wrapper!.get('[name="catalogCode"]').setValue("OTHER"); await load();
  fail(new Error("old-error")); await flushPromises();
  expect(wrapper!.get('[data-test="master-data-catalog"]').text()).toContain("OTHER");
  expect(wrapper!.text()).not.toContain("old-error");
});

it.each(["code", "enabled", "warehouse"])("rejects rows that do not match the requested %s filter", async filter => {
  open();
  if (filter === "code") await wrapper!.get('[name="catalogCode"]').setValue("OTHER");
  else if (filter === "enabled") await wrapper!.get('[name="catalogEnabled"]').setValue("true");
  else {
    await wrapper!.setProps({ kind: "locations" });
    await wrapper!.get('[name="catalogWarehouse"]').setValue(second);
    vi.stubGlobal("fetch", vi.fn(async () => new Response(JSON.stringify({
      rows: [{ ...row, warehouseId: id, packId: id, schemaVersion: "v1" }], offset: 0, limit: 25,
    }))));
  }
  await load();
  expect(wrapper!.find('[data-test="select-master-data"]').exists()).toBe(false);
  expect(wrapper!.text()).toContain("响应无效");
});

it.each(["code", "warehouse"])("rejects invalid %s filters before sending a query", async filter => {
  open();
  if (filter === "code") await wrapper!.get('[name="catalogCode"]').setValue("bad code");
  else {
    await wrapper!.setProps({ kind: "locations" });
    await wrapper!.get('[name="catalogWarehouse"]').setValue("not-a-uuid");
  }
  await load();
  expect(fetch).not.toHaveBeenCalled();
  expect(wrapper!.find('[role="alert"]').exists()).toBe(true);
});

it("resets to page one when the shared page size changes", async () => {
  vi.stubGlobal("fetch", vi.fn().mockResolvedValueOnce(new Response(JSON.stringify({ rows: [row], offset: 0, limit: 25 })))
    .mockResolvedValueOnce(new Response(JSON.stringify({ rows: [row], offset: 0, limit: 100 }))));
  open(); await load();
  await wrapper!.get('nav select').setValue("100"); await flushPromises();
  expect(vi.mocked(fetch).mock.calls[1]?.[0]).toBe("/api/wms/warehouses?offset=0&limit=100");
  expect(wrapper!.text()).toContain("最多 100 条");
});

it("removes old rows when refreshed JSON is corrupt instead of retaining a plausible success page", async () => {
  vi.stubGlobal("fetch", vi.fn().mockResolvedValueOnce(new Response(JSON.stringify({ rows: [row], offset: 0, limit: 25 })))
    .mockResolvedValueOnce(new Response("not valid json")));
  open(); await load(); await load();
  expect(wrapper!.find('[data-test="select-master-data"]').exists()).toBe(false);
  expect(wrapper!.text()).toContain("响应无效");
});
