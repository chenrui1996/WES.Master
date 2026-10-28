import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import StockResourcesPanel from "./StockResourcesPanel.vue";
import MasterDataPage from "./MasterDataPage.vue";
import { forgetIdentity, sessionState, setAccessToken } from "../identity/identity-api";
import { locationFields, warehouseFields } from "./master-data";
import { mayRegisterStockResource, readStockResource, registerStockResource, type StockResourceCommand } from "./stock-resources";

const id = "77966ccd-f1dd-412d-8e45-adb475608842";
const parent = "319d21fd-930a-47cb-b6c1-d529f7c37a0d";
const version = "a029ce52-d810-405c-b349-c0ac9ebad74b";
const receipt = { decision: 0, id, version };
const definition = { id, code: "EA", name: "Each", countable: true, precision: 0, version, createdAt: "2026-10-28T00:00:00+00:00" };
const resourceFields: [string, string[]][] = [
  ["units", ["Code", "Name", "Countable", "Precision"]],
  ["items", ["Code", "Name", "BaseUnitId", "BatchRequired"]],
  ["containers", ["Code", "Name", "WarehouseId"]],
];
let wrapper: VueWrapper | undefined;
beforeEach(() => {
  setAccessToken("fixture", "2099-01-01T00:00:00Z");
  sessionState.subject = { tenantId: "t1", userId: "u1", sessionId: "s1", credentialVersion: null, requiresPasswordChange: false,
    grants: [
      { permission: "wms.warehouses.read", scope: 0, departments: [], fields: [...warehouseFields] },
      { permission: "wms.locations.read", scope: 0, departments: [], fields: [...locationFields] },
      ...resourceFields.flatMap(([kind, fields]) => [
        { permission: `wms.${kind}.write`, scope: 0, departments: [], fields: [...fields] },
        { permission: `wms.${kind}.read`, scope: 0, departments: [], fields: ["Id", ...fields, "Version", "CreatedAt"] },
      ]),
      { permission: "wms.dimensions.write", scope: 0, departments: [], fields: ["WarehouseId", "LocationId", "ItemId", "Batch", "ContainerId"] },
    ] };
  vi.spyOn(navigator, "onLine", "get").mockReturnValue(true);
  vi.stubGlobal("fetch", vi.fn(async () => new Response(JSON.stringify(receipt))));
});
afterEach(() => { wrapper?.unmount(); wrapper = undefined; vi.useRealTimers(); vi.restoreAllMocks(); vi.unstubAllGlobals(); forgetIdentity(); });
function panel() { wrapper = mount(StockResourcesPanel); }
async function preview() {
  await wrapper!.get('[name="stockResourceCode"]').setValue("ea"); await wrapper!.get('[name="stockResourceName"]').setValue("Each");
  await wrapper!.get('[data-test="stock-registration"]').trigger("submit"); await flushPromises();
}
async function confirm() { await wrapper!.get('[data-test="confirm-stock-resource"]').trigger("click"); await flushPromises(); }

it("previews every explicit unit field without requests then confirms only the strict tuple and explicitly reads", async () => {
  panel(); await preview(); expect(fetch).not.toHaveBeenCalled();
  const text = wrapper!.get('[data-test="stock-resource-preview"]').text();
  for (const field of ["code", "name", "countable", "precision"]) expect(text).toContain(field);
  await confirm();
  const [url, options] = vi.mocked(fetch).mock.calls[0]!;
  expect(url).toBe("/api/wms/units"); expect(options!.method).toBe("POST");
  expect(JSON.parse(String(options!.body))).toEqual({ code: "ea", name: "Each", countable: true, precision: 0 });
  expect(wrapper!.get('[data-test="stock-resource-receipt"]').text()).toContain(version);
  vi.mocked(fetch).mockResolvedValue(new Response(JSON.stringify(definition)));
  await wrapper!.get('[data-test="read-stock-resource"]').trigger("click"); await flushPromises();
  expect(vi.mocked(fetch).mock.calls[1]![0]).toBe(`/api/wms/units/${id}`);
  expect(wrapper!.get('[data-test="stock-resource-details"]').text()).toContain("当前读取已确认");
});

