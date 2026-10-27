import { api, isRecord, sessionState } from "../identity/identity-api";
import { validIdentity } from "./master-data";

export const inventoryFields = ["Id", "WarehouseId", "LocationId", "ItemId", "Batch", "ContainerId", "UnitId",
  "Precision", "Quantity", "Reserved", "InTransit", "Unknown", "Protected", "Available", "Version"] as const;
export interface InventoryRow {
  id: string; warehouseId: string; locationId: string; itemId: string; batch: string; containerId: string;
  unitId: string; precision: number; quantity: string; reserved: string; inTransit: string; unknown: string;
  protected: string; available: string; version: string;
}
export interface InventoryFilters { warehouseId?: string; locationId?: string; itemId?: string; containerId?: string; batch?: string }
export class InventoryResponseError extends Error {
  constructor() { super("库存响应无效、精度或筛选不符，已丢弃；请重新读取。"); }
}
export function mayReadInventory(): boolean {
  const fields = new Set(sessionState.subject?.grants.filter(grant => grant.permission === "wms.inventory.read" && grant.scope === 2).flatMap(grant => grant.fields));
  return !!sessionState.subject && inventoryFields.every(field => fields.has(field));
}
export function validBatch(value: unknown): value is string {
  return typeof value === "string" && value.length <= 128 && value.trim() === value && !/[\u0000-\u001f\u007f-\u009f]/.test(value);
}
function quantity(value: unknown): value is string {
  return typeof value === "string" && /^(?:0|[1-9]\d{0,21})\.\d{6}$/.test(value);
}
function scaled(value: string): bigint { return BigInt(value.replace(".", "")); }
function parseRow(value: unknown): InventoryRow {
  const keys = inventoryFields.map(field => field[0]!.toLowerCase() + field.slice(1));
  if (!isRecord(value) || Object.keys(value).length !== keys.length || !keys.every(key => key in value)
    || !validIdentity(value.id) || !validIdentity(value.warehouseId) || !validIdentity(value.locationId) || !validIdentity(value.itemId)
    || !validIdentity(value.containerId) || !validIdentity(value.unitId) || !validIdentity(value.version) || !validBatch(value.batch)
    || typeof value.precision !== "number" || !Number.isInteger(value.precision) || value.precision < 0 || value.precision > 6
    || !quantity(value.quantity) || !quantity(value.reserved) || !quantity(value.inTransit) || !quantity(value.unknown)
    || !quantity(value.protected) || !quantity(value.available)) throw new InventoryResponseError();
  const book = scaled(value.quantity); const reserved = scaled(value.reserved); const transit = scaled(value.inTransit);
  const unknown = scaled(value.unknown); const protectedQty = scaled(value.protected); const available = scaled(value.available);
  const quantum = 10n ** BigInt(6 - value.precision);
  if (available + protectedQty !== book
    || reserved > protectedQty || transit > protectedQty || unknown > protectedQty
    || protectedQty > reserved + transit + unknown
    || [book, reserved, transit, unknown, protectedQty, available].some(amount => amount % quantum !== 0n))
    throw new InventoryResponseError();
  return { id: value.id, warehouseId: value.warehouseId, locationId: value.locationId, itemId: value.itemId, batch: value.batch,
    containerId: value.containerId, unitId: value.unitId, precision: value.precision, quantity: value.quantity,
    reserved: value.reserved, inTransit: value.inTransit, unknown: value.unknown, protected: value.protected, available: value.available, version: value.version };
}
export async function queryInventory(offset: number, limit: number, filters: InventoryFilters, signal: AbortSignal): Promise<InventoryRow[]> {
  if (!Number.isInteger(offset) || offset < 0 || offset > 10000 || ![25, 50, 100].includes(limit))
    throw new Error("库存页范围无效，请从第一页重新读取。");
  const query = new URLSearchParams({ offset: String(offset), limit: String(limit) });
  for (const key of ["warehouseId", "locationId", "itemId", "containerId"] as const) {
    const value = filters[key];
    if (value !== undefined) {
      if (!validIdentity(value)) throw new Error("维度筛选须为有效的非空 UUID。");
      query.set(key, value);
    }
  }
  if (filters.batch !== undefined) {
    if (!validBatch(filters.batch)) throw new Error("批次最长 128 字符，不能含边界空白或控制字符，不自动去空白。");
    query.set("batch", filters.batch);
  }
  let value: unknown;
  try { value = await api(`/api/wms/inventory?${query}`, { signal }); }
  catch (error) { if (error instanceof SyntaxError) throw new InventoryResponseError(); throw error; }
  if (!isRecord(value) || Object.keys(value).length !== 3 || !Array.isArray(value.rows)
    || value.offset !== offset || value.limit !== limit || value.rows.length > limit) throw new InventoryResponseError();
  const rows = value.rows.map(parseRow);
  for (const [index, row] of rows.entries()) {
    if (index > 0 && rows[index - 1]!.id.toLowerCase() >= row.id.toLowerCase()) throw new InventoryResponseError();
    for (const key of ["warehouseId", "locationId", "itemId", "containerId"] as const)
      if (filters[key] !== undefined && row[key].toLowerCase() !== filters[key]!.toLowerCase()) throw new InventoryResponseError();
    if (filters.batch !== undefined && row.batch !== filters.batch) throw new InventoryResponseError();
  }
  return rows;
}
