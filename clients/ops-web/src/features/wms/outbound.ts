import { api, isRecord, sessionState } from "../identity/identity-api";
import { validIdentity, validUtc, warehouseFields, locationFields } from "./master-data";
import { validBatch } from "./inventory";
import { mayReadStockResource } from "./stock-resources";

export const outboundFields = ["Id", "Code", "WarehouseId", "State", "Version", "CreatedAt", "Lines"] as const;
export const outboundStates = ["草稿", "已审核", "执行中（部分或待运输）", "已完成", "已取消", "部分完成并关闭余量"] as const;
export const outboundTitles = { create: "创建出库草稿", approval: "审核并全行预留", picking: "确认实拣与短缺",
  "shortage-release": "复核未执行短缺释放", shipment: "确认无运输直发", cancellation: "取消未执行出库",
  "close-remainder": "关闭部分发运余量" } as const;
export type OutboundKind = keyof typeof outboundTitles;
export interface OutboundLine {
  id: string; executionId: string; balanceId: string; itemId: string; unitId: string; batch: string; precision: number;
  containerId: string; locationId: string; branch: 0 | 1; plannedQuantity: string; reservedQuantity: string;
  pickedQuantity: string; shortageQuantity: string; releasedQuantity: string; shippedQuantity: string;
}
export interface OutboundDetail {
  id: string; code: string; warehouseId: string; state: 0 | 1 | 2 | 3 | 4 | 5; version: string; createdAt: string; lines: OutboundLine[];
}
interface DraftLine { id: string; balanceId: string; quantity: string; branch: 0 | 1 }
export type OutboundCommand = { operation: string } & (
  | { kind: "create"; input: { code: string; warehouseId: string; lines: DraftLine[] } }
  | { kind: "approval"; id: string; input: { expectedVersion: string; balances: { balanceId: string; expectedVersion: string }[] } }
  | { kind: "picking"; id: string; input: { expectedVersion: string; lineId: string; pickedQuantity: string; shortageQuantity: string; reason: string } }
  | { kind: "shortage-release"; id: string; input: { expectedVersion: string; lineId: string; quantity: string; reason: string } }
  | { kind: "shipment"; id: string; input: { expectedVersion: string; lineId: string; quantity: string } }
  | { kind: "cancellation" | "close-remainder"; id: string; input: { expectedVersion: string; reason: string } }
);
export interface OutboundReceipt { decision: 0 | 1; id: string; version: string }
const actionFields: Record<OutboundKind, readonly string[]> = {
  create: ["Code", "WarehouseId", "Lines"], approval: ["State"],
  picking: ["LineId", "PickedQuantity", "ShortageQuantity", "Reason"],
  "shortage-release": ["LineId", "Quantity", "Reason"], shipment: ["LineId", "Quantity"],
  cancellation: ["State", "Reason"], "close-remainder": ["State", "Reason"],
};
const permission: Record<OutboundKind, string> = { create: "create", approval: "approve", picking: "pick",
  "shortage-release": "release", shipment: "ship", cancellation: "cancel", "close-remainder": "close" };