it("keeps the original kind/key/body through unknown and offline without automatic retry", async () => {
  vi.mocked(fetch).mockResolvedValueOnce(new Response("", { status: 503 }));
  panel(); await preview(); await confirm();
  const original = vi.mocked(fetch).mock.calls[0]!;
  expect(wrapper!.get('[name="stockResourceKind"]').attributes("disabled")).toBeDefined();
  vi.spyOn(navigator, "onLine", "get").mockReturnValue(false); dispatchEvent(new Event("offline")); await flushPromises();
  expect(wrapper!.get('[data-test="retry-stock-resource"]').attributes("disabled")).toBeDefined();
  vi.spyOn(navigator, "onLine", "get").mockReturnValue(true); dispatchEvent(new Event("online")); await flushPromises();
  expect(fetch).toHaveBeenCalledTimes(1);
  await wrapper!.get('[data-test="retry-stock-resource"]').trigger("click"); await flushPromises();
  const recovered = vi.mocked(fetch).mock.calls[1]!;
  expect(recovered[0]).toBe(original[0]); expect(recovered[1]!.body).toBe(original[1]!.body);
  expect(new Headers(recovered[1]!.headers).get("Idempotency-Key")).toBe(new Headers(original[1]!.headers).get("Idempotency-Key"));
  expect(wrapper!.text()).toContain(id);
  const next = wrapper!.findAll("button").find(button => button.text() === "开始下一项登记")!;
  expect(next.attributes("disabled")).toBeUndefined();
  await next.trigger("click"); await flushPromises();
  expect(wrapper!.find('[data-test="stock-registration"]').exists()).toBe(true);
});

it.each([{}, { ...receipt, decision: 3 }, { ...receipt, id: "" }, { ...receipt, version: "" }, { ...receipt, quantity: "0" }])(
  "treats a malformed registration success as unknown: %j", async malformed => {
    vi.mocked(fetch).mockResolvedValue(new Response(JSON.stringify(malformed)));
    panel(); await preview(); await confirm();
    expect(wrapper!.find('[data-test="stock-resource-receipt"]').exists()).toBe(false);
    expect(wrapper!.find('[data-test="retry-stock-resource"]').exists()).toBe(true);
  });

it("preserves timeout unknown at the exact 10-second threshold and ignores late success", async () => {
  vi.useFakeTimers(); let resolve!: (value: Response) => void;
  vi.mocked(fetch).mockImplementationOnce(() => new Promise<Response>(done => { resolve = done; }));
  panel(); await preview(); await confirm();
  await vi.advanceTimersByTimeAsync(9999); expect(wrapper!.find('[data-test="retry-stock-resource"]').exists()).toBe(false);
  await vi.advanceTimersByTimeAsync(1); await flushPromises(); expect(wrapper!.find('[data-test="retry-stock-resource"]').exists()).toBe(true);
  resolve(new Response(JSON.stringify(receipt))); await flushPromises();
  expect(wrapper!.find('[data-test="stock-resource-receipt"]').exists()).toBe(false);
});

it.each([400, 403, 404, 405, 409, 422])("treats definite HTTP %i as rejection without blind retry", async status => {
  vi.mocked(fetch).mockResolvedValue(new Response("", { status }));
  panel(); await preview(); await confirm();
  expect(wrapper!.find('[data-test="retry-stock-resource"]').exists()).toBe(false);
  expect(wrapper!.text()).toContain("被拒绝");
});

