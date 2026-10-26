import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { api, apiBlob, forgetIdentity, sessionState, setAccessToken } from "./identity-api";

beforeEach(() => { forgetIdentity(); setAccessToken("fixture", "2099-01-01T00:00:00Z"); });
afterEach(() => { vi.unstubAllGlobals(); forgetIdentity(); });

it("preserves a raw file MIME and permanent operation instead of coercing it to JSON", async () => {
  let sent: RequestInit | undefined;
  vi.stubGlobal("fetch", vi.fn(async (_url: string, options: RequestInit) => {
    sent = options; return new Response("{}");
  }));
  const body = new Blob(["safe"], { type: "text/plain" });
  await api("/api/platform/files/staging", { method: "POST", body,
    headers: { "Content-Type": "text/plain", "Idempotency-Key": "permanent-operation" } });
  expect(sent?.body).toBe(body);
  expect(new Headers(sent?.headers).get("Content-Type")).toBe("text/plain");
  expect(new Headers(sent?.headers).get("Idempotency-Key")).toBe("permanent-operation");
});

it("reads a binary result without trying JSON and rechecks scope after its body finishes", async () => {
  vi.stubGlobal("fetch", vi.fn(async () => new Response("safe", { headers: { "Content-Type": "text/plain" } })));
  const blob = await apiBlob("/api/platform/files/staging/id/content");
  const text = await new Promise<string>((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => resolve(String(reader.result));
    reader.onerror = () => reject(reader.error);
    reader.readAsText(blob);
  });
  expect(text).toBe("safe");
  let finish: ReadableStreamDefaultController<Uint8Array> | undefined;
  const stream = new ReadableStream<Uint8Array>({ start(controller) { finish = controller; } });
  vi.stubGlobal("fetch", vi.fn(async () => new Response(stream)));
  const pending = apiBlob("/api/platform/files/staging/id/content");
  await Promise.resolve(); await Promise.resolve();
  forgetIdentity();
  finish!.enqueue(new TextEncoder().encode("private")); finish!.close();
  await expect(pending).rejects.toThrow("身份上下文已变更");
});

it("reports only current real upload byte events, propagates cancellation and never replays XHR", async () => {
  const requests: UploadXhr[] = [];
  vi.stubGlobal("XMLHttpRequest", class extends UploadXhr { constructor() { super(); requests.push(this); } });
  const progress = vi.fn();
  const controller = new AbortController();
  const pending = api("/api/platform/files/staging", {
    method: "POST", body: new Blob(["safe"]), signal: controller.signal, headers: { "Content-Type": "text/plain" },
  }, { onUploadProgress: progress });
  const xhr = requests[0]!;
  expect(xhr.url).toBe("/api/platform/files/staging");
  expect(xhr.headers.get("Content-Type")).toBe("text/plain");
  xhr.upload.onprogress?.(new ProgressEvent("progress", { lengthComputable: true, loaded: 2, total: 4 }));
  expect(progress).toHaveBeenCalledWith(2, 4);
  controller.abort();
  await expect(pending).rejects.toMatchObject({ name: "AbortError" });
  xhr.upload.onprogress?.(new ProgressEvent("progress", { loaded: 4, total: 4 }));
  expect(progress).toHaveBeenCalledTimes(1);
  expect(requests).toHaveLength(1);
});

it("rejects a successful late XHR after the current grants changed without delivering progress", async () => {
  sessionState.subject = { tenantId: "t", userId: "u", sessionId: "s", requiresPasswordChange: false, credentialVersion: null,
    grants: [{ permission: "platform.files.write", scope: 0, departments: [], fields: ["Upload"] }] };
  let xhr: UploadXhr | undefined;
  vi.stubGlobal("XMLHttpRequest", class extends UploadXhr { constructor() { super(); xhr = this; } });
  const progress = vi.fn();
  const pending = api("/api/platform/files/staging", { method: "POST", body: new Blob(["safe"]) }, { onUploadProgress: progress });
  sessionState.subject.grants = [];
  xhr!.upload.onprogress?.(new ProgressEvent("progress", { loaded: 2, total: 4 }));
  xhr!.status = 200; xhr!.response = new Blob(["{}"]); xhr!.onload?.();
  await expect(pending).rejects.toThrow("身份上下文已变更");
  expect(progress).not.toHaveBeenCalled();
});

class UploadXhr {
  upload: { onprogress: ((event: ProgressEvent) => void) | null } = { onprogress: null };
  headers = new Headers();
  url = "";
  status = 0;
  response: Blob | null = null;
  responseType = "";
  timeout = 0;
  onload: (() => void) | null = null;
  onerror: (() => void) | null = null;
  ontimeout: (() => void) | null = null;
  onabort: (() => void) | null = null;
  open(_method: string, url: string) { this.url = url; }
  setRequestHeader(name: string, value: string) { this.headers.set(name, value); }
  getAllResponseHeaders() { return "Content-Type: application/json\r\n"; }
  send(_body: Blob) {}
  abort() { this.onabort?.(); }
}
