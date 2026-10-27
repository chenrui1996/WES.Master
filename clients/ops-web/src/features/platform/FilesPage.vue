<script setup lang="ts">
import { computed, onUnmounted, ref, shallowRef, watch } from "vue";
import QueryState from "../../components/QueryState.vue";
import { api, ApiError, isRecord, sessionState } from "../identity/identity-api";
import { downloadAuthorizedFile } from "./file-download";
import StagingFileCatalog from "./StagingFileCatalog.vue";
import { labels, maximum, parseStagingFile, readFields, types, uuid, validName, type StagingFile } from "./staging-file";
const record = ref<StagingFile | null>(null);
const selected = shallowRef<File | null>(null);
const picker = ref<HTMLInputElement | null>(null);
const lookupId = ref("");
const operation = ref<string | null>(null);
const error = ref(""); const message = ref(""); const busy = ref(false);
const loading = ref(false); const fresh = ref(false); const confirmDelete = ref(false);
const sent = ref(0); const total = ref<number | null>(null); const uploading = ref(false);
let controller = new AbortController(); let sequence = 0;
const scope = computed(() => JSON.stringify(sessionState.subject));
function fields(permission: string) {
  return new Set(sessionState.subject?.grants.filter(grant => grant.permission === permission).flatMap(grant => grant.fields) ?? []);
}
const mayRead = computed(() => readFields.every(field => fields("platform.files.read").has(field)));
const mayUpload = computed(() => fields("platform.files.write").has("Upload"));
const mayScan = computed(() => fields("platform.files.write").has("Scan"));
const mayDelete = computed(() => fields("platform.files.write").has("Delete"));
const mayDownload = computed(() => fields("platform.files.download").has("Content"));
const usable = computed(() => fresh.value && !!record.value && !busy.value);
const queryState = computed(() => loading.value ? record.value ? "stale" : "loading"
  : !fresh.value && record.value ? "stale" : record.value ? "ready" : "empty");
