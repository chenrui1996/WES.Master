import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { Blob as ResponseBlob } from "node:buffer";
import FilesPage from "./FilesPage.vue";
import { forgetIdentity, sessionState, setAccessToken } from "../identity/identity-api";

const id = "77966ccd-f1dd-412d-8e45-adb475608842";
const row = { id, fileName: "检验 note.txt", contentType: "text/plain", byteCount: 4, state: 0, stored: true,
  version: "v1", createdAt: "2026-10-27T00:00:00Z", processingProblem: false };
let wrapper: VueWrapper | undefined;
let uploads: UploadXhr[];
beforeEach(() => {
  uploads = [];
  setAccessToken("fixture", "2099-01-01T00:00:00Z");
  sessionState.subject = { tenantId: "t1", userId: "u1", sessionId: "s1", credentialVersion: null, requiresPasswordChange: false,
    grants: [
      { permission: "platform.files.read", scope: 0, departments: [], fields: ["Id", "FileName", "ContentType", "ByteCount", "State", "Stored", "Version", "CreatedAt", "ProcessingProblem"] },
      { permission: "platform.files.write", scope: 0, departments: [], fields: ["Upload", "Scan", "Delete"] },
      { permission: "platform.files.download", scope: 0, departments: [], fields: ["Content"] },
    ] };
  vi.stubGlobal("XMLHttpRequest", class extends UploadXhr { constructor() { super(); uploads.push(this); } });
  vi.stubGlobal("fetch", vi.fn(async () => new Response(JSON.stringify({ decision: 0, file: row }))));
});
afterEach(() => { wrapper?.unmount(); wrapper = undefined; vi.restoreAllMocks(); vi.unstubAllGlobals(); forgetIdentity(); });

async function choose(file = new File(["safe"], "检验 note.txt", { type: "text/plain" })) {
  const input = wrapper!.get<HTMLInputElement>('input[type="file"]');
  Object.defineProperty(input.element, "files", { configurable: true, value: [file] });
  await input.trigger("change");
}
async function read() {
  await wrapper!.get('[name="fileId"]').setValue(id);
  await wrapper!.get('[data-test="file-lookup"]').trigger("submit");
  await flushPromises();
}
async function upload() {
  await wrapper!.get('[data-test="file-upload"]').trigger("submit");
  await flushPromises();
}
function reply(xhr: UploadXhr, status = 200) {
  xhr.status = status;
  xhr.response = new ResponseBlob([JSON.stringify(status === 200
    ? { decision: 0, id, state: 0, version: "v1", rejection: null }
    : { code: "DependencyUnavailable", correlationId: "file-correlation" })], { type: "application/json" });
  xhr.onload?.();
}

it("shows real byte progress then quarantine, never declares a stored upload available", async () => {
  wrapper = mount(FilesPage);
  await choose(); await upload();
  const xhr = uploads[0]!;
  expect(xhr.headers.get("X-WES-File-Name")).toBe("%E6%A3%80%E9%AA%8C%20note.txt");
  expect(xhr.headers.get("Content-Type")).toBe("text/plain");
  xhr.upload.onprogress?.(new ProgressEvent("progress", { lengthComputable: true, loaded: 2, total: 4 }));
  await flushPromises();
  expect(wrapper.text()).toContain("2 / 4");
  reply(xhr); await flushPromises();
  await vi.waitFor(() => expect(wrapper!.find('[data-test="file-download"]').exists()).toBe(true));
  expect(wrapper.text()).toContain("隔离");
  expect(wrapper.get('[data-test="file-download"]').attributes("disabled")).toBeDefined();
  expect(wrapper.text()).not.toContain("扫描尝试已完成；当前状态：可用");
});

it("retains the same immutable file and operation only for an explicit retry after an uncertain failure", async () => {
  wrapper = mount(FilesPage);
  await choose(); await upload();
  const first = uploads[0]!;
  reply(first, 503); await flushPromises();
  await vi.waitFor(() => expect(wrapper!.text()).toContain("file-correlation"));
  expect(wrapper.text()).toContain("file-correlation");
  expect(uploads).toHaveLength(1);
  await upload();
  expect(uploads[1]!.headers.get("Idempotency-Key")).toBe(first.headers.get("Idempotency-Key"));
  expect(uploads[1]!.body).toBe(first.body);
  reply(uploads[1]!);
  await vi.waitFor(() => expect(wrapper!.find('[data-test="file-details"]').exists()).toBe(true));
});

