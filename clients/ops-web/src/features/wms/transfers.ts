import { api, isRecord, sessionState } from "../identity/identity-api";
import { validIdentity, validUtc, warehouseFields, locationFields } from "./master-data";
import { validBatch } from "./inventory";
import { mayReadStockResource } from "./stock-resources";

export const transferFields = ["Id", "Code", "WarehouseId", "State", "Version", "CreatedAt", "Lines"] as const;
export const transferStates = ["草稿", "已全行审核", "执行中", "全部已移且已同步", "安全取消", "部分完成并关闭余量"] as const;
export const transferExecutions = ["尚未执行", "在途", "已到达", "结果未知（继续保护）", "未执行余量已关闭"] as const;
export const transferPostings = ["无到达记账", "已到待同步", "已同步入账", "到达补账失败（继续保护）"] as const;
export const transferTitles = { create: "创建多行移库草稿", approval: "全行原子审核", dispatch: "确认人工开始搬运",
  arrivals: "扫描确认人工到达", synchronizations: "仅补同步账目", unknown: "标记执行结果未知", closure: "安全关闭未执行余量" } as const;
export type TransferKind = keyof typeof transferTitles;
export interface TransferLine {
  id: string; executionId: string; sourceBalanceId: string; targetBalanceId: string; itemId: string; unitId: string;
  batch: string; precision: number; containerId: string; sourceLocationId: string; targetLocationId: string;
  branch: 0 | 1; plannedQuantity: string; reservedQuantity: string; movedQuantity: string; closedQuantity: string;
  executionStatus: 0 | 1 | 2 | 3 | 4; postingStatus: 0 | 1 | 2 | 3; sourceVersion: string; targetVersion: string;
}
export interface TransferDetail {
  id: string; code: string; warehouseId: string; state: 0 | 1 | 2 | 3 | 4 | 5; version: string; createdAt: string; lines: TransferLine[];
}
export interface TransferDraftLine { id: string; sourceBalanceId: string; targetBalanceId: string; quantity: string; branch: 0 | 1 }
export type TransferCommand = { operation: string } & (
  | { kind: "create"; input: { code: string; warehouseId: string; lines: TransferDraftLine[] } }
  | { kind: "approval"; id: string; input: { expectedVersion: string; balances: { balanceId: string; expectedVersion: string }[] } }
  | { kind: "dispatch" | "synchronizations"; id: string; input: { expectedVersion: string; lineId: string } }
  | { kind: "arrivals"; id: string; input: { expectedVersion: string; lineId: string; sourceLocationId: string;
    targetLocationId: string; containerId: string; quantity: string; reason: string } }
  | { kind: "unknown"; id: string; input: { expectedVersion: string; lineId: string; reason: string } }
  | { kind: "closure"; id: string; input: { expectedVersion: string; reason: string } }
);
export interface TransferReceipt { decision: 0 | 1; id: string; version: string }
const actionFields: Record<TransferKind, readonly string[]> = {
  create: ["Code", "WarehouseId", "Lines"], approval: ["State"], dispatch: ["State"], synchronizations: ["State"],
  arrivals: ["LineId", "SourceLocationId", "TargetLocationId", "ContainerId", "Quantity", "Reason"],
  unknown: ["State", "Reason"], closure: ["State", "Reason"],
};
const permissions: Record<TransferKind, string> = { create: "create", approval: "approve", dispatch: "dispatch",
  arrivals: "arrive", synchronizations: "sync", unknown: "unknown", closure: "close" };
