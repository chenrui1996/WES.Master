import { api, isRecord, sessionState } from "../identity/identity-api";
import { validBatch, type InventoryFilters } from "./inventory";
import { validIdentity, validUtc } from "./master-data";

export const stockLedgerFields = ["Id", "PostingId", "BalanceId", "WarehouseId", "LocationId", "ItemId",
  "Batch", "ContainerId", "UnitId", "Precision", "Kind", "Quantity", "Before", "After", "SourceDocumentId",
  "SourceLineId", "ExecutionUnitId", "CorrelationId", "OccurredAt"] as const;
export interface StockLedgerRow {
  id: string; postingId: string; balanceId: string; warehouseId: string; locationId: string; itemId: string; batch: string;
  containerId: string; unitId: string; precision: number; kind: 0 | 1 | 2; quantity: string; before: string; after: string;
  sourceDocumentId: string; sourceLineId: string; executionUnitId: string; correlationId: string; occurredAt: string;
}
export interface StockLedgerFilters extends InventoryFilters {
  sourceDocumentId?: string; correlationId?: string; kind?: number; from?: string; before?: string;
}
export class StockLedgerResponseError extends Error {
  constructor() { super("流水响应无效、精度或筛选不符，已丢弃；请重新读取。"); }
}
const filterIdentities = ["warehouseId", "locationId", "itemId", "containerId", "sourceDocumentId", "correlationId"] as const;
export function mayReadStockLedger(): boolean {
  const fields = new Set(sessionState.subject?.grants.filter(grant => grant.permission === "wms.ledger.read" && grant.scope === 2).flatMap(grant => grant.fields));
  return !!sessionState.subject && stockLedgerFields.every(field => fields.has(field));
}
function timeKey(value: unknown): string | null {
  if (!validUtc(value) || value.slice(0, 4) < "1000") return null;
  const fraction = value.slice(19).match(/^\.(\d+)/)?.[1] ?? "";
  if (fraction.length === 7 && fraction[6] !== "0") return null;
  // Date only retains milliseconds; compare the validated UTC microseconds without rounding.
  return `${value.slice(0, 19)}.${fraction.padEnd(6, "0").slice(0, 6)}`;
}
function book(value: unknown): value is string {
  return typeof value === "string" && /^(?:0|[1-9]\d{0,21})\.\d{6}$/.test(value);
}
function delta(value: unknown): value is string {
  return typeof value === "string" && /^-?(?:0|[1-9]\d{0,8})\.\d{6}$/.test(value);
}
function scaled(value: string): bigint { return BigInt(value.replace(".", "")); }
function parseRow(value: unknown): StockLedgerRow {
  const keys = stockLedgerFields.map(field => field[0]!.toLowerCase() + field.slice(1));
  if (!isRecord(value) || Object.keys(value).length !== keys.length || !keys.every(key => key in value)
    || !validIdentity(value.id) || !validIdentity(value.postingId) || !validIdentity(value.balanceId)
    || !validIdentity(value.warehouseId) || !validIdentity(value.locationId) || !validIdentity(value.itemId)
    || !validIdentity(value.containerId) || !validIdentity(value.unitId) || !validIdentity(value.sourceDocumentId)
    || !validIdentity(value.sourceLineId) || !validIdentity(value.executionUnitId) || !validIdentity(value.correlationId) || !validBatch(value.batch)
    || typeof value.precision !== "number" || !Number.isInteger(value.precision) || value.precision < 0 || value.precision > 6
    || value.kind !== 0 && value.kind !== 1 && value.kind !== 2
    || !delta(value.quantity) || !book(value.before) || !book(value.after) || !validUtc(value.occurredAt) || timeKey(value.occurredAt) === null)
    throw new StockLedgerResponseError();
  const change = scaled(value.quantity); const before = scaled(value.before); const after = scaled(value.after);
  const quantum = 10n ** BigInt(6 - value.precision);
  if (change === 0n || after - before !== change || [change, before, after].some(amount => amount % quantum !== 0n)
    || value.kind === 0 && change < 0n || value.kind === 1 && change > 0n) throw new StockLedgerResponseError();
  return { id: value.id, postingId: value.postingId, balanceId: value.balanceId,
    warehouseId: value.warehouseId, locationId: value.locationId, itemId: value.itemId,
    batch: value.batch, containerId: value.containerId, unitId: value.unitId, precision: value.precision,
    kind: value.kind, quantity: value.quantity, before: value.before, after: value.after,
    sourceDocumentId: value.sourceDocumentId, sourceLineId: value.sourceLineId, executionUnitId: value.executionUnitId,
    correlationId: value.correlationId, occurredAt: value.occurredAt };
}
export async function queryStockLedger(offset: number, limit: number, filters: StockLedgerFilters, signal: AbortSignal): Promise<StockLedgerRow[]> {
  if (!Number.isInteger(offset) || offset < 0 || offset > 10000 || ![25, 50, 100].includes(limit))
    throw new Error("流水页范围无效，请从第一页重新读取。");
  const query = new URLSearchParams({ offset: String(offset), limit: String(limit) });
  for (const key of filterIdentities) {
    const value = filters[key];
    if (value !== undefined) {
      if (!validIdentity(value)) throw new Error("维度及来源筛选须为有效的非空 UUID。");
      query.set(key, value);
    }
  }
  if (filters.batch !== undefined) {
    if (!validBatch(filters.batch)) throw new Error("批次最长 128 字符，不能含边界空白或控制字符，不自动去空白。");
    query.set("batch", filters.batch);
  }
  if (filters.kind !== undefined) {
    if (![0, 1, 2].includes(filters.kind)) throw new Error("记账类型无效。");
    query.set("kind", String(filters.kind));
  }
  for (const key of ["from", "before"] as const) {
    const value = filters[key];
    if (value !== undefined) {
      if (timeKey(value) === null) throw new Error("时间须为公元 1000 年起的 UTC ISO 时间，最多微秒精度。");
      query.set(key, value);
    }
  }
  if (filters.from !== undefined && filters.before !== undefined && timeKey(filters.from)! >= timeKey(filters.before)!)
    throw new Error("流水时间范围须起点早于终点，终点不包含。");
  let value: unknown;
  try { value = await api(`/api/wms/ledger?${query}`, { signal }); }
  catch (error) { if (error instanceof SyntaxError) throw new StockLedgerResponseError(); throw error; }
  if (!isRecord(value) || Object.keys(value).length !== 3 || !Array.isArray(value.rows)
    || value.offset !== offset || value.limit !== limit || value.rows.length > limit) throw new StockLedgerResponseError();
  const rows = value.rows.map(parseRow); const seen = new Set<string>();
  for (const [index, row] of rows.entries()) {
    const time = timeKey(row.occurredAt)!; const previous = rows[index - 1];
    if (seen.has(row.id.toLowerCase()) || previous && (time < timeKey(previous.occurredAt)!
      || time === timeKey(previous.occurredAt) && previous.id.toLowerCase() >= row.id.toLowerCase())) throw new StockLedgerResponseError();
    seen.add(row.id.toLowerCase());
    for (const key of filterIdentities)
      if (filters[key] !== undefined && row[key].toLowerCase() !== filters[key]!.toLowerCase()) throw new StockLedgerResponseError();
    if (filters.batch !== undefined && row.batch !== filters.batch || filters.kind !== undefined && row.kind !== filters.kind
      || filters.from !== undefined && time < timeKey(filters.from)! || filters.before !== undefined && time >= timeKey(filters.before)!)
      throw new StockLedgerResponseError();
  }
  return rows;
}
