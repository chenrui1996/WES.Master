import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import MasterDataPage from "./MasterDataPage.vue";
import { forgetIdentity, sessionState, setAccessToken } from "../identity/identity-api";
import WarehouseCreation from "./MasterDataCreation.vue";

const id = "77966ccd-f1dd-412d-8e45-adb475608842";
const second = "a029ce52-d810-405c-b349-c0ac9ebad74b";
const row = { id, code: "WH-01", name: "仓库定义", enabled: false, version: second, createdAt: "2026-10-27T00:00:00+00:00" };
const location = { ...row, code: "A1", name: "库位定义", warehouseId: second, packId: id, schemaVersion: "v1" };
const fields = ["Id", "Code", "Name", "Enabled", "Version", "CreatedAt"];
let wrapper: VueWrapper | undefined;
beforeEach(() => {
  setAccessToken("fixture", "2099-01-01T00:00:00Z");
  sessionState.subject = { tenantId: "t1", userId: "u1", sessionId: "s1", credentialVersion: null, requiresPasswordChange: false,
    grants: [
      { permission: "wms.warehouses.read", scope: 0, departments: [], fields: [...fields] },
      { permission: "wms.locations.read", scope: 0, departments: [], fields: [...fields, "WarehouseId", "PackId", "SchemaVersion"] },
    ] };
  vi.stubGlobal("fetch", vi.fn(async () => new Response(JSON.stringify(row))));
});
afterEach(() => { wrapper?.unmount(); wrapper = undefined; vi.restoreAllMocks(); vi.unstubAllGlobals(); forgetIdentity(); });
async function read(target = id) {
  await wrapper!.get('[name="resourceId"]').setValue(target);
  await wrapper!.get("form").trigger("submit"); await flushPromises();
}

it("reads only after explicit submit and renders safe disabled warehouse details without write actions", async () => {
  wrapper = mount(MasterDataPage);
  expect(fetch).not.toHaveBeenCalled();
  await read();
  expect(vi.mocked(fetch).mock.calls[0]?.[0]).toBe(`/api/wms/warehouses/${id}`);
  const details = wrapper.get('[data-test="master-data-details"]');
  expect(details.text()).toContain("仓库定义");
  expect(details.text()).toContain(second);
  expect(details.text()).toContain("主数据禁用");
  expect(wrapper.text()).toContain("不代表可分配");
  expect(wrapper.findAll("button").map(button => button.text())).toContain("读取当前详情");
  expect(wrapper.findAll("button").some(button => /创建|编辑|启用|禁用|绑定/.test(button.text()))).toBe(false);
});

it("selects the location boundary and displays complete pinned lineage", async () => {
  vi.stubGlobal("fetch", vi.fn(async () => new Response(JSON.stringify(location))));
  wrapper = mount(MasterDataPage);
  await wrapper.get('[name="resourceKind"]').setValue("locations");
  await read();
  expect(vi.mocked(fetch).mock.calls[0]?.[0]).toBe(`/api/wms/locations/${id}`);
  expect(wrapper.get('[data-test="master-data-details"]').text()).toContain("v1");
  expect(wrapper.text()).toContain("Pack");
});

it.each(["", "bad", "00000000-0000-0000-0000-000000000000", "../admin/users"])("rejects invalid UUID %s without sending it", async target => {
  wrapper = mount(MasterDataPage);
  await read(target);
  expect(fetch).not.toHaveBeenCalled();
  expect(wrapper.text()).toContain("有效的非空 UUID");
});

it.each([
  { ...row, id: second }, { ...row, enabled: "false" }, { ...row, version: "" },
  { ...row, createdAt: "2026-02-30T00:00:00Z" }, { ...row, code: "../wh" },
  { ...row, name: " name " }, { ...row, name: "line\nname" }, { ...row, createdAt: "2026-10-27T01:00:00+01:00" },
  { ...row, secret: "not-an-approved-field" },
])("rejects malformed or wrong-identity projections %#", async payload => {
  vi.stubGlobal("fetch", vi.fn(async () => new Response(JSON.stringify(payload))));
  wrapper = mount(MasterDataPage); await read();
  expect(wrapper.find('[data-test="master-data-details"]').exists()).toBe(false);
  expect(wrapper.text()).toContain("响应无效");
});