it("rejects an oversized selection before opening a transport", async () => {
  wrapper = mount(FilesPage);
  const file = new File(["x"], "too-large.txt", { type: "text/plain" });
  Object.defineProperty(file, "size", { value: 20_971_521 });
  await choose(file); await upload();
  expect(uploads).toHaveLength(0);
  expect(wrapper.text()).toContain("20971520");
});

it("clears the actual file picker and current record on grant replacement, ignoring old upload progress and success", async () => {
  wrapper = mount(FilesPage);
  await choose(); await upload();
  const old = uploads[0]!;
  sessionState.subject!.grants = [];
  await flushPromises();
  old.upload.onprogress?.(new ProgressEvent("progress", { loaded: 4, total: 4 }));
  reply(old); await flushPromises();
  expect(wrapper.find('[data-test="file-details"]').exists()).toBe(false);
  expect(wrapper.text()).not.toContain("检验 note.txt");
  expect(wrapper.get<HTMLInputElement>('input[type="file"]').element.value).toBe("");
  expect(old.aborted).toBe(true);
});

it("clears prior metadata on 403 and never lets a denied late read recreate it", async () => {
  wrapper = mount(FilesPage);
  await read();
  expect(wrapper.text()).toContain("检验 note.txt");
  vi.stubGlobal("fetch", vi.fn(async () => new Response('{"code":"PermissionDenied","correlationId":"denied"}', { status: 403 })));
  await read();
  expect(wrapper.find('[data-test="file-details"]').exists()).toBe(false);
  expect(wrapper.text()).toContain("denied");
});

it("posts the current scan fence but keeps unavailable scanning isolated without fabricated progress", async () => {
  wrapper = mount(FilesPage);
  await read();
  const calls: [string, RequestInit][] = [];
  vi.stubGlobal("fetch", vi.fn(async (path: string, options: RequestInit) => {
    calls.push([path, options]); return new Response(JSON.stringify({ decision: 0, file: { ...row, version: "v2" } }));
  }));
  await wrapper.get('[data-test="file-scan"]').trigger("click"); await flushPromises();
  expect(calls[0]?.[0]).toBe(`/api/platform/files/staging/${id}/scan`);
  expect(JSON.parse(String(calls[0]?.[1].body))).toEqual({ expectedVersion: "v1" });
  expect(wrapper.text()).toContain("隔离");
  expect(wrapper.find('progress[aria-label="扫描进度"]').exists()).toBe(false);
  expect(wrapper.get('[data-test="file-download"]').attributes("disabled")).toBeDefined();
});

it("requires a separate orphan deletion confirmation and never retries an old version automatically", async () => {
  wrapper = mount(FilesPage);
  await read();
  const writes: RequestInit[] = [];
  vi.stubGlobal("fetch", vi.fn(async (_path: string, options: RequestInit) => {
    writes.push(options); return new Response('{"code":"VersionConflict","correlationId":"changed"}', { status: 412 });
  }));
  await wrapper.get('[data-test="file-delete"]').trigger("click");
  expect(writes).toHaveLength(0);
  await wrapper.get('[data-test="file-delete-confirm"]').trigger("submit"); await flushPromises();
  expect(writes).toHaveLength(1);
  expect(JSON.parse(String(writes[0]!.body))).toEqual({ expectedVersion: "v1" });
  expect(wrapper.text()).toContain("changed");
  expect(wrapper.get('[data-test="file-scan"]').attributes("disabled")).toBeDefined();
});

it("downloads only a currently available record and releases the temporary object URL", async () => {
  vi.stubGlobal("fetch", vi.fn(async (path: string) => path.endsWith("/content")
    ? new Response("safe", { headers: { "Content-Type": "text/plain" } })
    : new Response(JSON.stringify({ decision: 0, file: { ...row, state: 1 } }))));
  const create = vi.fn(() => "blob:fixture");
  const revoke = vi.fn();
  Object.defineProperty(URL, "createObjectURL", { configurable: true, value: create });
  Object.defineProperty(URL, "revokeObjectURL", { configurable: true, value: revoke });
  const click = vi.spyOn(HTMLAnchorElement.prototype, "click").mockImplementation(() => {});
  wrapper = mount(FilesPage); await read();
  await wrapper.get('[data-test="file-download"]').trigger("click"); await flushPromises();
  expect(create).toHaveBeenCalledTimes(1);
  expect(click).toHaveBeenCalledTimes(1);
  expect(revoke).toHaveBeenCalledWith("blob:fixture");
});

