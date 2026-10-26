import { reactive, watch } from "vue";

export interface Grant { permission: string; scope: number; departments: string[]; fields: string[] }
export interface Subject {
  tenantId: string; userId: string; sessionId: string; requiresPasswordChange: boolean;
  credentialVersion: string | null; grants: Grant[];
}
export const sessionState = reactive<{ token: string | null; expiresAt: string | null; subject: Subject | null }>({
  token: null, expiresAt: null, subject: null,
});
let generation = 0;
watch(() => sessionState.subject, () => { generation++; }, { deep: true, flush: "sync" });
function requireGeneration(expected: number) {
  if (expected !== generation) throw new Error("身份上下文已变更，已丢弃旧响应，请重新读取。");
}
export function setAccessToken(token: string, expires: string) {
  if (!token || !Number.isFinite(Date.parse(expires))) throw new Error("身份响应格式无效，请重新登录。");
  sessionState.token = token;
  sessionState.expiresAt = expires;
  sessionState.subject = null;
  generation++;
}
export function forgetIdentity() {
  sessionState.token = null;
  sessionState.expiresAt = null;
  sessionState.subject = null;
  generation++;
}
export class ApiError extends Error {
  constructor(public readonly status: number, public readonly code: string, public readonly correlation: string) {
    super(`${code}。请检查输入或重新读取后重试。关联标识：${correlation}`);
  }
}
export interface UploadProgress { onUploadProgress: (loaded: number, total: number | null) => void }
export async function api<T = unknown>(path: string, options: RequestInit = {}, upload?: UploadProgress): Promise<T> {
  const { response, requestedGeneration } = await request(path, options, upload);
  const value: unknown = response.status === 204 ? undefined : await response.json();
  requireGeneration(requestedGeneration);
  return value as T;
}
export async function apiBlob(path: string, options: RequestInit = {}): Promise<Blob> {
  const { response, requestedGeneration } = await request(path, options);
  const value = await response.blob();
  requireGeneration(requestedGeneration);
  return value;
}
async function request(path: string, options: RequestInit, upload?: UploadProgress) {
  if (!path.startsWith("/api/") || path.includes("\\") || path.includes("#"))
    throw new Error("只允许同源 API 请求。");
  if (!navigator.onLine) throw new Error("当前离线，未发送请求。恢复连接后请重新读取并手动提交。");
  const headers = new Headers(options.headers);
  headers.set("X-WES-Client", "ops-web");
  const method = (options.method ?? "GET").toUpperCase();
  if (path.startsWith("/api/platform/") && ["POST", "PUT", "PATCH", "DELETE"].includes(method)
    && !/\/credential(?:\?|$)/.test(path) && !/\/tenants\/switch-check(?:\?|$)/.test(path)
    && !/\/users\/[^/]+\/disable(?:\?|$)/.test(path)
    && !headers.has("Idempotency-Key")) headers.set("Idempotency-Key", crypto.randomUUID());
  if (options.body && !headers.has("Content-Type") && typeof options.body === "string") headers.set("Content-Type", "application/json");
  if (sessionState.token) headers.set("Authorization", `Bearer ${sessionState.token}`);
  const requestedGeneration = generation;
  const response = upload ? await uploadResponse(path, method, headers, options, upload, requestedGeneration)
    : await fetch(path, { ...options, headers, credentials: "same-origin", cache: "no-store" });
  requireGeneration(requestedGeneration);
  if (!response.ok) {
    if (response.status === 401) forgetIdentity();
    const errorGeneration = generation;
    const text = await response.text();
    requireGeneration(errorGeneration);
    let problem: unknown;
    try { problem = text ? JSON.parse(text) : {}; }
    catch (error) {
      if (!(error instanceof SyntaxError)) throw error;
      problem = {};
    }
    const record = isRecord(problem) ? problem : {};
    throw new ApiError(response.status,
      typeof record.code === "string" ? record.code.slice(0, 100) : `RequestFailed${response.status}`,
      typeof record.correlationId === "string" ? record.correlationId.slice(0, 64) : "未提供");
  }
  return { response, requestedGeneration };
}

