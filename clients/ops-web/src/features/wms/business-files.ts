import { api, isRecord, sessionState } from "../identity/identity-api";
import { parseStagingFile, readFields, type StagingFile } from "../platform/staging-file";
import { locationFields, validIdentity, warehouseFields, type ResourceKind } from "./master-data";

/** Frozen original reference command; replay never substitutes a new version, key or target. */
export interface BusinessFileCommand {
  readonly kind: ResourceKind;
  readonly entityId: string;
  readonly fileId: string;
  readonly expectedVersion: string;
  readonly operation: string;
  readonly attached: boolean;
}
/** Original committed fact, not a current membership or metadata projection. */
export interface BusinessFileReceipt { id: string; version: string; attached: boolean }
/** Exact current-authorized realtime page without a total or unrelated reference directory. */
export interface BusinessFilePage { rows: StagingFile[]; offset: number; limit: number }

const metadataKeys = ["id", "fileName", "contentType", "byteCount", "state", "stored", "version", "createdAt", "processingProblem"];
function exact(value: unknown, keys: readonly string[]): value is Record<string, unknown> {
  return isRecord(value) && Object.keys(value).length === keys.length && keys.every(key => Object.hasOwn(value, key));
}
function same(left: string, right: string) { return left.toLowerCase() === right.toLowerCase(); }
function collection(kind: ResourceKind, entityId: string) {
  if (!["warehouses", "locations"].includes(kind) || !validIdentity(entityId))
    throw new Error("附件必须继承有效的当前仓库或库位，不能指定其他 owner。");
  return `/api/wms/${kind}/${entityId}/files`;
}
function file(value: unknown): StagingFile {
  if (!exact(value, metadataKeys)) throw new Error("附件元数据响应无效，已丢弃。");
  const result = parseStagingFile(value);
  if (!validIdentity(result.id) || !validIdentity(result.version)) throw new Error("附件身份或版本响应无效，已丢弃。");
  return result;
}
function fields(permission: string, required: readonly string[]) {
  const granted = new Set(sessionState.subject?.grants.filter(grant => grant.permission === permission).flatMap(grant => grant.fields));
  return !!sessionState.subject && required.every(field => granted.has(field));
}
/** UI field gate only; current resource scope and session are always checked by the server. */
export function mayReadBusinessFiles(kind: ResourceKind) {
  return fields("platform.files.read", readFields)
    && fields(`wms.${kind}.read`, kind === "warehouses" ? warehouseFields : locationFields);
}
/** Neither read nor creation fields imply either explicit attachment write action. */
export function mayChangeBusinessFile(kind: ResourceKind, action: "Attach" | "Detach") {
  return mayReadBusinessFiles(kind) && fields("platform.files.write", [action]) && fields(`wms.${kind}.write`, [action]);
}
/** Validates the complete original tuple before preview or transmission; no trimming of opaque versions. */
export function businessCommandError(command: BusinessFileCommand) {
  return !["warehouses", "locations"].includes(command.kind)
    || ![command.entityId, command.fileId, command.expectedVersion, command.operation].every(validIdentity)
    || typeof command.attached !== "boolean" ? "请输入原操作完整的有效非空 UUID、动作和原版本，不得换键或更换目标。" : "";
}
/** Explicit SQL-filtered page read; malformed, duplicate or wrong-filter rows are rejected rather than displayed. */
export async function queryBusinessFiles(kind: ResourceKind, entityId: string, signal: AbortSignal,
  offset = 0, limit = 25, state?: number): Promise<BusinessFilePage> {
  const path = collection(kind, entityId);
  if (!Number.isInteger(offset) || offset < 0 || offset > 10000 || ![25, 50, 100].includes(limit)
    || state !== undefined && (!Number.isInteger(state) || state < 0 || state > 4))
    throw new Error("附件目录分页或状态无效，请重新选择。");
  const value = await api(`${path}?offset=${offset}&limit=${limit}${state === undefined ? "" : `&state=${state}`}`, { signal });
  if (!exact(value, ["decision", "rows", "offset", "limit"]) || value.decision !== 0
    || value.offset !== offset || value.limit !== limit || !Array.isArray(value.rows) || value.rows.length > limit)
    throw new Error("附件目录响应无效，已拒绝选择。");
  const rows = value.rows.map(file);
  if (new Set(rows.map(row => row.id.toLowerCase())).size !== rows.length || rows.some(row => state !== undefined && row.state !== state))
    throw new Error("附件目录包含重复或不符条件的响应，已拒绝选择。");
  return { rows, offset, limit };
}
/** Revalidates one exact entity attachment rather than trusting a selected directory row. */
export async function readBusinessFile(kind: ResourceKind, entityId: string, fileId: string, signal: AbortSignal): Promise<StagingFile> {
  const path = collection(kind, entityId);
  if (!validIdentity(fileId)) throw new Error("附件文件 UUID 无效。");
  const value = await api(`${path}/${fileId}`, { signal });
  if (!exact(value, ["decision", "file"]) || value.decision !== 0) throw new Error("附件读取响应无效，已丢弃。");
  const result = file(value.file);
  if (!same(result.id, fileId)) throw new Error("附件身份与请求不符，已丢弃。");
  return result;
}
/** Sends only the original tuple and accepts only its exact three-field committed receipt. */
export async function changeBusinessFile(command: BusinessFileCommand, signal: AbortSignal): Promise<BusinessFileReceipt> {
  const invalid = businessCommandError(command);
  if (invalid) throw new Error(invalid);
  const value = await api(`${collection(command.kind, command.entityId)}/${command.fileId}`, {
    method: command.attached ? "PUT" : "DELETE", signal, headers: { "Idempotency-Key": command.operation },
    body: JSON.stringify({ expectedVersion: command.expectedVersion }),
  });
  if (!exact(value, ["id", "version", "attached"]) || typeof value.id !== "string" || !same(value.id, command.fileId)
    || typeof value.version !== "string" || !validIdentity(value.version) || value.attached !== command.attached)
    throw new Error("附件操作回执未确认，结果可能已提交；仅可恢复原操作。");
  return { id: value.id, version: value.version, attached: command.attached };
}
