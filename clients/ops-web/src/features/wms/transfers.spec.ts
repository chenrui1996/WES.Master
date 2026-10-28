import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { forgetIdentity, setAccessToken } from "../identity/identity-api";
import { executeTransfer, readTransfer, transferActionError, transferInputError, type TransferDetail } from "./transfers";

const id = "017966cc-f1dd-412d-8e45-adb475608842";
const target = "6a3ea5df-8407-4af1-8017-5e00d31e4d3e";
const next = "c682cb8c-94c9-44f4-9c64-86db33361e82";
function detail(): TransferDetail {
  return { id, code: "TR-1", warehouseId: id, state: 1, version: id, createdAt: "2026-10-29T00:00:00Z",
    lines: [{ id, executionId: next, sourceBalanceId: id, targetBalanceId: target, itemId: id, unitId: id, batch: "", precision: 0,
      containerId: id, sourceLocationId: id, targetLocationId: target, branch: 0, plannedQuantity: "3.000000",
      reservedQuantity: "3.000000", movedQuantity: "0.000000", closedQuantity: "0.000000",
      executionStatus: 0, postingStatus: 0, sourceVersion: id, targetVersion: target }] };
}
beforeEach(() => {
  setAccessToken("fixture", "2099-01-01T00:00:00Z");
  vi.stubGlobal("fetch", vi.fn(async () => new Response(JSON.stringify({ decision: 0, record: detail() }))));
});
afterEach(() => { vi.unstubAllGlobals(); forgetIdentity(); });
it("reads exact transfer detail with separate progress, execution and posting state", async () => {
  expect(await readTransfer(id, new AbortController().signal)).toEqual(detail());
});
it.each([
  (value: TransferDetail) => { Object.assign(value, { tenantId: id }); },
  (value: TransferDetail) => { value.id = target; },
  (value: TransferDetail) => { value.lines[0]!.targetBalanceId = id; },
  (value: TransferDetail) => { value.lines[0]!.targetLocationId = id; },
  (value: TransferDetail) => { value.lines[0]!.plannedQuantity = "3"; },
  (value: TransferDetail) => { value.lines[0]!.movedQuantity = "4.000000"; },
  (value: TransferDetail) => { value.lines[0]!.reservedQuantity = "0.000000"; },
  (value: TransferDetail) => { value.lines[0]!.plannedQuantity = "3.500000"; },
  (value: TransferDetail) => { value.lines[0]!.postingStatus = 2; },
  (value: TransferDetail) => { value.state = 3; },
  (value: TransferDetail) => { value.lines.push({ ...value.lines[0]! }); },
])("rejects malformed or internally inconsistent detail %#", async change => {
  const record = detail(); change(record);
  vi.mocked(fetch).mockResolvedValueOnce(new Response(JSON.stringify({ decision: 0, record })));
  await expect(readTransfer(id, new AbortController().signal)).rejects.toThrow("移库响应无效");
});
it.each([1, 3] as const)("keeps arrived unsynchronized evidence distinct with posting state %s", async postingStatus => {
  const record = detail(); record.state = 2; record.lines[0]!.executionStatus = 2; record.lines[0]!.postingStatus = postingStatus;
  vi.mocked(fetch).mockResolvedValueOnce(new Response(JSON.stringify({ decision: 0, record })));
  expect((await readTransfer(id, new AbortController().signal)).lines[0]!.movedQuantity).toBe("0.000000");
});
it("requires the complete frozen source/target balance-version set for approval", () => {
  const record = detail(); record.state = 0; record.lines[0]!.reservedQuantity = "0.000000";
  const command = { kind: "approval" as const, id, operation: next, input: { expectedVersion: id,
    balances: [{ balanceId: id, expectedVersion: id }, { balanceId: target, expectedVersion: target }] } };
  expect(transferActionError(record, command)).toBe("");
  expect(transferActionError(record, { ...command, input: { ...command.input, balances: command.input.balances.slice(0, 1) } })).toContain("完整");
});
it("validates scans and blocks fake transport completion", () => {
  const record = detail(); record.state = 2; record.lines[0]!.executionStatus = 1;
  const command = { kind: "arrivals" as const, id, operation: next, input: { expectedVersion: id, lineId: id,
    sourceLocationId: id, targetLocationId: target, containerId: id, quantity: "3", reason: "已扫描确认到达" } };
  expect(transferActionError(record, command)).toBe("");
  expect(transferActionError(record, { ...command, input: { ...command.input, quantity: "2" } })).toContain("完整");
  expect(transferActionError(record, { ...command, input: { ...command.input, targetLocationId: next } })).toContain("扫码");
  record.lines[0]!.branch = 1;
  expect(transferActionError(record, command)).toContain("运输");
});
it("only closes safe unstarted remainder, never transit, unknown or pending arrivals", () => {
  const record = detail();
  const command = { kind: "closure" as const, id, operation: next, input: { expectedVersion: id, reason: "确认未执行" } };
  expect(transferActionError(record, command)).toBe("");
  for (const executionStatus of [1, 2, 3] as const) {
    record.lines[0]!.executionStatus = executionStatus;
    expect(transferActionError(record, command)).not.toBe("");
  }
});
it.each(["arrivals", "unknown", "closure"] as const)("limits %s reason to the owner contract256 before HTTP", kind => {
  const common = { operation: next, id };
  const command = kind === "arrivals"
    ? { ...common, kind, input: { expectedVersion: id, lineId: id, sourceLocationId: id,
      targetLocationId: target, containerId: id, quantity: "3", reason: "a".repeat(256) } }
    : kind === "unknown"
      ? { ...common, kind, input: { expectedVersion: id, lineId: id, reason: "a".repeat(256) } }
      : { ...common, kind, input: { expectedVersion: id, reason: "a".repeat(256) } };
  expect(transferInputError(command)).toBe("");
  command.input.reason = "a".repeat(257);
  expect(transferInputError(command)).not.toBe("");
  expect(fetch).not.toHaveBeenCalled();
});
it.each(["0", "-1", "3e0", "03", "1.0000001", "1000000000", " 3"])("rejects invalid planned quantity %s", quantity => {
  expect(transferInputError({ kind: "create", operation: next, input: { code: "TR-1", warehouseId: id,
    lines: [{ id, sourceBalanceId: id, targetBalanceId: target, quantity, branch: 0 }] } })).not.toBe("");
});
it("freezes minimal original receipt, named path and exact original quantity spelling", async () => {
  vi.mocked(fetch).mockResolvedValueOnce(new Response(JSON.stringify({ decision: 1, id, version: next })));
  const command = { kind: "arrivals" as const, operation: next, id, input: { expectedVersion: id, lineId: id,
    sourceLocationId: id, targetLocationId: target, containerId: id, quantity: "3.000", reason: "到达" } };
  expect(await executeTransfer(command, new AbortController().signal)).toEqual({ decision: 1, id, version: next });
  const [url, options] = vi.mocked(fetch).mock.calls[0]!;
  expect(url).toBe(`/api/wms/transfers/${id}/arrivals`);
  expect(options?.body).toBe(JSON.stringify(command.input));
  expect(new Headers(options?.headers).get("Idempotency-Key")).toBe(next);
});
it.each([
  { decision: 0, id, version: id }, { decision: 0, id: target, version: next }, { decision: 2, id, version: next },
  { decision: 0, id, version: next, moved: true }, { decision: 0, id, version: "bad" },
])("rejects malformed success as unknown %#", async receipt => {
  vi.mocked(fetch).mockResolvedValueOnce(new Response(JSON.stringify(receipt)));
  await expect(executeTransfer({ kind: "dispatch", id, operation: next,
    input: { expectedVersion: id, lineId: id } }, new AbortController().signal)).rejects.toThrow("结果不明");
});
