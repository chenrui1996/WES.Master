import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { forgetIdentity, setAccessToken } from "../identity/identity-api";
import { changeBusinessFile, queryBusinessFiles, readBusinessFile, type BusinessFileCommand } from "./business-files";

const entityId = "77966ccd-f1dd-412d-8e45-adb475608842";
const fileId = "a029ce52-d810-405c-b349-c0ac9ebad74b";
const version = "66966ccd-f1dd-412d-8e45-adb475608842";
const operation = "55966ccd-f1dd-412d-8e45-adb475608842";
const command: BusinessFileCommand = { kind: "warehouses", entityId, fileId, expectedVersion: version, operation, attached: true };

beforeEach(() => {
  setAccessToken("fixture", "2099-01-01T00:00:00Z");
  vi.stubGlobal("fetch", vi.fn(async () => new Response(JSON.stringify({ id: fileId, version, attached: true }))));
});
afterEach(() => { vi.unstubAllGlobals(); forgetIdentity(); });

it("sends the exact fixed entity tuple and original operation without a body-selected policy", async () => {
  const result = await changeBusinessFile(command, new AbortController().signal);
  expect(result).toEqual({ id: fileId, version, attached: true });
  const [path, options] = vi.mocked(fetch).mock.calls[0]!;
  expect(path).toBe(`/api/wms/warehouses/${entityId}/files/${fileId}`);
  expect(options?.method).toBe("PUT");
  expect(options?.body).toBe(JSON.stringify({ expectedVersion: version }));
  expect(new Headers(options?.headers).get("Idempotency-Key")).toBe(operation);
  expect(new Headers(options?.headers).get("X-WES-Client")).toBe("ops-web");
});

it("queries the exact entity with explicit realtime pagination and no automatic mount requests", async () => {
  vi.mocked(fetch).mockResolvedValueOnce(new Response(JSON.stringify({ decision: 0, rows: [], offset: 0, limit: 25 })));
  expect(fetch).not.toHaveBeenCalled();

  const result = await queryBusinessFiles("locations", entityId, new AbortController().signal);

  expect(result.rows).toEqual([]);
  expect(vi.mocked(fetch).mock.calls[0]?.[0]).toBe(`/api/wms/locations/${entityId}/files?offset=0&limit=25`);
});

it.each([
  { ...command, entityId: "" }, { ...command, fileId: "../other" },
  { ...command, expectedVersion: ` ${version}` }, { ...command, operation: "00000000-0000-0000-0000-000000000000" },
])("rejects an invalid original tuple before sending %#", async invalid => {
  await expect(changeBusinessFile(invalid, new AbortController().signal)).rejects.toThrow("原操作");
  expect(fetch).not.toHaveBeenCalled();
});

it.each([
  { offset: -1, limit: 25 }, { offset: 10001, limit: 25 }, { offset: 0, limit: 10 },
  { offset: 0, limit: 25, state: 5 }, { offset: 0.5, limit: 25 },
])("rejects invalid pagination before sending %#", async query => {
  await expect(queryBusinessFiles("warehouses", entityId, new AbortController().signal,
    query.offset, query.limit, query.state)).rejects.toThrow("分页");
  expect(fetch).not.toHaveBeenCalled();
});

it.each([
  { id: entityId, version, attached: true }, { id: fileId, version: "", attached: true },
  { id: fileId, version, attached: false }, { id: fileId, version, attached: true, extra: "unapproved" },
])("rejects invalid successful receipts as uncertain %#", async value => {
  vi.mocked(fetch).mockResolvedValueOnce(new Response(JSON.stringify(value)));
  await expect(changeBusinessFile(command, new AbortController().signal)).rejects.toThrow("结果可能已提交");
});

it("uses the original DELETE tuple independently of PUT", async () => {
  vi.mocked(fetch).mockResolvedValueOnce(new Response(JSON.stringify({ id: fileId, version, attached: false })));
  await changeBusinessFile({ ...command, attached: false, kind: "locations" }, new AbortController().signal);
  expect(vi.mocked(fetch).mock.calls[0]?.[0]).toBe(`/api/wms/locations/${entityId}/files/${fileId}`);
  expect(vi.mocked(fetch).mock.calls[0]?.[1]?.method).toBe("DELETE");
});

const row = { id: fileId, fileName: "note.txt", contentType: "text/plain", byteCount: 4, state: 1,
  stored: true, version, createdAt: "2026-10-27T00:00:00Z", processingProblem: false };
it.each([
  { decision: 0, rows: [row, row], offset: 0, limit: 25 },
  { decision: 0, rows: [{ ...row, secret: "hidden" }], offset: 0, limit: 25 },
  { decision: 0, rows: [row], offset: 25, limit: 25 },
  { decision: 0, rows: [row], offset: 0, limit: 25, total: 1 },
])("rejects duplicate or malformed directory projections %#", async value => {
  vi.mocked(fetch).mockResolvedValueOnce(new Response(JSON.stringify(value)));
  await expect(queryBusinessFiles("warehouses", entityId, new AbortController().signal)).rejects.toThrow();
});

it("rejects a selected attachment whose returned identity does not match", async () => {
  vi.mocked(fetch).mockResolvedValueOnce(new Response(JSON.stringify({ decision: 0, file: { ...row, id: entityId } })));
  await expect(readBusinessFile("warehouses", entityId, fileId, new AbortController().signal)).rejects.toThrow("身份");
});
