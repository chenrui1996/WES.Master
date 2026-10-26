import { isRecord } from "../identity/identity-api";

export interface StagingFile {
  id: string; fileName: string; contentType: string; byteCount: number; state: number; stored: boolean;
  version: string; createdAt: string; processingProblem: boolean;
}
export const maximum = 20971520;
export const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
export const readFields = ["Id", "FileName", "ContentType", "ByteCount", "State", "Stored", "Version", "CreatedAt", "ProcessingProblem"];
export const types: Record<string, string> = { pdf: "application/pdf", png: "image/png", jpg: "image/jpeg", jpeg: "image/jpeg", txt: "text/plain" };
export const labels = ["隔离待处理", "可用（未绑定）", "扫描拒绝", "删除处理中", "已删除"];
export function validName(name: string) {
  return name.length > 0 && name.length <= 255 && !/[\u0000-\u001f\u007f/\\:]/u.test(name) && !/[. ]$/u.test(name);
}
export function parseStagingFile(file: unknown): StagingFile {
  if (!isRecord(file) || typeof file.id !== "string" || !uuid.test(file.id) || typeof file.fileName !== "string"
    || !validName(file.fileName) || typeof file.contentType !== "string" || !Object.values(types).includes(file.contentType)
    || typeof file.byteCount !== "number" || !Number.isInteger(file.byteCount) || file.byteCount < 1 || file.byteCount > maximum
    || typeof file.state !== "number" || !Number.isInteger(file.state) || file.state < 0 || file.state > 4
    || typeof file.stored !== "boolean" || typeof file.version !== "string" || !file.version
    || typeof file.createdAt !== "string" || !Number.isFinite(Date.parse(file.createdAt))
    || typeof file.processingProblem !== "boolean") throw new Error("文件元数据响应无效，已丢弃，请重新读取。");
  return { id: file.id, fileName: file.fileName, contentType: file.contentType, byteCount: file.byteCount, state: file.state,
    stored: file.stored, version: file.version, createdAt: file.createdAt, processingProblem: file.processingProblem };
}