function uploadResponse(path: string, method: string, headers: Headers, options: RequestInit,
  progress: UploadProgress, requestedGeneration: number): Promise<Response> {
  if (!(options.body instanceof Blob)) return Promise.reject(new Error("上传必须使用已限定大小的文件字节。"));
  const body = options.body;
  return new Promise((resolve, reject) => {
    const xhr = new XMLHttpRequest();
    const signal = options.signal;
    let finished = false;
    function complete(action: () => void) {
      if (finished) return;
      finished = true;
      signal?.removeEventListener("abort", abort);
      xhr.upload.onprogress = null;
      action();
    }
    function abort() {
      xhr.abort();
      complete(() => reject(new DOMException("上传已取消，结果可能未确认；仅显式重试原文件。", "AbortError")));
    }
    xhr.open(method, path);
    xhr.responseType = "blob";
    xhr.timeout = 120000;
    headers.forEach((value, key) => xhr.setRequestHeader(key, value));
    xhr.upload.onprogress = event => {
      if (!finished && !signal?.aborted && requestedGeneration === generation)
        progress.onUploadProgress(event.loaded, event.lengthComputable ? event.total : null);
    };
    xhr.onload = () => complete(() => {
      if (signal?.aborted) { reject(new DOMException("上传已取消。", "AbortError")); return; }
      const responseHeaders = new Headers();
      for (const line of xhr.getAllResponseHeaders().trim().split(/[\r\n]+/)) {
        const separator = line.indexOf(":");
        if (separator > 0) responseHeaders.append(line.slice(0, separator), line.slice(separator + 1).trim());
      }
      if (xhr.status < 200 || xhr.status > 599) { reject(new Error("上传未取得有效确认，请显式重试原文件。")); return; }
      resolve(new Response(xhr.status === 204 ? null : xhr.response, { status: xhr.status, headers: responseHeaders }));
    });
    xhr.onerror = () => complete(() => reject(new Error("上传连接中断，结果未确认；请显式重试原文件。")));
    xhr.ontimeout = () => complete(() => reject(new Error("上传确认超时，结果未确认；请显式重试原文件。")));
    xhr.onabort = () => complete(() => reject(new DOMException("上传已取消，结果可能未确认。", "AbortError")));
    signal?.addEventListener("abort", abort, { once: true });
    if (signal?.aborted) { abort(); return; }
    try { xhr.send(body); }
    catch (error) { complete(() => reject(error)); }
  });
}

export function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}
export interface TokenDelivery { accessToken: string; expiresAt: string; requiresPasswordChange: boolean; returnUrl: string }
export function acceptToken(value: unknown): TokenDelivery {
  if (!isRecord(value) || typeof value.accessToken !== "string" || typeof value.expiresAt !== "string"
    || typeof value.requiresPasswordChange !== "boolean" || typeof value.returnUrl !== "string")
    throw new Error("身份响应格式无效，请重新登录。");
  setAccessToken(value.accessToken, value.expiresAt);
  return { accessToken: value.accessToken, expiresAt: value.expiresAt,
    requiresPasswordChange: value.requiresPasswordChange, returnUrl: value.returnUrl };
}
export async function loadSubject(): Promise<Subject> {
  const value: unknown = await api("/api/identity/me");
  if (!isRecord(value) || !["tenantId", "userId", "sessionId"].every(key => typeof value[key] === "string")
    || typeof value.requiresPasswordChange !== "boolean" || !Array.isArray(value.grants)
    || !(value.credentialVersion === null || typeof value.credentialVersion === "string"))
    throw new Error("当前身份响应无效，请退出后重新登录。");
  const grants: Grant[] = value.grants.map((grant: unknown) => {
    if (!isRecord(grant) || typeof grant.permission !== "string" || typeof grant.scope !== "number"
      || !Array.isArray(grant.fields) || !grant.fields.every((field: unknown) => typeof field === "string")
      || !Array.isArray(grant.departments) || !grant.departments.every((id: unknown) => typeof id === "string"))
      throw new Error("权限响应无效，请退出后重新登录。");
    return { permission: grant.permission, scope: grant.scope, fields: grant.fields, departments: grant.departments };
  });
  const subject: Subject = { tenantId: String(value.tenantId), userId: String(value.userId), sessionId: String(value.sessionId),
    requiresPasswordChange: value.requiresPasswordChange, credentialVersion: value.credentialVersion, grants };
  sessionState.subject = subject;
  return subject;
}
export function canRead(permission: string): boolean {
  return sessionState.subject?.grants.some(grant => grant.permission === permission) ?? false;
}
