import { api, isRecord, sessionState } from "../identity/identity-api";
import { validBatch } from "./inventory";
import { validIdentity, warehouseFields } from "./master-data";

export interface InboundDraftLine {
  id: string; itemId: string; unitId: string; batch: string; precision: number; planned: string;
}
export interface InboundDraftBody { code: string; warehouseId: string; lines: InboundDraftLine[] }
export interface InboundDraftCommand extends InboundDraftBody { operation: string }
export interface InboundDraftReceipt { decision: 0 | 1; id: string }
export function mayCreateInboundDraft(): boolean {
  const grants = sessionState.subject?.grants;
  const write = new Set(grants?.filter(grant => grant.permission === "wms.inbound.write").flatMap(grant => grant.fields));
  const parent = new Set(grants?.filter(grant => grant.permission === "wms.warehouses.read").flatMap(grant => grant.fields));
  return !!grants && ["Code", "WarehouseId", "Lines"].every(field => write.has(field))
    && warehouseFields.every(field => parent.has(field));
}
export function inboundDraftError(body: InboundDraftBody): string {
  if (!/^[a-z0-9_.-]{1,64}$/i.test(body.code)) return "单号须为 1 至 64 个 ASCII 字母、数字、-、_ 或 .，不自动去除空白。";
  if (!validIdentity(body.warehouseId)) return "请输入有效的非空仓库 UUID。";
  if (body.lines.length < 1 || body.lines.length > 100) return "入库计划须有 1 至 100 行。";
  if (new Set(body.lines.map(line => line.id.toLowerCase())).size !== body.lines.length) return "计划行 UUID 不得重复。";
  for (const [index, line] of body.lines.entries()) {
    const error = `第 ${index + 1} 行：`;
    if (![line.id, line.itemId, line.unitId].every(validIdentity)) return `${error}物料、基础单位及行须有有效非空 UUID。`;
    if (!validBatch(line.batch)) return `${error}批次不能含控制字符或边界空白，最长128字符。`;
    if (!Number.isInteger(line.precision) || line.precision < 0 || line.precision > 6) return `${error}单位精度须显式为0至6。`;
    if (!/^(?:0|[1-9]\d{0,21})(?:\.\d{1,6})?$/.test(line.planned)) return `${error}数量须为正十进制string，最多22位整数、6位小数，不接受舍入。`;
    const [whole, fraction = ""] = line.planned.split(".");
    const value = BigInt(`${whole}${fraction.padEnd(6, "0")}`);
    if (value === 0n || value % (10n ** BigInt(6 - line.precision)) !== 0n) return `${error}数量须为正且符合单位精度。`;
  }
  return "";
}
export async function createInboundDraft(command: InboundDraftCommand, signal: AbortSignal): Promise<InboundDraftReceipt> {
  const error = inboundDraftError(command);
  if (error || !validIdentity(command.operation)) throw new Error(error || "原操作 UUID 无效。");
  let value: unknown;
  try {
    value = await api("/api/wms/inbound", { method: "POST", signal, headers: { "Idempotency-Key": command.operation },
      body: JSON.stringify({ code: command.code, warehouseId: command.warehouseId, lines: command.lines }) });
  } catch (failure) {
    if (failure instanceof SyntaxError) throw new Error("草稿创建响应无效，结果不明；请保留原操作核对。");
    throw failure;
  }
  if (!isRecord(value) || Object.keys(value).length !== 2 || !Object.hasOwn(value, "decision") || !Object.hasOwn(value, "id")
    || value.decision !== 0 && value.decision !== 1 || !validIdentity(value.id))
    throw new Error("草稿创建响应无效，结果不明；请保留原操作核对。");
  return { decision: value.decision, id: value.id };
}