it("rejects a location without complete schema lineage", async () => {
  vi.stubGlobal("fetch", vi.fn(async () => new Response(JSON.stringify({ ...location, schemaVersion: "" }))));
  wrapper = mount(MasterDataPage);
  await wrapper.get('[name="resourceKind"]').setValue("locations"); await read();
  expect(wrapper.find('[data-test="master-data-details"]').exists()).toBe(false);
  expect(wrapper.text()).toContain("响应无效");
});

it("clears previous data on invalid JSON instead of retaining a success-shaped projection", async () => {
  vi.stubGlobal("fetch", vi.fn().mockResolvedValueOnce(new Response(JSON.stringify(row)))
    .mockResolvedValueOnce(new Response("not valid json")));
  wrapper = mount(MasterDataPage); await read();
  await wrapper.get("form").trigger("submit"); await flushPromises();
  expect(wrapper.find('[data-test="master-data-details"]').exists()).toBe(false);
  expect(wrapper.text()).toContain("响应无效");
  expect(wrapper.text()).not.toContain("not valid json");
});

it.each(["id", "kind", "scope"])("discards late success when the %s changes", async changed => {
  let finish!: (response: Response) => void;
  const request = vi.fn((_url: string, _options: RequestInit) => new Promise<Response>(resolve => { finish = resolve; }));
  vi.stubGlobal("fetch", request);
  wrapper = mount(MasterDataPage); await read();
  const signal = request.mock.calls[0]![1].signal;
  if (changed === "id") await wrapper.get('[name="resourceId"]').setValue(second);
  else if (changed === "kind") await wrapper.get('[name="resourceKind"]').setValue("locations");
  else sessionState.subject = { ...sessionState.subject!, userId: "other" };
  await flushPromises();
  expect(signal?.aborted).toBe(true);
  finish(new Response(JSON.stringify(row))); await flushPromises();
  expect(wrapper.find('[data-test="master-data-details"]').exists()).toBe(false);
});

it("discards a late error after selecting another target and successfully reading it", async () => {
  let fail!: (error: Error) => void;
  vi.stubGlobal("fetch", vi.fn().mockImplementationOnce(() => new Promise<Response>((_, reject) => { fail = reject; }))
    .mockResolvedValueOnce(new Response(JSON.stringify({ ...row, id: second, code: "OTHER" }))));
  wrapper = mount(MasterDataPage); await read();
  await read(second);
  fail(new Error("old error")); await flushPromises();
  expect(wrapper.get('[data-test="master-data-details"]').text()).toContain("OTHER");
  expect(wrapper.text()).not.toContain("old error");
});

it("retains a failed same-target refresh only as stale and displays the safe correlation", async () => {
  vi.stubGlobal("fetch", vi.fn().mockResolvedValueOnce(new Response(JSON.stringify(row)))
    .mockResolvedValueOnce(new Response(JSON.stringify({ code: "Unavailable", correlationId: "wms-correlation" }), { status: 503 })));
  wrapper = mount(MasterDataPage); await read();
  await wrapper.get("form").trigger("submit"); await flushPromises();
  expect(wrapper.get('[data-test="master-data-details"]').text()).toContain("仓库定义");
  expect(wrapper.text()).toContain("陈旧");
  expect(wrapper.text()).toContain("wms-correlation");
});

it.each([401, 403, 404])("removes old resource data on HTTP %s", async status => {
  vi.stubGlobal("fetch", vi.fn().mockResolvedValueOnce(new Response(JSON.stringify(row)))
    .mockResolvedValueOnce(new Response("{}", { status })));
  wrapper = mount(MasterDataPage); await read();
  await wrapper.get("form").trigger("submit"); await flushPromises();
  expect(wrapper.find('[data-test="master-data-details"]').exists()).toBe(false);
  if (status === 404) expect(wrapper.text()).toContain("入口未装配");
});

it("clears loaded metadata immediately when a required field is revoked locally", async () => {
  wrapper = mount(MasterDataPage); await read();
  sessionState.subject!.grants[0]!.fields = ["Id"];
  await flushPromises();
  expect(wrapper.find('[data-test="master-data-details"]').exists()).toBe(false);
  expect(wrapper.get('button[type="submit"]').attributes("disabled")).toBeDefined();
});

