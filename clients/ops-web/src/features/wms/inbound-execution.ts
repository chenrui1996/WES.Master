import { api, isRecord, sessionState } from "../identity/identity-api";
import { validIdentity, warehouseFields, locationFields } from "./master-data";
import { mayReadInbound, type InboundDetail } from "./inbound";
import { mayReadStockResource } from "./stock-resources";

export type InboundExecutionKind = "approval" | "receipts";
export interface ReceiptInput {
  expectedVersion: string; expectedBalanceVersion: string; lineId: string; executionId: string; quantity: string;
  containerId: string; receiptLocationId: string; targetLocationId: string; balanceId: string;
}
export type InboundExecutionCommand = { operation: string; id: string } & (
  | { kind: "approval"; input: { expectedVersion: string } }
  | { kind: "receipts"; input: ReceiptInput }
);
export interface InboundExecutionReceipt { decision: 0 | 1; id: string; version: string }
function fields(permission: string, required: readonly string[]): boolean {
  const visible = new Set(sessionState.subject?.grants.filter(grant => grant.permission === permission).flatMap(grant => grant.fields));
  return required.every(field => visible.has(field));
}
export function mayExecuteInbound(kind: InboundExecutionKind): boolean {
  return mayReadInbound() && fields("wms.warehouses.read", warehouseFields)
    && mayReadStockResource("items") && mayReadStockResource("units")
    && (kind === "approval" ? fields("wms.inbound.approve", ["State"])
      : fields("wms.inbound.receive", ["LineId", "ExecutionId", "Quantity", "ContainerId", "ReceiptLocationId", "TargetLocationId", "BalanceId"])
        && fields("wms.locations.read", locationFields) && mayReadStockResource("containers"));
}
function scaled(value: string): bigint {
  const [whole, fraction = ""] = value.split(".");
  return BigInt(`${whole}${fraction.padEnd(6, "0")}`);
}
function receiptError(input: ReceiptInput): string {
  if (![input.expectedBalanceVersion, input.lineId, input.executionId, input.containerId,
    input.receiptLocationId, input.targetLocationId, input.balanceId].every(validIdentity))
    return "请选择计划行，并填写有效的容器、源目标库位、合法余额及原余额版本 UUID。";
  if (!/^(?:0|[1-9]\d{0,8})(?:\.\d{1,6})?$/.test(input.quantity)
    || scaled(input.quantity) <= 0n || scaled(input.quantity) > 999999999999999n)
    return "收货数量须为正十进制 string，至多9位整数、6位小数，不接受舍入。";
  return "";
}
export function inboundReceiptError(order: InboundDetail, input: ReceiptInput): string {
  const error = receiptError(input); if (error) return error;
  const line = order.lines.find(candidate => candidate.id === input.lineId);
  if (!line || scaled(input.quantity) > scaled(line.remaining)
    || scaled(input.quantity) % (10n ** BigInt(6 - line.precision)) !== 0n)
    return "数量须符合真实计划行单位精度且不超过待收数量；不自动舍入。";
  return "";
}
export async function executeInbound(command: InboundExecutionCommand, signal: AbortSignal): Promise<InboundExecutionReceipt> {
  if (![command.id, command.operation, command.input.expectedVersion].every(validIdentity))
    throw new Error("原单据、操作及原版本 UUID 无效。");
  if (command.kind === "receipts") { const error = receiptError(command.input); if (error) throw new Error(error); }
  let value: unknown;
  try {
    value = await api(`/api/wms/inbound/${command.id}/${command.kind}`, { method: "POST", signal,
      headers: { "Idempotency-Key": command.operation }, body: JSON.stringify(command.input) });
  } catch (failure) {
    if (failure instanceof SyntaxError) throw new Error("执行回执无效，结果不明；请保留原操作核对。");
    throw failure;
  }
  const resultId = command.kind === "approval" ? command.id : command.input.executionId;
  if (!isRecord(value) || Object.keys(value).length !== 3 || !["decision", "id", "version"].every(key => Object.hasOwn(value, key))
    || value.decision !== 0 && value.decision !== 1 || !validIdentity(value.id) || !validIdentity(value.version)
    || value.id.toLowerCase() !== resultId.toLowerCase() || value.version.toLowerCase() === command.input.expectedVersion.toLowerCase())
    throw new Error("执行回执无效，结果不明；请保留原操作核对。");
  return { decision: value.decision, id: value.id, version: value.version };
}
