import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import StockLedgerPage from "./StockLedgerPage.vue";
import { forgetIdentity, sessionState, setAccessToken } from "../identity/identity-api";
import { stockLedgerFields, queryStockLedger } from "./stock-ledger";

const id = "77966ccd-f1dd-412d-8e45-adb475608842";
const other = "77966ccd-f1dd-412d-8e45-adb475608843";
const row = { id, postingId: id, balanceId: id, warehouseId: id, locationId: id, itemId: id, batch: "", containerId: id,
  unitId: id, precision: 6, kind: 1, quantity: "-0.000001", before: "9999999999999999999999.999999",
  after: "9999999999999999999999.999998", sourceDocumentId: id, sourceLineId: id, executionUnitId: id,
  correlationId: id, occurredAt: "2026-10-27T01:02:03.123456+00:00" };
const page = (rows: unknown[] = [row], offset = 0, limit = 25) => new Response(JSON.stringify({ rows, offset, limit }));
let wrapper: VueWrapper | undefined;
async function read() { await wrapper!.get("form").trigger("submit"); await flushPromises(); }
beforeEach(() => {
  setAccessToken("fixture", "2099-01-01T00:00:00Z");
  sessionState.subject = { tenantId: "t1", userId: "u1", sessionId: "s1", requiresPasswordChange: false,
    credentialVersion: null, grants: [{ permission: "wms.ledger.read", scope: 2, departments: [], fields: [...stockLedgerFields] }] };
  vi.stubGlobal("fetch", vi.fn(async () => page()));
});
afterEach(() => { wrapper?.unmount(); wrapper = undefined; vi.unstubAllGlobals(); vi.restoreAllMocks(); forgetIdentity(); });
it("reads signed immutable amounts exactly without using Number or offering stock writes", async () => {
  wrapper = mount(StockLedgerPage); expect(fetch).not.toHaveBeenCalled(); await read();
  expect(fetch).toHaveBeenCalledWith("/api/wms/ledger?offset=0&limit=25", expect.objectContaining({ cache: "no-store" }));
  const list = wrapper.get('[data-test="stock-ledger-rows"]');
  for (const value of [row.quantity, row.before, row.after, row.sourceDocumentId, row.occurredAt]) expect(list.text()).toContain(value);
  expect(wrapper.text()).toContain("不是固定核对快照"); expect(wrapper.text()).toContain("不跨单位求和");
  expect(wrapper.find('[type="number"]').exists()).toBe(false); expect(wrapper.find("a").exists()).toBe(false);
});
it.each([
  [3, "2.000000", "10.000000", "12.000000", "调整入 AdjustmentIn"],
  [4, "-2.000000", "10.000000", "8.000000", "调整出 AdjustmentOut"],
] as const)("reads and filters count adjustment kind %s without dropping ledger history", async (kind, quantity, before, after, label) => {
  vi.mocked(fetch).mockResolvedValue(page([{ ...row, kind, quantity, before, after, precision: 0 }]));
  wrapper = mount(StockLedgerPage);
  await wrapper.get('[name="kind"]').setValue(String(kind)); await read();
  expect(wrapper.get('[data-test="stock-ledger-rows"]').text()).toContain(label);
  expect(new URL(String(vi.mocked(fetch).mock.calls[0]![0]), "https://localhost").searchParams.get("kind")).toBe(String(kind));
});
it.each(stockLedgerFields)("requires independent current All field %s", async field => {
  sessionState.subject!.grants[0]!.fields = stockLedgerFields.filter(value => value !== field);
  wrapper = mount(StockLedgerPage); await read(); expect(fetch).not.toHaveBeenCalled();
});
it.each([0, 1])("rejects scope %s instead of treating actor as stock owner", async scope => {
  sessionState.subject!.grants[0]!.scope = scope; wrapper = mount(StockLedgerPage); await read(); expect(fetch).not.toHaveBeenCalled();
});
it("does not borrow inventory read permission", async () => {
  sessionState.subject!.grants[0]!.permission = "wms.inventory.read"; wrapper = mount(StockLedgerPage); await read();
  expect(fetch).not.toHaveBeenCalled();
});
it.each([
  { ...row, quantity: -1 }, { ...row, quantity: "1e10" }, { ...row, quantity: "-0.000000" }, { ...row, quantity: "-01.000001" },
  { ...row, quantity: "-1000000000.000000" }, { ...row, after: "9999999999999999999999.999999" },
  { ...row, before: "-1.000000" }, { ...row, kind: 0 }, { ...row, kind: 3 }, { ...row, precision: 0 },
  { ...row, sourceDocumentId: "bad" }, { ...row, correlationId: "00000000-0000-0000-0000-000000000000" },
  { ...row, occurredAt: "2026-02-30T00:00:00Z" }, { ...row, occurredAt: "2026-10-27T01:02:03.1234561Z" },
  { ...row, occurredAt: "2026-10-27T01:02:03+01:00" }, { ...row, batch: " bad" }, { ...row, actorId: id },
])("rejects inconsistent, expanded or lossy history %# and clears prior results", async invalid => {
  wrapper = mount(StockLedgerPage); await read(); vi.mocked(fetch).mockResolvedValueOnce(page([invalid])); await read();
  expect(wrapper.find('[data-test="stock-ledger-rows"]').exists()).toBe(false); expect(wrapper.text()).toContain("无效");
});
it.each([
  () => page([row, row]), () => page([], 25), () => page([], 0, 50), () => new Response("{"),
  () => new Response(JSON.stringify({ rows: [], offset: 0, limit: 25, total: 0 })),
  () => page([{ ...row, id: other }, row]),
  () => page([row, { ...row, id: other, occurredAt: "2026-10-27T01:02:03.123455Z" }]),
])("rejects malformed or unordered whole page %#", async response => {
  vi.mocked(fetch).mockResolvedValueOnce(response()); wrapper = mount(StockLedgerPage); await read();
  expect(wrapper.text()).toContain("无效");
});
it("preserves microsecond ordering beyond Date millisecond resolution", async () => {
  vi.mocked(fetch).mockResolvedValueOnce(page([{ ...row, id: other }, { ...row, occurredAt: "2026-10-27T01:02:03.123457Z" }]));
  wrapper = mount(StockLedgerPage); await read(); expect(wrapper.findAll('[data-test="stock-ledger-rows"] li')).toHaveLength(2);
});
it("sends exact source, kind, batch and half-open UTC filters without trimming", async () => {
  wrapper = mount(StockLedgerPage); await read();
  await wrapper.get('[name="sourceDocumentId"]').setValue(id); expect(wrapper.find('[data-test="stock-ledger-rows"]').exists()).toBe(false);
  await wrapper.get('[name="correlationId"]').setValue(id); await wrapper.get('[name="kind"]').setValue("1");
  await wrapper.get('[name="batchMode"]').setValue("exact");
  await wrapper.get('[name="from"]').setValue("2026-10-27T01:02:03.123456Z");
  await wrapper.get('[name="before"]').setValue("2026-10-27T01:02:03.123457Z");
  await read();
  const url = new URL(vi.mocked(fetch).mock.calls.at(-1)![0]!.toString(), "https://localhost");
  expect(Object.fromEntries(url.searchParams)).toEqual({ offset: "0", limit: "25", sourceDocumentId: id,
    correlationId: id, kind: "1", batch: "", from: "2026-10-27T01:02:03.123456Z", before: "2026-10-27T01:02:03.123457Z" });
});
it.each([
  { warehouseId: "bad" }, { sourceDocumentId: "bad" }, { correlationId: "" }, { batch: " bad" }, { kind: 9 },
  { from: "2026-02-30T00:00:00Z" }, { from: "2026-10-27T01:02:03+01:00" },
  { from: "2026-10-27T01:02:03.1234561Z" }, { from: "0999-01-01T00:00:00Z" },
  { from: "2026-10-27T01:02:03.123457Z", before: "2026-10-27T01:02:03.123456Z" },
])("rejects invalid filters before HTTP %#", async filters => {
  await expect(queryStockLedger(0, 25, filters, new AbortController().signal)).rejects.toThrow();
  expect(fetch).not.toHaveBeenCalled();
});
it.each([[-1, 25], [10001, 25], [0.5, 25], [0, 1]])("rejects invalid bounds %s/%s", async (offset, limit) => {
  await expect(queryStockLedger(offset, limit, {}, new AbortController().signal)).rejects.toThrow("范围"); expect(fetch).not.toHaveBeenCalled();
});
it.each([
  { warehouseId: other }, { sourceDocumentId: other }, { correlationId: other }, { batch: "B" }, { kind: 0 },
  { from: "2026-10-27T01:02:03.123457Z" }, { before: "2026-10-27T01:02:03.123456Z" },
])("rejects facts outside requested filters %#", async filters => {
  await expect(queryStockLedger(0, 25, filters, new AbortController().signal)).rejects.toThrow("无效");
});
it.each([401, 403, 404])("clears retained history after HTTP %s", async status => {
  wrapper = mount(StockLedgerPage); await read();
  vi.mocked(fetch).mockResolvedValueOnce(new Response(JSON.stringify({ code: "Denied", correlationId: "safe-id" }), { status }));
  await read(); expect(wrapper.find('[data-test="stock-ledger-rows"]').exists()).toBe(false);
});
it("marks identical-query unavailability stale instead of replacing it with empty history", async () => {
  wrapper = mount(StockLedgerPage); await read();
  vi.mocked(fetch).mockResolvedValueOnce(new Response(JSON.stringify({ code: "Unavailable", correlationId: "safe-id" }), { status: 503 }));
  await read(); expect(wrapper.get('[data-test="stock-ledger-rows"]').text()).toContain(row.before);
  expect(wrapper.text()).toContain("陈旧"); expect(wrapper.text()).toContain("safe-id");
});
it.each(["scope", "filter", "offline", "unmount"])("cancels %s and ignores late history", async change => {
  let finish!: (value: Response) => void; let signal: AbortSignal | undefined;
  vi.mocked(fetch).mockImplementationOnce((_, options) => {
    signal = options?.signal ?? undefined; return new Promise(resolve => { finish = resolve; });
  });
  wrapper = mount(StockLedgerPage); await wrapper.get("form").trigger("submit");
  if (change === "scope") sessionState.subject!.sessionId = "s2";
  if (change === "filter") await wrapper.get('[name="sourceDocumentId"]').setValue(id);
  if (change === "offline") { vi.spyOn(navigator, "onLine", "get").mockReturnValue(false); dispatchEvent(new Event("offline")); }
  if (change === "unmount") { wrapper.unmount(); wrapper = undefined; }
  await flushPromises(); expect(signal?.aborted).toBe(true);
  finish(page()); await flushPromises(); expect(wrapper?.find('[data-test="stock-ledger-rows"]').exists() ?? false).toBe(false);
});
it("clears immediately on field revocation and does not auto-refresh on reconnect", async () => {
  wrapper = mount(StockLedgerPage); await read();
  vi.spyOn(navigator, "onLine", "get").mockReturnValue(false); dispatchEvent(new Event("offline")); await flushPromises();
  vi.spyOn(navigator, "onLine", "get").mockReturnValue(true); dispatchEvent(new Event("online")); await flushPromises();
  expect(fetch).toHaveBeenCalledTimes(1); expect(wrapper.text()).toContain("陈旧");
  sessionState.subject!.grants[0]!.fields = ["Id"]; await flushPromises();
  expect(wrapper.find('[data-test="stock-ledger-rows"]').exists()).toBe(false); await read(); expect(fetch).toHaveBeenCalledTimes(1);
});
it("shows authorized empty history without claiming no physical movement", async () => {
  vi.mocked(fetch).mockResolvedValueOnce(page([])); wrapper = mount(StockLedgerPage); await read();
  expect(wrapper.text()).toContain("当前授权筛选没有流水"); expect(wrapper.text()).toContain("不表示没有物理搬运");
});
it("paginates exact filters and resets to first page on changed page size", async () => {
  const rows = Array.from({ length: 25 }, (_, index) => ({ ...row, id: `77966ccd-f1dd-412d-8e45-${String(index).padStart(12, "0")}` }));
  vi.mocked(fetch).mockResolvedValueOnce(page(rows)); wrapper = mount(StockLedgerPage);
  await wrapper.get('[name="itemId"]').setValue(id); await read();
  vi.mocked(fetch).mockResolvedValueOnce(page([], 25)); await wrapper.get('[data-test="next-page"]').trigger("click"); await flushPromises();
  expect(fetch).toHaveBeenLastCalledWith(`/api/wms/ledger?offset=25&limit=25&itemId=${id}`, expect.anything());
  vi.mocked(fetch).mockResolvedValueOnce(page([], 0, 50));
  await wrapper.get('nav[aria-label="流水分页"] select').setValue("50"); await flushPromises();
  expect(fetch).toHaveBeenLastCalledWith(`/api/wms/ledger?offset=0&limit=50&itemId=${id}`, expect.anything());
});