function fields(name: string, required: readonly string[]): boolean {
  const granted = new Set(sessionState.subject?.grants.filter(grant => grant.permission === name).flatMap(grant => grant.fields));
  return !!sessionState.subject && required.every(field => granted.has(field));
}
export function mayReadOutbound(): boolean { return fields("wms.outbound.read", outboundFields); }
export function mayExecuteOutbound(kind: OutboundKind): boolean {
  return mayReadOutbound() && fields(`wms.outbound.${permission[kind]}`, actionFields[kind])
    && fields("wms.warehouses.read", warehouseFields) && fields("wms.locations.read", locationFields)
    && mayReadStockResource("items") && mayReadStockResource("units") && mayReadStockResource("containers");
}
function exact(value: unknown, keys: readonly string[]): value is Record<string, unknown> {
  return isRecord(value) && Object.keys(value).length === keys.length && keys.every(key => Object.hasOwn(value, key));
}
export function outboundScaled(value: string): bigint {
  const [whole, fraction = ""] = value.split(".");
  return BigInt(`${whole}${fraction.padEnd(6, "0")}`);
}
export function outboundUnshipped(line: OutboundLine): string {
  const value = (outboundScaled(line.plannedQuantity) - outboundScaled(line.shippedQuantity)).toString().padStart(7, "0");
  return `${value.slice(0, -6)}.${value.slice(-6)}`;
}
function inputAmount(value: string): boolean { return /^(?:0|[1-9]\d{0,8})(?:\.\d{1,6})?$/.test(value); }
function amount(value: unknown): value is string { return typeof value === "string" && /^(?:0|[1-9]\d{0,21})\.\d{6}$/.test(value); }
function reason(value: string): boolean {
  return value.length > 0 && value.length <= 512 && value === value.trim() && !/[\u0000-\u001f\u007f-\u009f]/.test(value);
}
export class OutboundResponseError extends Error {
  constructor() { super("出库响应无效、身份或数量不符，已丢弃；请重新读取。"); }
}
function parseLine(value: unknown): OutboundLine {
  const dimensions = ["id", "executionId", "balanceId", "itemId", "unitId", "containerId", "locationId"] as const;
  const quantities = ["plannedQuantity", "reservedQuantity", "pickedQuantity", "shortageQuantity", "releasedQuantity", "shippedQuantity"] as const;
  if (!exact(value, [...dimensions, ...quantities, "batch", "precision", "branch"]) || !dimensions.every(key => validIdentity(value[key]))
    || !quantities.every(key => amount(value[key])) || !validBatch(value.batch)
    || typeof value.precision !== "number" || !Number.isInteger(value.precision) || value.precision < 0 || value.precision > 6
    || value.branch !== 0 && value.branch !== 1) throw new OutboundResponseError();
  const { id, executionId, balanceId, itemId, unitId, containerId, locationId, plannedQuantity, reservedQuantity,
    pickedQuantity, shortageQuantity, releasedQuantity, shippedQuantity } = value;
  if (typeof id !== "string" || typeof executionId !== "string" || typeof balanceId !== "string" || typeof itemId !== "string"
    || typeof unitId !== "string" || typeof containerId !== "string" || typeof locationId !== "string"
    || typeof plannedQuantity !== "string" || typeof reservedQuantity !== "string" || typeof pickedQuantity !== "string"
    || typeof shortageQuantity !== "string" || typeof releasedQuantity !== "string" || typeof shippedQuantity !== "string") throw new OutboundResponseError();
  const planned = outboundScaled(plannedQuantity); const reserved = outboundScaled(reservedQuantity);
  const picked = outboundScaled(pickedQuantity); const shortage = outboundScaled(shortageQuantity);
  const released = outboundScaled(releasedQuantity); const shipped = outboundScaled(shippedQuantity);
  const quantum = 10n ** BigInt(6 - value.precision);
  if (planned <= 0n || shipped > picked || picked + shortage > planned || reserved + released + shipped > planned
    || [planned, reserved, picked, shortage, released, shipped].some(quantity => quantity % quantum !== 0n))
    throw new OutboundResponseError();
  return { id, executionId, balanceId, itemId, unitId, containerId, locationId, batch: value.batch, precision: value.precision,
    branch: value.branch, plannedQuantity, reservedQuantity, pickedQuantity, shortageQuantity, releasedQuantity, shippedQuantity };
}
export async function readOutbound(id: string, signal: AbortSignal): Promise<OutboundDetail> {
  if (!validIdentity(id)) throw new Error("请输入有效的非空出库单 UUID，不自动去除空白。");
  let value: unknown;
  try { value = await api(`/api/wms/outbound/${id}`, { signal }); }
  catch (failure) { if (failure instanceof SyntaxError) throw new OutboundResponseError(); throw failure; }
  if (!exact(value, ["decision", "record"]) || value.decision !== 0
    || !exact(value.record, ["id", "code", "warehouseId", "state", "version", "createdAt", "lines"])) throw new OutboundResponseError();
  const record = value.record;
  if (!validIdentity(record.id) || record.id.toLowerCase() !== id.toLowerCase() || !validIdentity(record.warehouseId)
    || !validIdentity(record.version) || !validUtc(record.createdAt) || typeof record.code !== "string" || !/^[A-Z0-9_.-]{1,64}$/.test(record.code)
    || record.state !== 0 && record.state !== 1 && record.state !== 2 && record.state !== 3 && record.state !== 4 && record.state !== 5
    || !Array.isArray(record.lines) || record.lines.length < 1 || record.lines.length > 100) throw new OutboundResponseError();
  const lines = record.lines.map(parseLine);
  if (["id", "executionId", "balanceId"].some(key => {
    const identities = lines.map(line => key === "id" ? line.id : key === "executionId" ? line.executionId : line.balanceId);
    return new Set(identities.map(value => value.toLowerCase())).size !== lines.length;
  })) throw new OutboundResponseError();
  const complete = lines.every(line => outboundScaled(line.shippedQuantity) === outboundScaled(line.plannedQuantity));
  if ((record.state === 3) !== complete
    || [1, 2].includes(record.state) && lines.some(line => outboundScaled(line.reservedQuantity)
      + outboundScaled(line.releasedQuantity) + outboundScaled(line.shippedQuantity) !== outboundScaled(line.plannedQuantity)
      || outboundScaled(line.reservedQuantity) < outboundScaled(line.pickedQuantity) - outboundScaled(line.shippedQuantity))
    || record.state === 0 && lines.some(line => [line.reservedQuantity, line.pickedQuantity, line.shortageQuantity,
      line.releasedQuantity, line.shippedQuantity].some(quantity => outboundScaled(quantity) !== 0n))
    || record.state >= 3 && lines.some(line => outboundScaled(line.reservedQuantity) !== 0n || line.pickedQuantity !== line.shippedQuantity)
    || record.state === 4 && lines.some(line => outboundScaled(line.pickedQuantity) > 0n || outboundScaled(line.shippedQuantity) > 0n)
    || record.state === 5 && !lines.some(line => outboundScaled(line.shippedQuantity) > 0n)) throw new OutboundResponseError();
  return { id: record.id, code: record.code, warehouseId: record.warehouseId, state: record.state,
    version: record.version, createdAt: record.createdAt, lines };
}
export function outboundInputError(command: OutboundCommand): string {
  if (!validIdentity(command.operation)) return "原操作 UUID 无效。";
  if (command.kind === "create") {
    const { code, warehouseId, lines } = command.input;
    if (!/^[A-Za-z0-9_.-]{1,64}$/.test(code) || !validIdentity(warehouseId)) return "请输入合法单号和仓库 UUID。";
    if (lines.length < 1 || lines.length > 100) return "计划必须包含1至100行。";
    if (new Set(lines.map(line => line.id.toLowerCase())).size !== lines.length
      || new Set(lines.map(line => line.balanceId.toLowerCase())).size !== lines.length) return "行或来源余额重复，请合并同维度计划。";
    if (lines.some(line => !validIdentity(line.id) || !validIdentity(line.balanceId) || !inputAmount(line.quantity)
      || outboundScaled(line.quantity) <= 0n || line.branch !== 0 && line.branch !== 1)) return "计划行须有合法 UUID、正数量和明确执行分支；不舍入。";
    return "";
  }
  if (!validIdentity(command.id) || !validIdentity(command.input.expectedVersion)) return "原单据或原版本 UUID 无效。";
  const input = command.input;
  if ("lineId" in input && !validIdentity(input.lineId)) return "请选择真实计划行。";
  if ("reason" in input && !reason(input.reason)) return "原因须为1至512个字符，不接受边界空白或控制字符。";
  if (command.kind === "approval") {
    const rows = command.input.balances;
    if (!rows.length || rows.length > 100 || rows.some(row => !validIdentity(row.balanceId) || !validIdentity(row.expectedVersion))
      || new Set(rows.map(row => row.balanceId.toLowerCase())).size !== rows.length) return "审核须提供全部来源余额及各自原版本 UUID，不可重复。";
  } else if (command.kind === "picking") {
    if (![command.input.pickedQuantity, command.input.shortageQuantity].every(inputAmount)
      || outboundScaled(command.input.pickedQuantity) + outboundScaled(command.input.shortageQuantity) <= 0n)
      return "实拣和短缺须为非负十进制 string，合计须大于零。";
  } else if ("quantity" in input && (!inputAmount(input.quantity) || outboundScaled(input.quantity) <= 0n)) return "数量须为正十进制 string，至多9位整数、6位小数。";
  return "";
}
export function outboundActionError(order: OutboundDetail, command: OutboundCommand): string {
  const invalid = outboundInputError(command); if (invalid) return invalid;
  if (command.kind === "create") return "";
  if (command.id.toLowerCase() !== order.id.toLowerCase() || command.input.expectedVersion.toLowerCase() !== order.version.toLowerCase())
    return "原单据或原版本不匹配，请关闭后重新读取。";
  if (order.state >= 3 || command.kind === "approval" && order.state !== 0
    || command.kind !== "approval" && command.kind !== "cancellation" && order.state === 0) return "原单状态不允许本次操作，请重新读取。";
  if (command.kind === "approval") {
    if (command.input.balances.length !== order.lines.length
      || !order.lines.every(line => command.input.balances.some(row => row.balanceId.toLowerCase() === line.balanceId.toLowerCase())))
      return "审核必须覆盖原计划全部且仅有来源余额。";
    return "";
  }
  if (!("lineId" in command.input)) return "";
  const input = command.input;
  const line = order.lines.find(line => line.id === input.lineId);
  if (!line) return "计划行不存在。";
  const quantities = "pickedQuantity" in input ? [input.pickedQuantity, input.shortageQuantity] : [input.quantity];
  if (quantities.some(quantity => outboundScaled(quantity) % (10n ** BigInt(6 - line.precision)) !== 0n)) return "数量不符合真实单位精度；不自动舍入。";
  if (command.kind === "picking" && quantities.reduce((total, quantity) => total + outboundScaled(quantity), 0n)
    > outboundScaled(line.plannedQuantity) - outboundScaled(line.pickedQuantity) - outboundScaled(line.shortageQuantity))
    return "新增实拣与短缺超过尚未记录的计划数量。";
  if (command.kind === "shipment" && (line.branch !== 0 || outboundScaled(line.shippedQuantity) !== 0n
    || outboundScaled(command.input.quantity) !== outboundScaled(line.pickedQuantity)
    || outboundScaled(command.input.quantity) > outboundScaled(line.reservedQuantity)
    || outboundScaled(line.shortageQuantity) > outboundScaled(line.releasedQuantity)))
    return "仅无运输分支允许本行一次直发全部实拣，须先复核短缺；需运输不能手工扣账。";
  if (command.kind === "shortage-release" && outboundScaled(command.input.quantity)
    > outboundScaled(line.shortageQuantity) - outboundScaled(line.releasedQuantity)) return "释放数量超过尚未复核的短缺。";
  return "";
}
export async function executeOutbound(command: OutboundCommand, signal: AbortSignal): Promise<OutboundReceipt> {
  const invalid = outboundInputError(command); if (invalid) throw new Error(invalid);
  let value: unknown;
  try {
    value = await api(command.kind === "create" ? "/api/wms/outbound" : `/api/wms/outbound/${command.id}/${command.kind}`,
      { method: "POST", signal, headers: { "Idempotency-Key": command.operation }, body: JSON.stringify(command.input) });
  } catch (failure) {
    if (failure instanceof SyntaxError) throw new Error("出库执行回执无效，结果不明；请保留原操作核对。");
    throw failure;
  }
  if (!exact(value, ["decision", "id", "version"]) || value.decision !== 0 && value.decision !== 1
    || !validIdentity(value.id) || !validIdentity(value.version)
    || command.kind !== "create" && (value.id.toLowerCase() !== command.id.toLowerCase()
      || value.version.toLowerCase() === command.input.expectedVersion.toLowerCase())) throw new Error("出库执行回执无效，结果不明；请保留原操作核对。");
  return { decision: value.decision, id: value.id, version: value.version };
}
