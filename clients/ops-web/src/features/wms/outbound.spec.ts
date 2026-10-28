import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { forgetIdentity, sessionState, setAccessToken } from "../identity/identity-api";
import { executeOutbound, outboundFields, outboundInputError, readOutbound, type OutboundDetail } from "./outbound";

const id = "017966cc-f1dd-412d-8e45-adb475608842";
const version = "6a3ea5df-8407-4af1-8017-5e00d31e4d3e";
const next = "c682cb8c-94c9-44f4-9c64-86db33361e82";
function detail(): OutboundDetail {
  return { id, code: "OUT-1", warehouseId: id, state: 0, version, createdAt: "2026-10-28T00:00:00Z",
    lines: [{ id, executionId: id, balanceId: id, itemId: id, unitId: id, batch: "", precision: 0, containerId: id, locationId: id,
      branch: 0, plannedQuantity: "7.000000", reservedQuantity: "0.000000", pickedQuantity: "0.000000",
      shortageQuantity: "0.000000", releasedQuantity: "0.000000", shippedQuantity: "0.000000" }] };
}
beforeEach(() => {
  setAccessToken("fixture", "2099-01-01T00:00:00Z");
  sessionState.subject = { tenantId: id, userId: id, sessionId: id, requiresPasswordChange: false, credentialVersion: null,
    grants: [{ permission: "wms.outbound.read", fields: [...outboundFields], scope: 0, departments: [] }] };
  vi.stubGlobal("fetch", vi.fn(async () => new Response(JSON.stringify({ decision: 0, record: detail() }))));
});
afterEach(() => { vi.unstubAllGlobals(); forgetIdentity(); });
it("reads exact authorized order identity, immutable dimensions and string quantities", async () => {
  expect(await readOutbound(id, new AbortController().signal)).toEqual(detail());
  expect(vi.mocked(fetch).mock.calls[0]![0]).toBe(`/api/wms/outbound/${id}`);
});
it.each([
  (value: OutboundDetail) => { value.id = next; },
  (value: OutboundDetail) => { value.lines[0]!.plannedQuantity = "7"; },
  (value: OutboundDetail) => { value.lines[0]!.plannedQuantity = "7.000001"; },
  (value: OutboundDetail) => { value.lines[0]!.shippedQuantity = "8.000000"; },
  (value: OutboundDetail) => { value.lines[0]!.pickedQuantity = "8.000000"; },
  (value: OutboundDetail) => { value.lines.push({ ...value.lines[0]! }); },
  (value: OutboundDetail) => { value.state = 3; },
  (value: OutboundDetail) => { value.state = 1; },
  (value: OutboundDetail) => { Object.assign(value.lines[0]!, { branch: 99 }); },
  (value: OutboundDetail) => { Object.assign(value, { privatePayload: "forbidden" }); },
])("rejects malformed, impossible or wrong-identity response %#", async change => {
  const value = detail(); change(value);
  vi.mocked(fetch).mockResolvedValueOnce(new Response(JSON.stringify({ decision: 0, record: value })));
  await expect(readOutbound(id, new AbortController().signal)).rejects.toThrow("出库响应无效");
});
it.each([" 1", "1 ", "1e0", "-1", "0", "1.0000001", "1000000000", "NaN"])("rejects invalid draft quantity %s", quantity => {
  expect(outboundInputError({ kind: "create", operation: id, input: { code: "OUT-1", warehouseId: id,
    lines: [{ id, balanceId: id, quantity, branch: 0 }] } })).not.toBe("");
});
it("rejects duplicate source balances and missing approval versions before requests", () => {
  const line = { id, balanceId: id, quantity: "7", branch: 0 as const };
  expect(outboundInputError({ kind: "create", operation: id, input: { code: "OUT-1", warehouseId: id,
    lines: [line, { ...line, id: next }] } })).toContain("重复");
  expect(outboundInputError({ kind: "approval", operation: id, id, input: { expectedVersion: version,
    balances: [{ balanceId: id, expectedVersion: "" }] } })).not.toBe("");
  expect(fetch).not.toHaveBeenCalled();
});
it.each([
  { decision: 0, id, version }, { decision: 0, id: next, version: next }, { decision: 2, id, version: next },
  { decision: 0, id, version: next, extra: true }, { decision: 0, id, version: "bad" },
])("does not turn malformed operation success into confirmed %#", async value => {
  vi.mocked(fetch).mockResolvedValueOnce(new Response(JSON.stringify(value)));
  await expect(executeOutbound({ kind: "cancellation", operation: id, id, input: { expectedVersion: version, reason: "未执行" } },
    new AbortController().signal)).rejects.toThrow("结果不明");
});
it("preserves exact decimal, original version/body/key and named path for execution", async () => {
  vi.mocked(fetch).mockResolvedValueOnce(new Response(JSON.stringify({ decision: 0, id, version: next })));
  const command = { kind: "picking" as const, id, operation: next,
    input: { expectedVersion: version, lineId: id, pickedQuantity: "5.000", shortageQuantity: "2", reason: "实拣5缺2" } };
  expect(await executeOutbound(command, new AbortController().signal)).toEqual({ decision: 0, id, version: next });
  const [url, options] = vi.mocked(fetch).mock.calls[0]!;
  expect(url).toBe(`/api/wms/outbound/${id}/picking`);
  expect(options?.body).toBe(JSON.stringify(command.input));
  expect(new Headers(options?.headers).get("Idempotency-Key")).toBe(next);
});