function fields(name: string, required: readonly string[]): boolean {
  const granted = new Set(sessionState.subject?.grants.filter(grant => grant.permission === name).flatMap(grant => grant.fields));
  return !!sessionState.subject && required.every(field => granted.has(field));
}
export function mayReadTransfer(): boolean { return fields("wms.transfers.read", transferFields); }
export function mayExecuteTransfer(kind: TransferKind): boolean {
  return mayReadTransfer() && fields(`wms.transfers.${permissions[kind]}`, actionFields[kind])
    && fields("wms.warehouses.read", warehouseFields) && fields("wms.locations.read", locationFields)
    && mayReadStockResource("items") && mayReadStockResource("units") && mayReadStockResource("containers");
}
function exact(value: unknown, keys: readonly string[]): value is Record<string, unknown> {
  return isRecord(value) && Object.keys(value).length === keys.length && keys.every(key => Object.hasOwn(value, key));
}
function amount(value: unknown): value is string { return typeof value === "string" && /^(?:0|[1-9]\d{0,8})\.\d{6}$/.test(value); }
function inputAmount(value: string): boolean {
  return /^(?:0|[1-9]\d{0,8})(?:\.\d{1,6})?$/.test(value) && scaled(value) > 0n;
}
function scaled(value: string): bigint {
  const [whole, fraction = ""] = value.split(".");
  return BigInt(`${whole}${fraction.padEnd(6, "0")}`);
}
function unique(values: string[]): boolean { return new Set(values.map(value => value.toLowerCase())).size === values.length; }
function same(left: string, right: string): boolean { return left.toLowerCase() === right.toLowerCase(); }
export class TransferResponseError extends Error {
  constructor() { super("移库响应无效、数量或状态不一致，已丢弃；请重新读取。"); }
}
const lineKeys = ["id", "executionId", "sourceBalanceId", "targetBalanceId", "itemId", "unitId", "batch", "precision",
  "containerId", "sourceLocationId", "targetLocationId", "branch", "plannedQuantity", "reservedQuantity",
  "movedQuantity", "closedQuantity", "executionStatus", "postingStatus", "sourceVersion", "targetVersion"] as const;
