import { api, isRecord, sessionState } from "../identity/identity-api";
import { validIdentity, warehouseFields, locationFields, type ResourceKind } from "./master-data";

export interface StateCommand { id: string; operation: string; expectedVersion: string; enabled: boolean; reason: string }
export interface StateReceipt { decision: 0 | 1; id: string; version: string }
export function mayChangeMasterDataState(kind: ResourceKind = "warehouses"): boolean {
  const grants = sessionState.subject?.grants;
  const write = new Set(grants?.filter(grant => grant.permission === `wms.${kind}.write`).flatMap(grant => grant.fields));
  const read = new Set(grants?.filter(grant => grant.permission === `wms.${kind}.read`).flatMap(grant => grant.fields));
  const parent = new Set(grants?.filter(grant => grant.permission === "wms.warehouses.read").flatMap(grant => grant.fields));
  return !!grants && write.has("Enabled") && (kind === "warehouses" ? warehouseFields : locationFields).every(field => read.has(field))
    && (kind === "warehouses" || warehouseFields.every(field => parent.has(field)));
}
export function stateError(command: StateCommand): string {
  if (![command.id, command.operation, command.expectedVersion].every(validIdentity)) return "资源、原操作及期望版本 UUID 必须有效。";
  if (!command.reason.length || command.reason.length > 256 || command.reason !== command.reason.trim()
    || /[\u0000-\u001f\u007f-\u009f]/.test(command.reason)) return "请输入1至256字符的启停原因，不接受控制字符或边界空白。";
  return "";
}
export async function changeMasterDataState(command: StateCommand, signal: AbortSignal, kind: ResourceKind = "warehouses"): Promise<StateReceipt> {
  const error = stateError(command); if (error) throw new Error(error);
  let value: unknown;
  try {
    value = await api(`/api/wms/${kind}/${command.id}/state`, { method: "PUT", signal,
      headers: { "Idempotency-Key": command.operation },
      body: JSON.stringify({ expectedVersion: command.expectedVersion, enabled: command.enabled, reason: command.reason }) });
  } catch (failure) {
    if (failure instanceof SyntaxError) throw new Error("主数据启停响应无效，结果不明；请保留原操作核对。");
    throw failure;
  }
  if (!isRecord(value) || Object.keys(value).length !== 3 || !["decision", "id", "version"].every(key => Object.hasOwn(value, key))
    || value.decision !== 0 && value.decision !== 1 || !validIdentity(value.id) || !validIdentity(value.version)
    || value.id.toLowerCase() !== command.id.toLowerCase() || value.version.toLowerCase() === command.expectedVersion.toLowerCase())
    throw new Error("主数据启停响应无效，结果不明；请保留原操作核对。");
  return { decision: value.decision, id: value.id, version: value.version };
}