it.each(["Code", "Name", "Countable", "Precision"])("requires current unit write field %s", async missing => {
  const grant = sessionState.subject!.grants.find(grant => grant.permission === "wms.units.write")!;
  grant.fields = grant.fields.filter(field => field !== missing);
  panel(); expect(wrapper!.find('[data-test="stock-registration"]').exists()).toBe(false); expect(fetch).not.toHaveBeenCalled();
});

it("requires complete parent/material/unit/container reads for dimension registration", () => {
  expect(mayRegisterStockResource("dimensions")).toBe(true);
  sessionState.subject!.grants.find(grant => grant.permission === "wms.units.read")!.fields = ["Id"];
  expect(mayRegisterStockResource("dimensions")).toBe(false); expect(mayRegisterStockResource("items")).toBe(false);
});

it("rejects fractional countable precision without a request", async () => {
  panel(); await wrapper!.get('[name="stockPrecision"]').setValue("3"); await preview();
  expect(wrapper!.text()).toContain("计件单位必须为0"); expect(fetch).not.toHaveBeenCalled();
});

it("clears preview and read business data when the current identity changes", async () => {
  panel(); await preview();
  sessionState.subject = { ...sessionState.subject!, sessionId: "replacement" }; await flushPromises();
  expect(wrapper!.find('[data-test="stock-resource-preview"]').exists()).toBe(false);
  expect(wrapper!.text()).toContain("主体或权限已改变"); expect(fetch).not.toHaveBeenCalled();
});

it("actual parent keeps unknown mounted and excludes master-data queries", async () => {
  vi.mocked(fetch).mockResolvedValueOnce(new Response("", { status: 503 }));
  wrapper = mount(MasterDataPage); await wrapper.get('[data-test="open-stock-resources"]').trigger("click");
  const child = wrapper.getComponent(StockResourcesPanel);
  await child.get('[name="stockResourceCode"]').setValue("EA"); await child.get('[name="stockResourceName"]').setValue("Each");
  await child.get('[data-test="stock-registration"]').trigger("submit"); await child.get('[data-test="confirm-stock-resource"]').trigger("click"); await flushPromises();
  expect(wrapper.findComponent(StockResourcesPanel).exists()).toBe(true);
  expect(wrapper.find('[name="resourceId"]').exists()).toBe(false);
  await child.get('[data-test="retry-stock-resource"]').trigger("click"); await flushPromises();
  expect(fetch).toHaveBeenCalledTimes(2);
});

it("sends no quantity/unit/precision input for a zero dimension and routes each registration kind exactly", async () => {
  const commands: StockResourceCommand[] = [
    { operation: id, kind: "items", input: { code: "I", name: "Item", baseUnitId: parent, batchRequired: false } },
    { operation: id, kind: "containers", input: { code: "C", name: "Container", warehouseId: parent } },
    { operation: id, kind: "dimensions", input: { warehouseId: parent, locationId: id, itemId: parent, batch: "", containerId: id } },
  ];
  for (const command of commands) await registerStockResource(command, new AbortController().signal);
  expect(vi.mocked(fetch).mock.calls.map(call => call[0])).toEqual(["/api/wms/items", "/api/wms/containers", "/api/wms/dimensions"]);
  const body = JSON.parse(String(vi.mocked(fetch).mock.calls[2]![1]!.body));
  expect(Object.keys(body)).toEqual(["warehouseId", "locationId", "itemId", "batch", "containerId"]);
});