it("does not allow a warehouse grant to read location metadata", async () => {
  sessionState.subject!.grants.pop();
  wrapper = mount(MasterDataPage);
  await wrapper.get('[name="resourceKind"]').setValue("locations");
  await read();
  expect(fetch).not.toHaveBeenCalled();
  expect(wrapper.text()).toContain("缺少当前资源类型");
});

it("defaults to locations when only the location projection is readable", async () => {
  sessionState.subject!.grants.shift();
  vi.stubGlobal("fetch", vi.fn(async () => new Response(JSON.stringify(location))));
  wrapper = mount(MasterDataPage); await read();
  expect(vi.mocked(fetch).mock.calls[0]?.[0]).toBe(`/api/wms/locations/${id}`);
});

it("marks offline data stale and does not request or replay after reconnect", async () => {
  let online = true;
  vi.spyOn(navigator, "onLine", "get").mockImplementation(() => online);
  wrapper = mount(MasterDataPage); await read();
  online = false; window.dispatchEvent(new Event("offline")); await flushPromises();
  expect(wrapper.text()).toContain("离线");
  expect(wrapper.text()).toContain("陈旧");
  await wrapper.get("form").trigger("submit"); await flushPromises();
  expect(fetch).toHaveBeenCalledTimes(1);
  online = true; window.dispatchEvent(new Event("online")); await flushPromises();
  expect(fetch).toHaveBeenCalledTimes(1);
  expect(wrapper.text()).toContain("陈旧");
});

it("aborts pending reads on offline transition and rejects their late response", async () => {
  let finish!: (response: Response) => void;
  const request = vi.fn((_url: string, _options: RequestInit) => new Promise<Response>(resolve => { finish = resolve; }));
  vi.stubGlobal("fetch", request);
  let online = true;
  vi.spyOn(navigator, "onLine", "get").mockImplementation(() => online);
  wrapper = mount(MasterDataPage); await read();
  online = false; window.dispatchEvent(new Event("offline")); await flushPromises();
  expect(request.mock.calls[0]![1].signal?.aborted).toBe(true);
  finish(new Response(JSON.stringify(row))); await flushPromises();
  expect(wrapper.find('[data-test="master-data-details"]').exists()).toBe(false);
  expect(wrapper.text()).toContain("离线");
});

it("aborts pending work when unmounted", async () => {
  const request = vi.fn((_url: string, _options: RequestInit) => new Promise<Response>(() => {}));
  vi.stubGlobal("fetch", request);
  wrapper = mount(MasterDataPage); await read();
  wrapper.unmount(); wrapper = undefined;
  expect(request.mock.calls[0]![1].signal?.aborted).toBe(true);
});

it("re-reads selected catalog identities instead of treating catalog rows as current details", async () => {
  vi.stubGlobal("fetch", vi.fn().mockResolvedValueOnce(new Response(JSON.stringify({ rows: [row], offset: 0, limit: 25 })))
    .mockResolvedValueOnce(new Response(JSON.stringify({ ...row, name: "重新读取的定义" }))));
  wrapper = mount(MasterDataPage);
  await wrapper.get(".master-data-catalog form").trigger("submit"); await flushPromises();
  await wrapper.get('[data-test="select-master-data"]').trigger("click"); await flushPromises();
  expect(vi.mocked(fetch).mock.calls[1]?.[0]).toBe(`/api/wms/warehouses/${id}`);
  expect(wrapper.get('[data-test="master-data-details"]').text()).toContain("重新读取的定义");
});

it("clears the confirmed catalog if current detail authority is denied", async () => {
  vi.stubGlobal("fetch", vi.fn().mockResolvedValueOnce(new Response(JSON.stringify({ rows: [row], offset: 0, limit: 25 })))
    .mockResolvedValueOnce(new Response(JSON.stringify(row)))
    .mockResolvedValueOnce(new Response("{}", { status: 403 })));
  wrapper = mount(MasterDataPage);
  await wrapper.get(".master-data-catalog form").trigger("submit"); await flushPromises();
  await wrapper.get('[data-test="select-master-data"]').trigger("click"); await flushPromises();
  await wrapper.get("form").trigger("submit"); await flushPromises();
  expect(wrapper.find('[data-test="master-data-details"]').exists()).toBe(false);
  expect(wrapper.find('[data-test="select-master-data"]').exists()).toBe(false);
});

