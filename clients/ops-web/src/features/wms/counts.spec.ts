import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { forgetIdentity, setAccessToken } from "../identity/identity-api";
import { countInputError, countActionError, executeCount, readCount, readCountReview, type CountDetail } from "./counts";

const id = "017966cc-f1dd-412d-8e45-adb475608842";
const version = "6a3ea5df-8407-4af1-8017-5e00d31e4d3e";
const next = "c682cb8c-94c9-44f4-9c64-86db33361e82";
function detail(): CountDetail {
  return { id, code: "COUNT-1", warehouseId: id, locationId: id, state: 1, version, createdAt: "2026-10-28T00:00:00Z",
    snapshotAt: "2026-10-28T00:01:00Z", lockOwnerId: id, reminderDue: false, policy: "WES-RULES-1.0/RULE-06/1800s",
    lines: [{ id, itemId: id, unitId: id, batch: "", precision: 0, containerId: id, quantity: null, countedBy: null, countedAt: null }] };
}
beforeEach(() => {
  setAccessToken("fixture", "2099-01-01T00:00:00Z");
  vi.stubGlobal("fetch", vi.fn(async () => new Response(JSON.stringify({ decision: 0, record: detail() }))));
});
afterEach(() => { vi.unstubAllGlobals(); forgetIdentity(); });
it("reads blind legal dimensions without exposing the private stock snapshot", async () => {
  expect(await readCount(id, new AbortController().signal)).toEqual(detail());
  expect(vi.mocked(fetch).mock.calls[0]![0]).toBe(`/api/wms/counts/${id}`);
});
it.each([3, 4] as const)("reads terminal state %s with no active lock owner", async state => {
  const record = detail(); record.state = state; record.lockOwnerId = null;
  if (state === 3) Object.assign(record.lines[0]!, { quantity: "10.000000", countedBy: id, countedAt: "2026-10-28T00:02:00Z" });
  vi.mocked(fetch).mockResolvedValueOnce(new Response(JSON.stringify({ decision: 0, record })));
  expect((await readCount(id, new AbortController().signal)).state).toBe(state);
});
it.each([
  (value: CountDetail) => { Object.assign(value.lines[0]!, { bookQuantity: "10.000000" }); },
  (value: CountDetail) => { Object.assign(value.lines[0]!, { difference: "0.000000" }); },
  (value: CountDetail) => { Object.assign(value.lines[0]!, { balanceId: id }); },
  (value: CountDetail) => { Object.assign(value, { privateSnapshot: "forbidden" }); },
  (value: CountDetail) => { value.id = next; },
  (value: CountDetail) => { value.lines[0]!.quantity = "0.000000"; },
  (value: CountDetail) => { value.lines.push({ ...value.lines[0]! }); },
  (value: CountDetail) => { value.snapshotAt = null; },
  (value: CountDetail) => { value.state = 2; },
  (value: CountDetail) => { value.policy = "automatic-unlock"; },
])("rejects malformed or privacy-breaking blind response %#", async change => {
  const record = detail(); change(record);
  vi.mocked(fetch).mockResolvedValueOnce(new Response(JSON.stringify({ decision: 0, record })));
  await expect(readCount(id, new AbortController().signal)).rejects.toThrow("盘点响应无效");
});
it.each(["-2.000000", "0.000000", "2.000000"])("parses separately requested review with exact signed delta %s", async difference => {
  const record = detail(); record.state = 2;
  Object.assign(record.lines[0]!, { quantity: `${10 + Number(difference)}.000000`, countedBy: id, countedAt: "2026-10-28T00:02:00Z",
    bookQuantity: "10.000000", difference });
  vi.mocked(fetch).mockResolvedValueOnce(new Response(JSON.stringify({ decision: 0, record })));
  expect((await readCountReview(id, new AbortController().signal)).lines[0]!.difference).toBe(difference);
  expect(vi.mocked(fetch).mock.calls[0]![0]).toBe(`/api/wms/counts/${id}/review`);
});
it("rejects review delta not equal to measured minus book", async () => {
  const record = detail();
  Object.assign(record.lines[0]!, { quantity: "8.000000", countedBy: id, countedAt: "2026-10-28T00:02:00Z",
    bookQuantity: "10.000000", difference: "2.000000" });
  vi.mocked(fetch).mockResolvedValueOnce(new Response(JSON.stringify({ decision: 0, record })));
  await expect(readCountReview(id, new AbortController().signal)).rejects.toThrow("盘点响应无效");
});
it.each(["-1", "1e0", "1.0000001", " 1", "1 ", "10000000000000000000000", "NaN"])("rejects invalid measured quantity %s before HTTP", quantity => {
  expect(countInputError({ kind: "records", id, operation: next, input: { expectedVersion: version,
    lineId: id, quantity, locationId: id, itemId: id, containerId: id, reason: "实盘" } })).not.toBe("");
  expect(fetch).not.toHaveBeenCalled();
});
it.each(["1000000000", "9999999999999999999999.999999"])("accepts absolute inventory quantity %s, not just movement-size bounds", quantity => {
  expect(countInputError({ kind: "records", id, operation: next, input: { expectedVersion: version,
    lineId: id, quantity, locationId: id, itemId: id, containerId: id, reason: "实盘" } })).toBe("");
});
it("accepts zero count but checks scan tuple and actual unit precision", () => {
  const command = { kind: "records" as const, id, operation: next, input: { expectedVersion: version,
    lineId: id, quantity: "0", locationId: id, itemId: id, containerId: id, reason: "无实物" } };
  expect(countActionError(detail(), command)).toBe("");
  expect(countActionError(detail(), { ...command, input: { ...command.input, quantity: "1.5" } })).toContain("精度");
  expect(countActionError(detail(), { ...command, input: { ...command.input, containerId: next } })).toContain("扫码");
});
it("does not allow a stale or invalidated measurement to be adjusted", () => {
  const command = { kind: "adjustment" as const, id, operation: next, input: { expectedVersion: version, reason: "授权差异" } };
  expect(countActionError(detail(), command)).not.toBe("");
  const invalidated = detail(); invalidated.state = 4;
  expect(countActionError(invalidated, command)).not.toBe("");
});
it.each([
  { decision: 0, id, version }, { decision: 0, id: next, version: next }, { decision: 2, id, version: next },
  { decision: 0, id, version: next, bookQuantity: "10.000000" }, { decision: 0, id, version: "bad" },
])("treats a malformed success receipt as unknown %#", async receipt => {
  vi.mocked(fetch).mockResolvedValueOnce(new Response(JSON.stringify(receipt)));
  await expect(executeCount({ kind: "invalidation", operation: next, id,
    input: { expectedVersion: version, reason: "人工解锁" } }, new AbortController().signal)).rejects.toThrow("结果不明");
});
it("freezes named path, zero decimal spelling, exact input and original key", async () => {
  vi.mocked(fetch).mockResolvedValueOnce(new Response(JSON.stringify({ decision: 1, id, version: next })));
  const command = { kind: "records" as const, id, operation: next, input: { expectedVersion: version,
    lineId: id, quantity: "0.000", locationId: id, itemId: id, containerId: id, reason: "无实物" } };
  expect(await executeCount(command, new AbortController().signal)).toEqual({ decision: 1, id, version: next });
  const [url, options] = vi.mocked(fetch).mock.calls[0]!;
  expect(url).toBe(`/api/wms/counts/${id}/records`);
  expect(options?.body).toBe(JSON.stringify(command.input));
  expect(new Headers(options?.headers).get("Idempotency-Key")).toBe(next);
});
