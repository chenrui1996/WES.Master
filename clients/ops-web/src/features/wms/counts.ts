import { api, isRecord, sessionState } from "../identity/identity-api";
import { validIdentity, validUtc, warehouseFields, locationFields } from "./master-data";
import { validBatch } from "./inventory";
import { mayReadStockResource } from "./stock-resources";

export const countFields = ["Id", "Code", "WarehouseId", "LocationId", "State", "Version", "Lines"] as const;
export const countStates = ["草稿", "盲盘中（局部锁有效）", "全部已盘，待授权调整", "调整完成", "已人工解锁，旧计数失效"] as const;
export const countPolicy = "WES-RULES-1.0/RULE-06/1800s";
export const countTitles = { create: "创建位置盘点计划", start: "开始盲盘并锁定库位", records: "记录实盘或修订",
  adjustment: "授权确认全部差异调整", invalidation: "人工解锁并使计数失效" } as const;
export type CountKind = keyof typeof countTitles;
export interface CountLine {
  id: string; itemId: string; unitId: string; batch: string; precision: number; containerId: string;
  quantity: string | null; countedBy: string | null; countedAt: string | null;
}
export interface CountDetail {
  id: string; code: string; warehouseId: string; locationId: string; state: 0 | 1 | 2 | 3 | 4; version: string;
  createdAt: string; snapshotAt: string | null; lockOwnerId: string | null; reminderDue: boolean; policy: string; lines: CountLine[];
}
export interface CountReviewLine extends CountLine { bookQuantity: string; difference: string | null }
export interface CountReview extends CountDetail { lines: CountReviewLine[] }
export type CountCommand = { operation: string } & (
  | { kind: "create"; input: { code: string; warehouseId: string; locationId: string } }
  | { kind: "start"; id: string; input: { expectedVersion: string } }
  | { kind: "records"; id: string; input: { expectedVersion: string; lineId: string; quantity: string;
    locationId: string; itemId: string; containerId: string; reason: string } }
  | { kind: "adjustment" | "invalidation"; id: string; input: { expectedVersion: string; reason: string } }
);
export interface CountReceipt { decision: 0 | 1; id: string; version: string }
const actionFields: Record<CountKind, readonly string[]> = {
  create: ["Code", "WarehouseId", "LocationId"], start: ["State"],
  records: ["LineId", "Quantity", "LocationId", "ItemId", "ContainerId", "Reason"],
  adjustment: ["State", "Reason"], invalidation: ["State", "Reason"],
};
const permission: Record<CountKind, string> = { create: "create", start: "start", records: "record", adjustment: "adjust", invalidation: "invalidate" };
function fields(name: string, required: readonly string[]): boolean {
  const granted = new Set(sessionState.subject?.grants.filter(grant => grant.permission === name).flatMap(grant => grant.fields));
  return !!sessionState.subject && required.every(field => granted.has(field));
}
export function mayReadCount(): boolean { return fields("wms.counts.read", countFields); }
export function mayReviewCount(): boolean { return mayReadCount() && fields("wms.counts.review", ["BookQuantity", "Difference"]); }
export function mayExecuteCount(kind: CountKind): boolean {
  return mayReadCount() && fields(`wms.counts.${permission[kind]}`, actionFields[kind])
    && fields("wms.warehouses.read", warehouseFields) && fields("wms.locations.read", locationFields)
    && mayReadStockResource("items") && mayReadStockResource("units") && mayReadStockResource("containers");
}
function exact(value: unknown, keys: readonly string[]): value is Record<string, unknown> {
  return isRecord(value) && Object.keys(value).length === keys.length && keys.every(key => Object.hasOwn(value, key));
}
function amount(value: unknown): value is string { return typeof value === "string" && /^(?:0|[1-9]\d{0,21})\.\d{6}$/.test(value); }
function delta(value: unknown): value is string { return typeof value === "string" && /^-?(?:0|[1-9]\d{0,21})\.\d{6}$/.test(value) && value !== "-0.000000"; }
export function countScaled(value: string): bigint { return BigInt(value.replace(".", "")); }
function inputScaled(value: string): bigint {
  const [whole, fraction = ""] = value.split(".");
  return BigInt(`${whole}${fraction.padEnd(6, "0")}`);
}
export class CountResponseError extends Error {
  constructor() { super("盘点响应无效、包含未授权账面字段或数量不符，已丢弃；请重新读取。"); }
}
const lineKeys = ["id", "itemId", "unitId", "batch", "precision", "containerId", "quantity", "countedBy", "countedAt"] as const;
function parseLine(value: unknown, review: boolean): CountLine {
  if (!exact(value, review ? [...lineKeys, "bookQuantity", "difference"] : lineKeys)
    || !validIdentity(value.id) || !validIdentity(value.itemId) || !validIdentity(value.unitId) || !validIdentity(value.containerId)
    || !validBatch(value.batch) || typeof value.precision !== "number" || !Number.isInteger(value.precision)
    || value.precision < 0 || value.precision > 6) throw new CountResponseError();
  const { id, itemId, unitId, containerId, quantity, countedBy, countedAt } = value;
  if (quantity === null ? countedBy !== null || countedAt !== null
    : !amount(quantity) || !validIdentity(countedBy) || !validUtc(countedAt)
      || countScaled(quantity) % (10n ** BigInt(6 - value.precision)) !== 0n) throw new CountResponseError();
  if (quantity !== null && typeof quantity !== "string" || countedBy !== null && typeof countedBy !== "string"
    || countedAt !== null && typeof countedAt !== "string") throw new CountResponseError();
  return { id, itemId, unitId, containerId, batch: value.batch, precision: value.precision, quantity, countedBy, countedAt };
}
function parseReviewLine(value: unknown): CountReviewLine {
  const line = parseLine(value, true);
  if (!isRecord(value) || !amount(value.bookQuantity) || countScaled(value.bookQuantity) % (10n ** BigInt(6 - line.precision)) !== 0n
    || (line.quantity === null ? value.difference !== null
      : !delta(value.difference) || countScaled(line.quantity) - countScaled(value.bookQuantity) !== countScaled(value.difference)))
    throw new CountResponseError();
  const difference = value.difference;
  if (difference !== null && typeof difference !== "string") throw new CountResponseError();
  return { ...line, bookQuantity: value.bookQuantity, difference };
}
async function load(id: string, signal: AbortSignal, review: boolean): Promise<CountDetail | CountReview> {
  if (!validIdentity(id)) throw new Error("请输入有效的非空盘点单 UUID，不自动去除空白。");
  let value: unknown;
  try { value = await api(`/api/wms/counts/${id}${review ? "/review" : ""}`, { signal }); }
  catch (failure) { if (failure instanceof SyntaxError) throw new CountResponseError(); throw failure; }
  if (!exact(value, ["decision", "record"]) || value.decision !== 0
    || !exact(value.record, ["id", "code", "warehouseId", "locationId", "state", "version", "createdAt",
      "snapshotAt", "lockOwnerId", "reminderDue", "policy", "lines"])) throw new CountResponseError();
  const record = value.record;
  if (!validIdentity(record.id) || record.id.toLowerCase() !== id.toLowerCase() || !validIdentity(record.warehouseId)
    || !validIdentity(record.locationId) || !validIdentity(record.version) || !validUtc(record.createdAt)
    || typeof record.code !== "string" || !/^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$/.test(record.code)
    || typeof record.state !== "number" || !Number.isInteger(record.state) || record.state < 0 || record.state > 4
    || typeof record.reminderDue !== "boolean" || record.policy !== countPolicy || !Array.isArray(record.lines)
    || record.lines.length > 100 || (record.state === 0 ? record.lines.length !== 0 || record.snapshotAt !== null || record.lockOwnerId !== null
      : !validUtc(record.snapshotAt) || record.lines.length === 0
        || (record.state === 1 || record.state === 2 ? !validIdentity(record.lockOwnerId) : record.lockOwnerId !== null))
    || record.reminderDue && record.state !== 1 && record.state !== 2) throw new CountResponseError();
  const lines = review ? record.lines.map(parseReviewLine) : record.lines.map(line => parseLine(line, false));
  const { snapshotAt, lockOwnerId } = record;
  if (snapshotAt !== null && typeof snapshotAt !== "string" || lockOwnerId !== null && typeof lockOwnerId !== "string")
    throw new CountResponseError();
  if (new Set(lines.map(line => line.id.toLowerCase())).size !== lines.length
    || lines.some(line => line.countedAt !== null && snapshotAt !== null && Date.parse(line.countedAt) < Date.parse(snapshotAt))
    || (record.state === 2 || record.state === 3) && lines.some(line => line.quantity === null)
    || record.state === 1 && lines.every(line => line.quantity !== null)) throw new CountResponseError();
  return { id: record.id, code: record.code, warehouseId: record.warehouseId, locationId: record.locationId,
    state: record.state as CountDetail["state"], version: record.version, createdAt: record.createdAt,
    snapshotAt, lockOwnerId, reminderDue: record.reminderDue, policy: record.policy, lines };
}
export async function readCount(id: string, signal: AbortSignal): Promise<CountDetail> { return load(id, signal, false); }
export async function readCountReview(id: string, signal: AbortSignal): Promise<CountReview> {
  const record = await load(id, signal, true);
  const lines = record.lines.map(line => {
    if (!("bookQuantity" in line) || !("difference" in line) || !amount(line.bookQuantity)
      || line.difference !== null && !delta(line.difference)) throw new CountResponseError();
    return { ...line, bookQuantity: line.bookQuantity, difference: line.difference };
  });
  return { ...record, lines };
}
function reason(value: string): boolean {
  return value.length > 0 && value.length <= 512 && value === value.trim() && !/[\u0000-\u001f\u007f-\u009f]/.test(value);
}
export function countInputError(command: CountCommand): string {
  if (!validIdentity(command.operation)) return "原操作 UUID 无效。";
  if (command.kind === "create") {
    if (!/^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$/.test(command.input.code)) return "单号须为1至64个字母、数字、点、下划线或连字符，以字母数字开头。";
    return [command.input.warehouseId, command.input.locationId].every(validIdentity) ? "" : "仓库和盘点库位须为有效 UUID。";
  }
  if (!validIdentity(command.id) || !validIdentity(command.input.expectedVersion)) return "原单据或原版本 UUID 无效。";
  const input = command.input;
  if ("reason" in input && !reason(input.reason)) return "原因须为1至512个字符，不接受边界空白或控制字符。";
  if ("quantity" in input) {
    if (![input.lineId, input.locationId, input.itemId, input.containerId].every(validIdentity)) return "应盘行及扫描库位、物料、容器须为有效 UUID。";
    if (!/^(?:0|[1-9]\d{0,21})(?:\.\d{1,6})?$/.test(input.quantity)) return "实盘须为非负十进制 string，至多22位整数、6位小数，不自动舍入。";
  }
  return "";
}
export function countActionError(record: CountDetail, command: CountCommand): string {
  const invalid = countInputError(command); if (invalid || command.kind === "create") return invalid;
  if (command.id.toLowerCase() !== record.id.toLowerCase() || command.input.expectedVersion.toLowerCase() !== record.version.toLowerCase())
    return "原单据或原版本不匹配，请关闭后重新读取。";
  if (command.kind === "start") return record.state === 0 || record.state === 4 ? "" : "仅草稿或已失效计划可重新开始；旧计数不会复用。";
  if (record.state !== 1 && record.state !== 2) return "当前没有有效盘点锁，旧计数不能录入或调整。";
  if (command.kind === "adjustment") return record.state === 2 ? "" : "必须全部应盘行已录数，才能授权调整。";
  if (command.kind === "records") {
    const input = command.input; const line = record.lines.find(line => line.id === input.lineId);
    if (!line || input.locationId.toLowerCase() !== record.locationId.toLowerCase() || input.itemId.toLowerCase() !== line.itemId.toLowerCase()
      || input.containerId.toLowerCase() !== line.containerId.toLowerCase()) return "扫码库位、物料或容器与应盘行不符。";
    if (inputScaled(input.quantity) % (10n ** BigInt(6 - line.precision)) !== 0n) return "实盘数量不符合真实单位精度；不自动舍入。";
  }
  return "";
}
export async function executeCount(command: CountCommand, signal: AbortSignal): Promise<CountReceipt> {
  const invalid = countInputError(command); if (invalid) throw new Error(invalid);
  let value: unknown;
  try {
    value = await api(command.kind === "create" ? "/api/wms/counts" : `/api/wms/counts/${command.id}/${command.kind}`,
      { method: "POST", signal, headers: { "Idempotency-Key": command.operation }, body: JSON.stringify(command.input) });
  } catch (failure) {
    if (failure instanceof SyntaxError) throw new Error("盘点执行回执无效，结果不明；请保留原操作核对。");
    throw failure;
  }
  if (!exact(value, ["decision", "id", "version"]) || value.decision !== 0 && value.decision !== 1
    || !validIdentity(value.id) || !validIdentity(value.version) || command.kind !== "create"
      && (value.id.toLowerCase() !== command.id.toLowerCase() || value.version.toLowerCase() === command.input.expectedVersion.toLowerCase()))
    throw new Error("盘点执行回执无效，结果不明；请保留原操作核对。");
  return { decision: value.decision, id: value.id, version: value.version };
}