it("drops the previous actionable file immediately when the lookup identity is edited", async () => {
  wrapper = mount(FilesPage); await read();
  expect(wrapper.get('[data-test="file-details"]').text()).toContain(id);
  await wrapper.get('[name="fileId"]').setValue("d0677920-7870-4476-8966-2f534d3c6c07");
  expect(wrapper.find('[data-test="file-details"]').exists()).toBe(false);
});

it("discards binary bytes that arrive after the session changes before creating a download URL", async () => {
  let finish: ((response: Response) => void) | undefined;
  vi.stubGlobal("fetch", vi.fn((path: string) => path.endsWith("/content")
    ? new Promise<Response>(resolve => { finish = resolve; })
    : Promise.resolve(new Response(JSON.stringify({ decision: 0, file: { ...row, state: 1 } })))));
  const create = vi.fn(() => "blob:must-not-be-used");
  Object.defineProperty(URL, "createObjectURL", { configurable: true, value: create });
  wrapper = mount(FilesPage); await read();
  await wrapper.get('[data-test="file-download"]').trigger("click");
  sessionState.subject!.sessionId = "replacement-session";
  await flushPromises();
  finish?.(new Response("safe", { headers: { "Content-Type": "text/plain" } }));
  await flushPromises();
  expect(create).not.toHaveBeenCalled();
  expect(wrapper.find('[data-test="file-details"]').exists()).toBe(false);
});

it("cancel aborts upload but preserves its identity only for manual same-file retry", async () => {
  wrapper = mount(FilesPage); await choose(); await upload();
  const first = uploads[0]!;
  await wrapper.findAll("button").find(button => button.text().startsWith("取消等待"))!.trigger("click");
  expect(first.aborted).toBe(true);
  expect(wrapper.text()).toContain("结果可能未确认");
  await upload();
  expect(uploads[1]!.headers.get("Idempotency-Key")).toBe(first.headers.get("Idempotency-Key"));
  expect(uploads[1]!.body).toBe(first.body);
});

it("offline submission cannot open a transport and never queues a later upload", async () => {
  wrapper = mount(FilesPage); await choose();
  vi.spyOn(navigator, "onLine", "get").mockReturnValue(false);
  await upload();
  expect(uploads).toHaveLength(0);
  expect(wrapper.text()).toContain("离线");
  vi.spyOn(navigator, "onLine", "get").mockReturnValue(true);
  window.dispatchEvent(new Event("online"));
  await flushPromises();
  expect(uploads).toHaveLength(0);
});

it("a version refresh failure retains only visibly stale metadata with all mutations blocked", async () => {
  wrapper = mount(FilesPage); await read();
  vi.stubGlobal("fetch", vi.fn(async () => new Response('{"code":"DependencyUnavailable","correlationId":"refresh-failed"}', { status: 503 })));
  await read();
  expect(wrapper.get('[data-test="file-details"]').text()).toContain(id);
  expect(wrapper.get('[aria-label="暂存文件元数据"] [role="status"]').text()).toContain("refresh-failed");
  for (const action of ["file-scan", "file-download", "file-delete"])
    expect(wrapper.get(`[data-test="${action}"]`).attributes("disabled")).toBeDefined();
});

it("upload permission without scan/delete fields never enables those actions", async () => {
  sessionState.subject!.grants[1]!.fields = ["Upload"];
  wrapper = mount(FilesPage); await read();
  expect(wrapper.get('[data-test="file-scan"]').attributes("disabled")).toBeDefined();
  expect(wrapper.get('[data-test="file-delete"]').attributes("disabled")).toBeDefined();
});

class UploadXhr {
  headers = new Headers(); body: Blob | null = null; status = 0; response: ResponseBlob | null = null; aborted = false;
  upload: { onprogress: ((event: ProgressEvent) => void) | null } = { onprogress: null };
  responseType = ""; timeout = 0;
  onload: (() => void) | null = null; onerror: (() => void) | null = null;
  onabort: (() => void) | null = null; ontimeout: (() => void) | null = null;
  open() {}
  setRequestHeader(key: string, value: string) { this.headers.set(key, value); }
  getAllResponseHeaders() { return "Content-Type: application/json\r\n"; }
  send(body: Blob) { this.body = body; }
  abort() { this.aborted = true; this.onabort?.(); }
}