it("clears current details if a refreshed catalog no longer has current full-field authority", async () => {
  vi.stubGlobal("fetch", vi.fn().mockResolvedValueOnce(new Response(JSON.stringify(row)))
    .mockResolvedValueOnce(new Response("{}", { status: 403 })));
  wrapper = mount(MasterDataPage); await read();
  await wrapper.get(".master-data-catalog form").trigger("submit"); await flushPromises();
  expect(wrapper.find('[data-test="master-data-details"]').exists()).toBe(false);
  expect(wrapper.text()).toContain("RequestFailed403");
});

it("integrates creation without retaining old query results or automatically reading its receipt", async () => {
  sessionState.subject!.grants.push({ permission: "wms.warehouses.write", scope: 0, departments: [], fields: ["Code", "Name"] });
  vi.mocked(fetch).mockResolvedValueOnce(new Response(JSON.stringify(row)))
    .mockResolvedValueOnce(new Response(JSON.stringify({ decision: 0, id: second, version: id }), { status: 201 }))
    .mockResolvedValueOnce(new Response(JSON.stringify({ ...row, id: second, code: "NEW-WH" })));
  wrapper = mount(MasterDataPage); await read();
  await wrapper.findAll("button").find(button => button.text() === "新建禁用仓库")!.trigger("click");
  expect(wrapper.find('[data-test="master-data-details"]').exists()).toBe(false);
  expect(wrapper.find(".master-data-query").exists()).toBe(false);
  const form = wrapper.getComponent(WarehouseCreation);
  await form.get('input[name="warehouseCode"]').setValue("new-wh");
  await form.get('input[name="warehouseName"]').setValue("New warehouse");
  await form.get("form").trigger("submit"); await flushPromises();
  await form.get('[data-test="confirm-warehouse"]').trigger("click"); await flushPromises();
  expect(fetch).toHaveBeenCalledTimes(2);
  expect(wrapper.find('[data-test="master-data-catalog"]').exists()).toBe(false);
  await form.get('[data-test="read-created-warehouse"]').trigger("click"); await flushPromises();
  expect(fetch).toHaveBeenCalledTimes(3);
  expect(vi.mocked(fetch).mock.calls[2]![0]).toBe(`/api/wms/warehouses/${second}`);
  expect(wrapper.get('[data-test="master-data-details"]').text()).toContain("NEW-WH");
});

it("integrates location creation exclusively and rechecks current location read only on explicit receipt action", async () => {
  sessionState.subject!.grants.push({ permission: "wms.locations.write", scope: 0, departments: [],
    fields: ["Code", "Name", "WarehouseId", "PackId", "SchemaVersion"] });
  vi.mocked(fetch).mockResolvedValueOnce(new Response(JSON.stringify(row)))
    .mockResolvedValueOnce(new Response(JSON.stringify({ decision: 0, id, version: second }), { status: 201 }))
    .mockResolvedValueOnce(new Response("{}", { status: 403 }));
  wrapper = mount(MasterDataPage); await read();
  await wrapper.findAll("button").find(button => button.text() === "新建禁用库位")!.trigger("click");
  expect(wrapper.find('[data-test="master-data-details"]').exists()).toBe(false);
  expect(wrapper.find(".master-data-query").exists()).toBe(false);
  expect(wrapper.findAll("button").some(button => button.text() === "新建禁用仓库")).toBe(false);
  const form = wrapper.getComponent(WarehouseCreation);
  for (const [name, value] of Object.entries({ locationCode: "new-loc", locationName: "New location",
    warehouseId: second, packId: id, schemaVersion: "v1" }))
    await form.get(`input[name="${name}"]`).setValue(value);
  await form.get("form").trigger("submit"); await flushPromises();
  await form.get('[data-test="confirm-location"]').trigger("click"); await flushPromises();
  expect(fetch).toHaveBeenCalledTimes(2);
  expect(wrapper.find('[data-test="master-data-catalog"]').exists()).toBe(false);
  await form.get('[data-test="read-created-location"]').trigger("click"); await flushPromises();
  expect(vi.mocked(fetch).mock.calls[2]![0]).toBe(`/api/wms/locations/${id}`);
  expect(wrapper.find('[data-test="master-data-details"]').exists()).toBe(false);
  expect(wrapper.text()).toContain("RequestFailed403");
});
