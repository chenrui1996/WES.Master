import { api, isRecord, sessionState } from "../identity/identity-api";
import { validBatch } from "./inventory";
import { validIdentity, validUtc } from "./master-data";

export const inboundFields = ["Id", "Code", "WarehouseId", "State", "Version", "CreatedAt", "Lines", "Receipts"] as const;
export interface InboundLine {
  id: string; itemId: string; unitId: string; batch: string; precision: number;
  planned: string; received: string; remaining: string;
}
export interface InboundReceipt {
  id: string; lineId: string; containerId: string; receiptLocationId: string; targetLocationId: string;
  quantity: string; requiresTransport: boolean; arrived: boolean; transportId: string | null;
}
export interface InboundDetail {
  id: string; code: string; warehouseId: string; state: 0 | 1 | 2 | 3; version: string; createdAt: string;
  lines: InboundLine[]; receipts: InboundReceipt[];
}
export class InboundResponseError extends Error {
  constructor() { super("入库响应无效、身份或数量不符，已丢弃；请重新读取。"); }
}
export function mayReadInbound(): boolean {
  const fields = new Set(sessionState.subject?.grants.filter(grant => grant.permission === "wms.inbound.read").flatMap(grant => grant.fields));
  return !!sessionState.subject && inboundFields.every(field => fields.has(field));
}
function exact(value: unknown, keys: readonly string[]): value is Record<string, unknown> {
  return isRecord(value) && Object.keys(value).length === keys.length && keys.every(key => Object.hasOwn(value, key));
}
function amount(value: unknown): value is string {
  return typeof value === "string" && /^(?:0|[1-9]\d{0,21})\.\d{6}$/.test(value);
}
function scaled(value: string): bigint { return BigInt(value.replace(".", "")); }
function parseLine(value: unknown): InboundLine {
  if (!exact(value, ["id", "itemId", "unitId", "batch", "precision", "planned", "received", "remaining"])
    || !validIdentity(value.id) || !validIdentity(value.itemId) || !validIdentity(value.unitId) || !validBatch(value.batch)
    || typeof value.precision !== "number" || !Number.isInteger(value.precision) || value.precision < 0 || value.precision > 6
    || !amount(value.planned) || !amount(value.received) || !amount(value.remaining)) throw new InboundResponseError();
  const quantities = [value.planned, value.received, value.remaining].map(scaled);
  const quantum = 10n ** BigInt(6 - value.precision);
  if (scaled(value.planned) === 0n || scaled(value.planned) !== scaled(value.received) + scaled(value.remaining)
    || quantities.some(quantity => quantity % quantum !== 0n)) throw new InboundResponseError();
  return { id: value.id, itemId: value.itemId, unitId: value.unitId, batch: value.batch, precision: value.precision,
    planned: value.planned, received: value.received, remaining: value.remaining };
}
function parseReceipt(value: unknown): InboundReceipt {
  if (!exact(value, ["id", "lineId", "containerId", "receiptLocationId", "targetLocationId", "quantity", "requiresTransport", "arrived", "transportId"])
    || !validIdentity(value.id) || !validIdentity(value.lineId) || !validIdentity(value.containerId)
    || !validIdentity(value.receiptLocationId) || !validIdentity(value.targetLocationId)
    || !amount(value.quantity) || scaled(value.quantity) <= 0n || scaled(value.quantity) > 999999999999999n
    || typeof value.requiresTransport !== "boolean" || typeof value.arrived !== "boolean"
    || value.transportId !== null && !validIdentity(value.transportId)
    || value.requiresTransport !== (value.receiptLocationId.toLowerCase() !== value.targetLocationId.toLowerCase())
    || !value.requiresTransport && (!value.arrived || value.transportId !== null)
    || value.requiresTransport && value.arrived && value.transportId === null) throw new InboundResponseError();
  return { id: value.id, lineId: value.lineId, containerId: value.containerId, receiptLocationId: value.receiptLocationId,
    targetLocationId: value.targetLocationId, quantity: value.quantity, requiresTransport: value.requiresTransport,
    arrived: value.arrived, transportId: value.transportId };
}
export async function readInbound(id: string, signal: AbortSignal): Promise<InboundDetail> {
  if (!validIdentity(id)) throw new Error("请输入有效的非空入库单 UUID，不自动去除空白。");
  let value: unknown;
  try { value = await api(`/api/wms/inbound/${id}`, { signal }); }
  catch (error) { if (error instanceof SyntaxError) throw new InboundResponseError(); throw error; }
  if (!exact(value, ["id", "code", "warehouseId", "state", "version", "createdAt", "lines", "receipts"])
    || !validIdentity(value.id) || value.id.toLowerCase() !== id.toLowerCase() || !validIdentity(value.warehouseId)
    || typeof value.code !== "string" || !/^[A-Z0-9_.-]{1,64}$/.test(value.code)
    || value.state !== 0 && value.state !== 1 && value.state !== 2 && value.state !== 3
    || !validIdentity(value.version) || !validUtc(value.createdAt) || !Array.isArray(value.lines) || !Array.isArray(value.receipts)
    || value.lines.length < 1 || value.lines.length > 100) throw new InboundResponseError();
  const lines = value.lines.map(parseLine); const receipts = value.receipts.map(parseReceipt);
  const ids = new Set(lines.map(line => line.id.toLowerCase()));
  if (ids.size !== lines.length || new Set(receipts.map(unit => unit.id.toLowerCase())).size !== receipts.length) throw new InboundResponseError();
  for (const receipt of receipts) {
    const line = lines.find(line => line.id.toLowerCase() === receipt.lineId.toLowerCase());
    if (!line || scaled(receipt.quantity) % (10n ** BigInt(6 - line.precision)) !== 0n) throw new InboundResponseError();
  }
  for (const line of lines)
    if (scaled(line.received) !== receipts.filter(unit => unit.lineId.toLowerCase() === line.id.toLowerCase())
      .reduce((sum, unit) => sum + scaled(unit.quantity), 0n)) throw new InboundResponseError();
  const completed = lines.every(line => scaled(line.remaining) === 0n) && receipts.every(unit => unit.arrived);
  if (value.state <= 1 && receipts.length > 0 || value.state >= 2 && receipts.length === 0
    || (value.state === 3) !== completed) throw new InboundResponseError();
  return { id: value.id, code: value.code, warehouseId: value.warehouseId, state: value.state, version: value.version,
    createdAt: value.createdAt, lines, receipts };
}