it.each(["items", "containers", "dimensions"])("previews and submits the actual %s field branch", async kind => {
  panel(); await wrapper!.get('[name="stockResourceKind"]').setValue(kind);
  if (kind !== "dimensions") {
    await wrapper!.get('[name="stockResourceCode"]').setValue("RESOURCE");
    await wrapper!.get('[name="stockResourceName"]').setValue("Resource");
  }
  if (kind === "items") {
    await wrapper!.get('[name="stockBaseUnitId"]').setValue(parent);
    await wrapper!.get('[name="stockBatchRequired"]').setValue(true);
  } else {
    await wrapper!.get('[name="stockWarehouseId"]').setValue(parent);
    if (kind === "dimensions") {
      await wrapper!.get('[name="stockLocationId"]').setValue(id); await wrapper!.get('[name="stockItemId"]').setValue(parent);
      await wrapper!.get('[name="stockBatch"]').setValue("LOT-A"); await wrapper!.get('[name="stockContainerId"]').setValue(id);
    }
  }
  await wrapper!.get('[data-test="stock-registration"]').trigger("submit"); await flushPromises();
  expect(fetch).not.toHaveBeenCalled(); const previewText = wrapper!.get('[data-test="stock-resource-preview"]').text();
  await confirm(); const [url, options] = vi.mocked(fetch).mock.calls[0]!;
  expect(url).toBe(`/api/wms/${kind}`);
  const expected = kind === "items" ? { code: "RESOURCE", name: "Resource", baseUnitId: parent, batchRequired: true }
    : kind === "containers" ? { code: "RESOURCE", name: "Resource", warehouseId: parent }
    : { warehouseId: parent, locationId: id, itemId: parent, batch: "LOT-A", containerId: id };
  expect(JSON.parse(String(options!.body))).toEqual(expected);
  for (const field of Object.keys(expected)) expect(previewText).toContain(field);
});

it("retains unresolved uncertainty after a definite recovery rejection without allowing a new key", async () => {
  vi.mocked(fetch).mockResolvedValueOnce(new Response("", { status: 503 })).mockResolvedValueOnce(new Response("", { status: 403 }));
  panel(); await preview(); await confirm(); await wrapper!.get('[data-test="retry-stock-resource"]').trigger("click"); await flushPromises();
  expect(wrapper!.text()).toContain("先前不明结果仍需查证");
  expect(wrapper!.findAll("button").find(button => button.text() === "开始下一项登记")!.attributes("disabled")).toBeDefined();
  expect(wrapper!.find('[data-test="stock-resource-receipt"]').exists()).toBe(false);
});

it.each(["offline", "timeout", "identity"])("clears pending current reads and ignores late replies on %s", async event => {
  vi.useFakeTimers(); let resolve!: (value: Response) => void;
  vi.mocked(fetch).mockImplementationOnce(() => new Promise<Response>(done => { resolve = done; }));
  panel(); await wrapper!.get('[name="stockLookupId"]').setValue(id);
  await wrapper!.get('[data-test="stock-lookup"]').trigger("submit"); await flushPromises();
  if (event === "offline") {
    vi.spyOn(navigator, "onLine", "get").mockReturnValue(false); dispatchEvent(new Event("offline")); await flushPromises();
    vi.spyOn(navigator, "onLine", "get").mockReturnValue(true); dispatchEvent(new Event("online")); await flushPromises();
    expect(fetch).toHaveBeenCalledTimes(1);
  } else if (event === "timeout") {
    await vi.advanceTimersByTimeAsync(9999); expect(wrapper!.text()).not.toContain("10秒内未取得当前资源详情");
    await vi.advanceTimersByTimeAsync(1); await flushPromises(); expect(wrapper!.text()).toContain("10秒内未取得当前资源详情");
  } else {
    sessionState.subject!.grants = []; await flushPromises();
    expect(wrapper!.text()).toContain("当前主体或权限已改变");
  }
  resolve(new Response(JSON.stringify(definition))); await flushPromises();
  expect(wrapper!.find('[data-test="stock-resource-details"]').exists()).toBe(false);
});

it.each([{ ...definition, precision: 3 }, { ...definition, id: parent }, { ...definition, actorId: parent }, { ...definition, createdAt: "bad" }])(
  "rejects malformed or wrong-target current details: %j", async malformed => {
    vi.mocked(fetch).mockResolvedValue(new Response(JSON.stringify(malformed)));
    await expect(readStockResource("units", id, new AbortController().signal)).rejects.toThrow("响应无效");
  });