function clearSelection() {
  selected.value = null; operation.value = null;
  if (picker.value) picker.value.value = "";
}
function invalidate() {
  ++sequence; controller.abort(); controller = new AbortController();
  record.value = null; fresh.value = false; busy.value = false; loading.value = false;
  uploading.value = false; confirmDelete.value = false; lookupId.value = "";
  sent.value = 0; total.value = null; error.value = ""; message.value = ""; clearSelection();
}
watch(scope, invalidate, { flush: "sync" });
watch(lookupId, id => {
  if (record.value && record.value.id.toLowerCase() !== id.trim().toLowerCase()) {
    record.value = null; fresh.value = false; confirmDelete.value = false;
  }
}, { flush: "sync" });
onUnmounted(invalidate);
function begin() {
  controller.abort(); controller = new AbortController();
  busy.value = true; error.value = ""; message.value = ""; confirmDelete.value = false;
  const ticket = ++sequence;
  const fingerprint = scope.value;
  const signal = controller.signal;
  return { ticket, signal, current: () => ticket === sequence && fingerprint === scope.value && !signal.aborted };
}
function failed(failure: unknown) {
  fresh.value = false; confirmDelete.value = false;
  if (failure instanceof ApiError && (failure.status === 401 || failure.status === 403)) {
    record.value = null; lookupId.value = ""; clearSelection();
  }
  error.value = failure instanceof Error ? failure.message : "请求未确认，请重新读取后显式重试。";
}
function parseFile(value: unknown): StagingFile {
  if (!isRecord(value) || value.decision !== 0 || !isRecord(value.file)) throw new Error("文件响应未确认，请重新读取。");
  return parseStagingFile(value.file);
}
async function loadRecord(id: string, signal: AbortSignal): Promise<StagingFile> {
  const result = parseFile(await api(`/api/platform/files/staging/${id}`, { signal }));
  if (result.id.toLowerCase() !== id.toLowerCase()) throw new Error("文件身份与请求不符，已丢弃响应。");
  return result;
}
function fileType(file: File) { return file.type || types[file.name.split(".").pop()?.toLowerCase() ?? ""] || ""; }
function selectFile(event: Event) {
  const input = event.target;
  if (!(input instanceof HTMLInputElement)) return;
  const file = input.files?.[0];
  invalidate();
  if (!file) return;
  if (!validName(file.name) || !types[file.name.split(".").pop()?.toLowerCase() ?? ""]) {
    error.value = "仅允许有效文件名的 PDF、PNG、JPEG、TXT；不得包含路径或控制字符。"; return;
  }
  if (file.size < 1 || file.size > maximum) { error.value = "文件必须为 1 至 20971520 字节。"; return; }
  selected.value = file; operation.value = crypto.randomUUID();
}
async function readFile() {
  if (!mayRead.value || busy.value) return;
  const id = lookupId.value.trim();
  if (!uuid.test(id)) { error.value = "请输入有效文件 UUID。"; return; }
  if (record.value?.id.toLowerCase() !== id.toLowerCase()) record.value = null;
  fresh.value = false;
  const run = begin(); loading.value = true;
  try {
    const value = await loadRecord(id, run.signal);
    if (run.current()) { record.value = value; fresh.value = true; }
  } catch (failure) { if (run.current()) failed(failure); }
  finally { if (run.ticket === sequence) { busy.value = false; loading.value = false; } }
}
function selectCatalogFile(id: string) {
  if (busy.value) return;
  lookupId.value = id;
  void readFile();
}
async function uploadFile() {
  if (!mayUpload.value || busy.value || !selected.value || !operation.value) return;
  const file = selected.value; const key = operation.value;
  const run = begin(); uploading.value = true; sent.value = 0; total.value = file.size;
  try {
    const value: unknown = await api("/api/platform/files/staging", { method: "POST", body: file, signal: run.signal,
      headers: { "Content-Type": fileType(file), "Idempotency-Key": key,
        "X-WES-File-Name": encodeURIComponent(file.name).replace(/[!'()*]/g, char => `%${char.charCodeAt(0).toString(16).toUpperCase()}`) },
    }, { onUploadProgress: (loaded, size) => { if (run.current()) { sent.value = loaded; total.value = size; } } });
    if (!run.current()) return;
    if (!isRecord(value) || value.decision !== 0 || typeof value.id !== "string" || !uuid.test(value.id)
      || typeof value.version !== "string" || !value.version || typeof value.state !== "number"
      || !Number.isInteger(value.state) || value.state < 0 || value.state > 4) throw new Error("上传结果未确认，请显式重试原文件。");
    lookupId.value = value.id; clearSelection();
    message.value = `隔离存储已确认；当前状态：${labels[value.state]}。这不是业务附件或扫描成功。`;
    uploading.value = false;
    if (mayRead.value) {
      const current = await loadRecord(value.id, run.signal);
      if (run.current()) { record.value = current; fresh.value = true; }
    }
  } catch (failure) { if (run.current()) failed(failure); }
  finally { if (run.ticket === sequence) { busy.value = false; uploading.value = false; } }
}
async function processFile(deleting: boolean) {
  const file = record.value;
  if (!usable.value || !file || (deleting ? !mayDelete.value || !confirmDelete.value : !mayScan.value)) return;
  const run = begin(); fresh.value = false;
  try {
    const result = await api(`/api/platform/files/staging/${file.id}${deleting ? "" : "/scan"}`, {
      method: deleting ? "DELETE" : "POST", signal: run.signal, body: JSON.stringify({ expectedVersion: file.version }),
    });
    if (!run.current()) return;
    const next = parseFile(result);
    if (next.id !== file.id) throw new Error("文件身份与请求不符，已丢弃响应。");
    record.value = next; fresh.value = true;
    message.value = deleting ? "孤儿删除操作已确认；永久身份和审计仍保留。"
      : `扫描尝试已完成；当前状态：${labels[next.state]}。仅可用状态允许下载。`;
  } catch (failure) { if (run.current()) failed(failure); }
  finally { if (run.ticket === sequence) busy.value = false; }
}
async function downloadFile() {
  const file = record.value;
  if (!usable.value || !file || file.state !== 1 || !file.stored || !mayDownload.value || !mayRead.value) return;
  const run = begin();
  try {
    if (await downloadAuthorizedFile(`/api/platform/files/staging/${file.id}/content`, file, run.signal,
      () => run.current() && record.value?.id === file.id && record.value.version === file.version))
      message.value = "已发起受权下载；请在浏览器中确认接收结果。";
  } catch (failure) { if (run.current()) failed(failure); }
  finally { if (run.ticket === sequence) busy.value = false; }
}
function cancel() {
  controller.abort(); ++sequence; busy.value = false; uploading.value = false; loading.value = false;
  fresh.value = false; confirmDelete.value = false;
  error.value = "请求已取消，服务端结果可能未确认。可显式重试原文件上传，或重新读取文件状态；不会自动重放。";
}
</script>

<template>
  <section aria-labelledby="files-heading" class="files-page">
    <header><p class="eyebrow">PLATFORM · 暂存文件</p><h1 id="files-heading">文件隔离与处理</h1>
      <p class="hint">仅处理未绑定暂存文件，不代表业务附件。文件不会保存到本机缓存；扫描未通过不能下载。</p></header>
    <p v-if="error" role="alert" class="error">{{ error }}</p>
    <p v-if="message" role="status" class="notice">{{ message }}</p>
    <form data-test="file-upload" @submit.prevent="uploadFile" aria-label="上传暂存文件">
      <fieldset :disabled="busy || !mayUpload"><legend>选择并显式上传</legend>
        <label>文件（PDF / PNG / JPEG / TXT，最多 20 MiB）
          <input ref="picker" type="file" accept=".pdf,.png,.jpg,.jpeg,.txt" @change="selectFile" /></label>
        <p v-if="selected" class="identifier">{{ selected.name }} · {{ selected.size }} 字节 · {{ fileType(selected) }}</p>
        <button type="submit" :disabled="!selected">{{ operation && error ? "显式重试原文件" : "上传到隔离区" }}</button>
      </fieldset>
      <p v-if="!mayUpload" class="hint">当前没有 Upload 字段权限；服务端仍会实时复验权限及数据范围。</p>
    </form>
    <div v-if="uploading" role="status" aria-live="polite">
      <label>上传字节 <progress :value="total === null ? undefined : sent" :max="total ?? undefined" aria-label="上传字节进度" /></label>
      <span>{{ sent }} / {{ total ?? "未知" }} 字节；{{ total !== null && sent >= total ? "等待服务器存储确认，不代表扫描通过。" : "正在传输…" }}</span>
    </div>
    <p v-else-if="busy" role="status">正在读取或处理；扫描工作量未知，不显示虚构进度。</p>
    <button v-if="busy" type="button" @click="cancel">取消等待（结果可能未知）</button>
    <StagingFileCatalog v-if="mayRead" :busy="busy" @select="selectCatalogFile" />
    <form data-test="file-lookup" @submit.prevent="readFile" aria-label="读取已知文件">
      <label>暂存文件 UUID <input v-model="lookupId" name="fileId" autocomplete="off" :disabled="busy" /></label>
      <button type="submit" :disabled="busy || !mayRead">读取当前状态</button>
    </form>
    <QueryState label="暂存文件元数据" :state="queryState" :busy="loading"
      :stale-message="error || '正在刷新；旧状态不能用于下载或提交。'" @retry="readFile">
      <template #empty><p>选择一个文件上传，或输入已有暂存文件 UUID。业务绑定文件必须回业务实体入口。</p></template>
      <article v-if="record" data-test="file-details">
        <h2>{{ record.fileName }}</h2>
        <dl>
          <dt>标识</dt><dd>{{ record.id }}</dd><dt>处理状态</dt><dd>{{ labels[record.state] }}</dd>
          <dt>类型</dt><dd>{{ record.contentType }}</dd><dt>大小</dt><dd>{{ record.byteCount }} 字节</dd>
          <dt>存储确认</dt><dd>{{ record.stored ? "已确认（不等于扫描通过）" : "未确认" }}</dd>
          <dt>创建时间 UTC</dt><dd>{{ record.createdAt }}</dd><dt>版本</dt><dd>{{ record.version }}</dd>
        </dl>
        <p v-if="record.processingProblem" role="status" class="notice">隔离超过 600 秒仍未恢复，请检查存储及扫描服务，不能绕过隔离。</p>
        <div class="file-actions">
          <button type="button" data-test="file-scan" :disabled="!usable || !mayScan || record.state !== 0 || !record.stored" @click="processFile(false)">显式扫描</button>
          <button type="button" data-test="file-download" :disabled="!usable || !mayDownload || record.state !== 1 || !record.stored" @click="downloadFile">授权下载</button>
          <button type="button" data-test="file-delete" :disabled="!usable || !mayDelete || record.state === 4" @click="confirmDelete = true">删除孤儿文件…</button>
        </div>
        <form v-if="confirmDelete" data-test="file-delete-confirm" @submit.prevent="processFile(true)" aria-label="确认删除孤儿文件">
          <p>仅创建满 24 小时且确认无引用的文件可删除；对象删除不可恢复，永久操作身份及审计保留。服务端将复验当前版本和引用。</p>
          <button type="submit" :disabled="busy">确认删除此孤儿</button>
          <button type="button" @click="confirmDelete = false">取消删除</button>
        </form>
      </article>
    </QueryState>
  </section>
</template>

<style scoped>
.files-page { min-width: 0; }
form, fieldset { min-width: 0; margin-block: 1rem; }
fieldset { border: 1px solid var(--wes-color-border-soft); border-radius: var(--wes-radius); padding: 1rem; }
label { display: flex; flex-wrap: wrap; align-items: center; gap: .75rem; margin-block: .75rem; }
input { max-width: 100%; min-width: 0; }
input[name="fileId"] { width: min(100%, 28rem); }
button { min-height: 44px; }
progress { max-width: 100%; }
dl { display: grid; grid-template-columns: minmax(5rem, .3fr) minmax(0, 1fr); gap: .75rem; }
dt { font-weight: 600; } dd { margin: 0; overflow-wrap: anywhere; }
.file-actions { display: flex; flex-wrap: wrap; gap: .75rem; }
h2, .identifier { overflow-wrap: anywhere; }
</style>