function parseLine(value: unknown): TransferLine {
  if (!exact(value, lineKeys) || !validIdentity(value.id) || !validIdentity(value.executionId)
    || !validIdentity(value.sourceBalanceId) || !validIdentity(value.targetBalanceId) || same(value.sourceBalanceId, value.targetBalanceId)
    || !validIdentity(value.itemId) || !validIdentity(value.unitId) || !validIdentity(value.containerId)
    || !validIdentity(value.sourceLocationId) || !validIdentity(value.targetLocationId) || same(value.sourceLocationId, value.targetLocationId)
    || !validIdentity(value.sourceVersion) || !validIdentity(value.targetVersion) || !validBatch(value.batch)
    || typeof value.precision !== "number" || !Number.isInteger(value.precision) || value.precision < 0 || value.precision > 6
    || value.branch !== 0 && value.branch !== 1
    || !amount(value.plannedQuantity) || scaled(value.plannedQuantity) === 0n
    || !amount(value.reservedQuantity) || !amount(value.movedQuantity) || !amount(value.closedQuantity)
    || value.executionStatus !== 0 && value.executionStatus !== 1 && value.executionStatus !== 2 && value.executionStatus !== 3 && value.executionStatus !== 4
    || value.postingStatus !== 0 && value.postingStatus !== 1 && value.postingStatus !== 2 && value.postingStatus !== 3)
    throw new TransferResponseError();
  const { plannedQuantity, reservedQuantity, movedQuantity, closedQuantity, postingStatus, executionStatus, precision } = value;
  const planned = scaled(plannedQuantity); const reserved = scaled(reservedQuantity);
  const moved = scaled(movedQuantity); const closed = scaled(closedQuantity);
  if ([planned, reserved, moved, closed].some(quantity => quantity % (10n ** BigInt(6 - precision)) !== 0n)
    || reserved + moved + closed > planned || moved !== 0n && moved !== planned || closed !== 0n && closed !== planned
    || (postingStatus === 2 ? executionStatus !== 2 || moved !== planned || reserved !== 0n : moved !== 0n)
    || (postingStatus === 1 || postingStatus === 3) && (executionStatus !== 2 || reserved !== planned)
    || executionStatus === 2 && postingStatus === 0
    || executionStatus === 4 && (closed !== planned || reserved !== 0n || postingStatus !== 0)
    || executionStatus !== 4 && closed !== 0n) throw new TransferResponseError();
  return { id: value.id, executionId: value.executionId, sourceBalanceId: value.sourceBalanceId, targetBalanceId: value.targetBalanceId,
    itemId: value.itemId, unitId: value.unitId, batch: value.batch, precision: value.precision, containerId: value.containerId,
    sourceLocationId: value.sourceLocationId, targetLocationId: value.targetLocationId, branch: value.branch,
    plannedQuantity, reservedQuantity, movedQuantity, closedQuantity, executionStatus, postingStatus,
    sourceVersion: value.sourceVersion, targetVersion: value.targetVersion };
}
export async function readTransfer(id: string, signal: AbortSignal): Promise<TransferDetail> {
  if (!validIdentity(id)) throw new Error("请输入有效的非空移库单 UUID，不自动去除空白。");
  let value: unknown;
  try { value = await api(`/api/wms/transfers/${id}`, { signal }); }
  catch (failure) { if (failure instanceof SyntaxError) throw new TransferResponseError(); throw failure; }
  if (!exact(value, ["decision", "record"]) || value.decision !== 0
    || !exact(value.record, ["id", "code", "warehouseId", "state", "version", "createdAt", "lines"])) throw new TransferResponseError();
  const record = value.record;
  if (!validIdentity(record.id) || !same(record.id, id) || !validIdentity(record.warehouseId) || !validIdentity(record.version)
    || !validUtc(record.createdAt) || typeof record.code !== "string" || !/^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$/.test(record.code)
    || record.state !== 0 && record.state !== 1 && record.state !== 2 && record.state !== 3 && record.state !== 4 && record.state !== 5
    || !Array.isArray(record.lines) || record.lines.length === 0 || record.lines.length > 100) throw new TransferResponseError();
  const lines = record.lines.map(parseLine);
  if (["id", "executionId", "sourceBalanceId", "targetBalanceId", "containerId"].some(key =>
    !unique(lines.map(line => {
      switch (key) { case "id": return line.id; case "executionId": return line.executionId;
        case "sourceBalanceId": return line.sourceBalanceId; case "targetBalanceId": return line.targetBalanceId; default: return line.containerId; }
    })))
    || !unique(lines.flatMap(line => [line.sourceBalanceId, line.targetBalanceId]))
    || lines.some(line => record.state === 0 ? line.executionStatus !== 0 || line.postingStatus !== 0 || scaled(line.reservedQuantity) !== 0n
      : scaled(line.reservedQuantity) + scaled(line.movedQuantity) + scaled(line.closedQuantity) !== scaled(line.plannedQuantity))
    || record.state === 1 && lines.some(line => line.executionStatus !== 0)
    || record.state === 2 && (lines.every(line => line.executionStatus === 0) || lines.every(line => line.postingStatus === 2)
      || lines.some(line => line.executionStatus === 4))
    || record.state === 3 && lines.some(line => line.postingStatus !== 2)
    || record.state === 4 && lines.some(line => line.executionStatus !== 4)
    || record.state === 5 && (!lines.some(line => line.postingStatus === 2) || !lines.some(line => line.executionStatus === 4)
      || lines.some(line => line.postingStatus !== 2 && line.executionStatus !== 4))) throw new TransferResponseError();
  return { id: record.id, code: record.code, warehouseId: record.warehouseId, state: record.state,
    version: record.version, createdAt: record.createdAt, lines };
}
export function transferInputError(command: TransferCommand): string {
  if (!validIdentity(command.operation)) return "原操作 UUID 无效。";
  if (command.kind === "create") {
    const input = command.input;
    if (!/^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$/.test(input.code) || !validIdentity(input.warehouseId)) return "单号或仓库 UUID 无效。";
    if (input.lines.length === 0 || input.lines.length > 100) return "移库须为1至100行。";
    if (input.lines.some(line => ![line.id, line.sourceBalanceId, line.targetBalanceId].every(validIdentity)
      || same(line.sourceBalanceId, line.targetBalanceId) || !inputAmount(line.quantity) || line.branch !== 0 && line.branch !== 1))
      return "行/源/目标 UUID 或正数量无效；至多9位整数6位小数，不拆分整容器。";
    return unique(input.lines.map(line => line.id)) && unique(input.lines.flatMap(line => [line.sourceBalanceId, line.targetBalanceId]))
      ? "" : "行与全部源/目标账维度不得重复。";
  }
  if (!validIdentity(command.id) || !validIdentity(command.input.expectedVersion)) return "原单据或原版本 UUID 无效。";
  const input = command.input;
  if ("lineId" in input && !validIdentity(input.lineId)) return "执行行 UUID 无效。";
  if ("reason" in input && (!input.reason || input.reason.length > 256 || input.reason !== input.reason.trim()
    || /[\u0000-\u001f\u007f-\u009f]/.test(input.reason))) return "原因须为1至256个字符，不接受边界空白或控制字符。";
  if (command.kind === "approval" && (command.input.balances.length < 2 || command.input.balances.length > 200
    || command.input.balances.some(balance => !validIdentity(balance.balanceId) || !validIdentity(balance.expectedVersion))
    || !unique(command.input.balances.map(balance => balance.balanceId)))) return "审核须提供完整、不重复的源/目标账维度版本集。";
  if (command.kind === "arrivals" && (![command.input.sourceLocationId, command.input.targetLocationId, command.input.containerId].every(validIdentity)
    || !inputAmount(command.input.quantity))) return "扫码来源/目标/容器或正数量无效。";
  return "";
}
export function transferActionError(record: TransferDetail, command: TransferCommand): string {
  const invalid = transferInputError(command); if (invalid || command.kind === "create") return invalid;
  if (!same(command.id, record.id) || !same(command.input.expectedVersion, record.version)) return "原单据或原版本不匹配，请关闭后重新读取。";
  if (command.kind === "approval") {
    if (record.state !== 0) return "仅草稿可全行审核。";
    const versions = record.lines.flatMap(line => [{ balanceId: line.sourceBalanceId, expectedVersion: line.sourceVersion },
      { balanceId: line.targetBalanceId, expectedVersion: line.targetVersion }]);
    return versions.length === command.input.balances.length && versions.every(balance => command.input.balances.some(input =>
      same(input.balanceId, balance.balanceId) && same(input.expectedVersion, balance.expectedVersion))) ? "" : "必须固定全部源/目标账维度当前版本，不可只审核部分行。";
  }
  if (command.kind === "closure") return [0, 1, 2].includes(record.state)
    && record.lines.every(line => line.executionStatus === 0 || line.postingStatus === 2)
    && record.lines.some(line => line.executionStatus === 0) ? "" : "仅可关闭确认未执行余量；在途、Unknown、已到待同步不可释放。";
  if (record.state !== 1 && record.state !== 2) return "当前单据不允许执行。";
  const input = command.input; const line = record.lines.find(line => same(line.id, input.lineId));
  if (!line) return "执行行不属于当前单据。";
  if ((command.kind === "dispatch" || command.kind === "arrivals") && line.branch !== 0) return "需运输行不允许人工伪完成；可信运输契约尚未接入。";
  if (command.kind === "dispatch") return line.executionStatus === 0 ? "" : "仅尚未执行人工行可开始搬运。";
  if (command.kind === "synchronizations") return line.executionStatus === 2 && [1, 3].includes(line.postingStatus)
    ? "" : "仅有耐久到达证据的未同步行可补账；不得重运或释放Unknown。";
  if (command.kind === "unknown") return line.executionStatus === 1 ? "" : "仅在途行可标记Unknown。";
  if (command.kind === "arrivals") {
    if (line.executionStatus !== 1) return "仅人工在途行可扫描确认到达。";
    if (![same(command.input.sourceLocationId, line.sourceLocationId), same(command.input.targetLocationId, line.targetLocationId),
      same(command.input.containerId, line.containerId)].every(Boolean)) return "扫码来源、目标或容器不符。";
    return scaled(command.input.quantity) === scaled(line.plannedQuantity) ? "" : "必须确认固定执行单元完整数量，不隐式拆容器。";
  }
  return "";
}
export async function executeTransfer(command: TransferCommand, signal: AbortSignal): Promise<TransferReceipt> {
  const invalid = transferInputError(command); if (invalid) throw new Error(invalid);
  let value: unknown;
  try {
    value = await api(command.kind === "create" ? "/api/wms/transfers" : `/api/wms/transfers/${command.id}/${command.kind}`,
      { method: "POST", signal, headers: { "Idempotency-Key": command.operation }, body: JSON.stringify(command.input) });
  } catch (failure) {
    if (failure instanceof SyntaxError) throw new Error("移库执行回执无效，结果不明；请保留原操作核对。");
    throw failure;
  }
  if (!exact(value, ["decision", "id", "version"]) || value.decision !== 0 && value.decision !== 1
    || !validIdentity(value.id) || !validIdentity(value.version) || command.kind !== "create"
      && (!same(value.id, command.id) || same(value.version, command.input.expectedVersion)))
    throw new Error("移库执行回执无效，结果不明；请保留原操作核对。");
  return { decision: value.decision, id: value.id, version: value.version };
}
