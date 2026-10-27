<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, shallowRef, useId, watch } from "vue";
import QueryState from "../../components/QueryState.vue";
import RealtimePager from "../../components/RealtimePager.vue";
import StagingFileCatalog from "../platform/StagingFileCatalog.vue";
import { api, ApiError, isRecord, sessionState } from "../identity/identity-api";
import { downloadAuthorizedFile } from "../platform/file-download";
import { labels, parseStagingFile, type StagingFile } from "../platform/staging-file";
import { businessCommandError, changeBusinessFile, mayChangeBusinessFile, mayReadBusinessFiles,
  queryBusinessFiles, readBusinessFile, type BusinessFileCommand, type BusinessFileReceipt } from "./business-files";
import { validIdentity, type ResourceKind } from "./master-data";

const props = defineProps<{ kind: ResourceKind; entityId: string; active: boolean }>();
const emit = defineEmits<{ locked: [value: boolean] }>();
const heading = useId();
const rows = ref<StagingFile[]>([]); const record = shallowRef<StagingFile | null>(null);
const origin = ref<"attachment" | "staging">("attachment");
const offset = ref(0); const limit = ref(25); const state = ref("");
const busy = ref(false); const fresh = ref(false); const listFresh = ref(false); const attempted = ref(false);
const error = ref(""); const message = ref(""); const online = ref(navigator.onLine);
const phase = ref<"idle" | "preview" | "submitting" | "unknown" | "confirmed" | "rejected">("idle");
const command = shallowRef<BusinessFileCommand | null>(null); const receipt = shallowRef<BusinessFileReceipt | null>(null);
const uncertain = ref(false); const restoring = ref(false); const acknowledged = ref(false);
const manualOpen = ref(false); const manualFile = ref(""); const manualVersion = ref(""); const manualOperation = ref(""); const manualAttached = ref(true);
const scope = computed(() => JSON.stringify([sessionState.subject, props.kind, props.entityId]));
const readable = computed(() => mayReadBusinessFiles(props.kind));
const mayAttach = computed(() => mayChangeBusinessFile(props.kind, "Attach"));
const mayDetach = computed(() => mayChangeBusinessFile(props.kind, "Detach"));
const mayDownload = computed(() => sessionState.subject?.grants.some(grant =>
  grant.permission === "platform.files.download" && grant.fields.includes("Content")) ?? false);
const locked = computed(() => ["preview", "submitting", "unknown"].includes(phase.value) || uncertain.value);
const available = computed(() => props.active && online.value && readable.value);
const canRead = computed(() => available.value && !locked.value && !busy.value);
const canConfirm = computed(() => online.value && readable.value && !!command.value
  && (command.value.attached ? mayAttach.value : mayDetach.value)
  && (phase.value === "unknown" || props.active && (restoring.value || fresh.value)));
const queryState = computed(() => busy.value ? rows.value.length ? "stale" : "loading"
  : rows.value.length ? listFresh.value ? "ready" : "stale" : error.value ? "error" : "empty");
let controller = new AbortController(); let sequence = 0;
let deadline: ReturnType<typeof setTimeout> | undefined;
function cancel() {
  ++sequence; controller.abort(); controller = new AbortController(); clearTimeout(deadline); busy.value = false;
}
function clearFileData() { rows.value = []; record.value = null; fresh.value = false; listFresh.value = false; }
function clearOperation() {
  command.value = null; receipt.value = null; phase.value = "idle"; uncertain.value = false; restoring.value = false; acknowledged.value = false;
}
function reset() {
  cancel(); clearFileData(); clearOperation(); offset.value = 0; attempted.value = false;
  manualOpen.value = false; manualFile.value = ""; manualVersion.value = ""; manualOperation.value = "";
  error.value = ""; message.value = "";
}
watch(scope, reset, { flush: "sync" });
watch(locked, value => emit("locked", value), { immediate: true, flush: "sync" });
watch([offset, limit, state], () => {
  if (phase.value === "submitting") stopWaiting(); else cancel();
  clearFileData(); attempted.value = false;
}, { flush: "sync" });
watch(state, () => { offset.value = 0; }, { flush: "sync" });
function stopWaiting() {
  const submitting = phase.value === "submitting";
  cancel(); fresh.value = false; listFresh.value = false;
  if (submitting) { phase.value = "unknown"; uncertain.value = true; }
  error.value = submitting ? "已停止等待；原操作可能已提交，只能显式恢复原键。取消不是回滚。" : "读取未确认，旧数据仅供陈旧参考，请显式重读。";
}
watch(() => props.active, active => { if (!active) stopWaiting(); }, { flush: "sync" });
function connection() { online.value = navigator.onLine; if (!online.value) stopWaiting(); }
onMounted(() => { addEventListener("online", connection); addEventListener("offline", connection); });
onUnmounted(() => { cancel(); emit("locked", false); removeEventListener("online", connection); removeEventListener("offline", connection); });
function begin() {
  cancel(); busy.value = true; error.value = ""; message.value = "";
  const ticket = sequence; const fingerprint = scope.value; const signal = controller.signal;
  return { ticket, signal, current: () => ticket === sequence && fingerprint === scope.value && !signal.aborted };
}
function failed(failure: unknown) {
  fresh.value = false; listFresh.value = false;
  if (failure instanceof ApiError && [401, 403].includes(failure.status)) clearFileData();
  error.value = failure instanceof Error ? failure.message : "附件读取未确认，请显式重试。";
}
async function readList() {
  if (!canRead.value) return;
  clearOperation(); record.value = null; fresh.value = false; listFresh.value = false; attempted.value = true;
  const run = begin();
  try {
    const result = await queryBusinessFiles(props.kind, props.entityId, run.signal, offset.value, limit.value,
      state.value === "" ? undefined : Number(state.value));
    if (run.current()) { rows.value = result.rows; listFresh.value = true; }
  } catch (failure) { if (run.current()) failed(failure); }
  finally { if (run.ticket === sequence) busy.value = false; }
}
function page(start: number, size: number) {
  if (!canRead.value) return;
  if (start > 10000) { listFresh.value = false; error.value = "已达偏移上限，请从第一页重新读取。"; return; }
  offset.value = start; limit.value = size; void readList();
}
async function select(id: string, staging = false) {
  if (!canRead.value || staging && !mayAttach.value || !staging && !listFresh.value) return;
  clearOperation(); record.value = null; fresh.value = false;
  const run = begin();
  try {
    let value: StagingFile;
    if (staging) {
      if (!validIdentity(id)) throw new Error("暂存文件身份无效。");
      const result = await api(`/api/platform/files/staging/${id}`, { signal: run.signal });
      if (!isRecord(result) || result.decision !== 0) throw new Error("暂存元数据读取无效。");
      value = parseStagingFile(result.file);
      if (value.id.toLowerCase() !== id.toLowerCase() || !validIdentity(value.version)) throw new Error("暂存文件身份或版本不符。");
    } else value = await readBusinessFile(props.kind, props.entityId, id, run.signal);
    if (run.current()) { record.value = value; origin.value = staging ? "staging" : "attachment"; fresh.value = true; }
  } catch (failure) { if (run.current()) failed(failure); }
  finally { if (run.ticket === sequence) busy.value = false; }
}
function prepare() {
  const file = record.value; const attached = origin.value === "staging";
  if (!canRead.value || !fresh.value || !file || !(attached ? mayAttach.value && file.state === 1 && file.stored : mayDetach.value)) return;
  command.value = Object.freeze({ kind: props.kind, entityId: props.entityId, fileId: file.id,
    expectedVersion: file.version, operation: crypto.randomUUID(), attached });
  receipt.value = null; restoring.value = false; phase.value = "preview"; error.value = ""; acknowledged.value = false;
}
function recoverPreview() {
  if (!canRead.value || !(manualAttached.value ? mayAttach.value : mayDetach.value)) return;
  const original: BusinessFileCommand = { kind: props.kind, entityId: props.entityId, fileId: manualFile.value,
    expectedVersion: manualVersion.value, operation: manualOperation.value, attached: manualAttached.value };
  error.value = businessCommandError(original);
  if (error.value) return;
  command.value = Object.freeze(original); receipt.value = null; restoring.value = true; phase.value = "preview"; acknowledged.value = false;
}
function cancelPreview() {
  if (phase.value !== "preview") return;
  const wasRestoring = restoring.value; clearOperation();
  message.value = wasRestoring ? "只取消本次恢复预览；原操作是否提交仍需查证。" : "已取消预览；未发送绑定或解绑请求。";
}
async function submit() {
  const original = command.value;
  if (!original || !canConfirm.value || !["preview", "unknown"].includes(phase.value)) return;
  const wasUncertain = uncertain.value || restoring.value;
  const run = begin(); phase.value = "submitting"; fresh.value = false; listFresh.value = false;
  deadline = setTimeout(stopWaiting, 10000);
  try {
    const result = await changeBusinessFile(original, run.signal);
    if (!run.current()) return;
    receipt.value = result; phase.value = "confirmed"; uncertain.value = false; clearFileData(); manualOpen.value = false;
    message.value = "原操作已确认；回执不是当前成员关系，请显式重读当前附件目录。";
  } catch (failure) {
    if (!run.current()) return;
    const rejected = !wasUncertain && failure instanceof ApiError && [400, 401, 403, 404, 405, 409, 412, 422, 428].includes(failure.status);
    phase.value = rejected ? "rejected" : "unknown"; uncertain.value = !rejected;
    if (failure instanceof ApiError && [401, 403].includes(failure.status)) clearFileData();
    error.value = `${failure instanceof Error ? failure.message : "请求未确认。"}${rejected ? " 本次操作被拒绝，请重读后处理。" : " 原操作结果仍不明；不得换键或更新原版本。仅可显式恢复原操作。"}`;
  } finally { if (run.ticket === sequence) { clearTimeout(deadline); busy.value = false; } }
}
function exitUnknown() {
  if (phase.value !== "unknown" || !acknowledged.value) return;
  cancel(); clearOperation(); clearFileData(); manualOpen.value = false;
  message.value = "已退出本地恢复等待；这不会撤销或证明服务端原操作未提交。请凭记录核验。";
}
async function download() {
  const file = record.value;
  if (!canRead.value || !fresh.value || !file || origin.value !== "attachment" || file.state !== 1 || !file.stored || !mayDownload.value) return;
  const run = begin();
  try {
    if (await downloadAuthorizedFile(`/api/wms/${props.kind}/${props.entityId}/files/${file.id}/content`, file, run.signal,
      () => run.current() && available.value && record.value?.id === file.id && record.value.version === file.version))
      message.value = "已发起受权下载；请在浏览器中确认接收结果。";
  } catch (failure) { if (run.current()) failed(failure); }
  finally { if (run.ticket === sequence) busy.value = false; }
}
function stateLabel(file: StagingFile) { return file.state === 1 ? "可用（不代表当前扫描服务健康）" : labels[file.state]; }
</script>

<template>
  <section class="entity-attachments" :aria-labelledby="heading" data-test="entity-attachments" :aria-busy="busy">
    <h2 :id="heading">当前实体附件</h2>
    <p class="identifier">{{ kind === "warehouses" ? "仓库" : "库位" }} · {{ entityId }}</p>
    <p class="hint">实时目录不是固定快照；元数据、下载、绑定和解绑分别授权。回执不代表当前成员关系。离开页面不会撤销已提交操作。</p>
    <p v-if="!available" role="status" class="notice">当前上下文未确认或离线，已加载数据仅作陈旧参考；请在线重新读取实体，不自动重放。</p>
    <p v-if="error" role="alert" class="error">{{ error }}</p>
    <p v-if="message" role="status" class="notice">{{ message }}</p>
    <div class="actions">
      <label>附件处理状态<select v-model="state" name="attachmentState" :disabled="!canRead">
        <option value="">全部状态</option><option v-for="(_, index) in labels" :key="index" :value="String(index)">{{ index === 1 ? "可用" : labels[index] }}</option>
      </select></label>
      <button type="button" data-test="attachments-read" :disabled="!canRead" @click="readList">读取当前附件目录</button>
      <button v-if="busy" type="button" data-test="attachment-cancel-wait" @click="stopWaiting">停止等待（不是回滚）</button>
    </div>
    <QueryState label="当前实体附件目录" :state="queryState" :busy="busy" :error-message="error"
      :stale-message="`旧目录仅供参考，不可选择：${error || '当前读取未确认'}`" @retry="readList">
      <template #empty><p>{{ attempted ? "当前条件及授权下没有附件；不代表其他实体或全系统没有文件。" : "请显式读取当前实体附件目录。" }}</p></template>
      <ul class="attachment-list"><li v-for="file in rows" :key="file.id">
        <h3>{{ file.fileName }}</h3><p>{{ stateLabel(file) }} · {{ file.byteCount }} 字节 · {{ file.contentType }}</p>
        <p class="identifier">{{ file.id }} · UTC {{ file.createdAt }}</p>
        <p v-if="file.processingProblem" role="status">隔离处理超过 600 秒，不可绕过扫描。</p>
        <button type="button" data-test="attachment-select" :disabled="!canRead || !listFresh" @click="select(file.id)">选择并重新读取 {{ file.fileName }}</button>
      </li></ul>
    </QueryState>
    <RealtimePager label="实体附件实时分页" :offset="offset" :limit="limit" :count="rows.length" :busy="!canRead" :failed="!listFresh" @change="page" />
    <article v-if="record" data-test="attachment-details">
      <h3>{{ origin === "attachment" ? "当前实体附件元数据" : "重新读取的暂存候选（尚未绑定）" }}</h3>
      <p v-if="!fresh" role="status">陈旧元数据，不可创建新命令或下载。</p>
      <dl><div><dt>文件名</dt><dd>{{ record.fileName }}</dd></div><div><dt>文件 UUID</dt><dd>{{ record.id }}</dd></div>
        <div><dt>状态</dt><dd>{{ stateLabel(record) }}</dd></div><div><dt>类型 / 字节</dt><dd>{{ record.contentType }} / {{ record.byteCount }}</dd></div>
        <div><dt>隔离对象已存储</dt><dd>{{ record.stored ? "是" : "否" }}</dd></div><div><dt>版本</dt><dd>{{ record.version }}</dd></div>
        <div><dt>创建时间 UTC</dt><dd>{{ record.createdAt }}</dd></div><div><dt>处理超时</dt><dd>{{ record.processingProblem ? "是，不能绕过扫描" : "否（不代表扫描服务健康）" }}</dd></div></dl>
      <button v-if="origin === 'attachment' && mayDownload" type="button" data-test="attachment-download"
        :disabled="!canRead || !fresh || record.state !== 1 || !record.stored" @click="download">下载当前附件</button>
      <button v-if="origin === 'staging' ? mayAttach : mayDetach" type="button" data-test="attachment-prepare"
        :disabled="!canRead || !fresh || origin === 'staging' && (record.state !== 1 || !record.stored)" @click="prepare">
        {{ origin === "staging" ? "预览绑定到当前实体" : "预览从当前实体解绑" }}</button>
    </article>
    <template v-if="mayAttach">
      <p class="hint">绑定只接受可用且已存储的暂存候选；上传、扫描仍在暂存文件工作区完成，不自动扫描或绑定。</p>
      <StagingFileCatalog :busy="!canRead" @select="id => select(id, true)" />
    </template>
    <details v-if="(mayAttach || mayDetach) && !locked" :open="manualOpen" data-test="attachment-recovery">
      <summary @click.prevent="manualOpen = !manualOpen">凭原记录恢复操作（不是新建命令）</summary>
      <form v-if="manualOpen" data-test="attachment-recovery-form" @submit.prevent="recoverPreview">
        <fieldset :disabled="!canRead"><legend>继承上述当前实体，输入原操作完整记录</legend>
          <label>原文件 UUID<input v-model="manualFile" name="originalFile" autocomplete="off" spellcheck="false" maxlength="36" /></label>
          <label>原 expectedVersion<input v-model="manualVersion" name="originalVersion" autocomplete="off" spellcheck="false" maxlength="36" /></label>
          <label>原 Idempotency-Key<input v-model="manualOperation" name="originalOperation" autocomplete="off" spellcheck="false" maxlength="36" /></label>
          <label>原动作<select v-model="manualAttached" name="originalAttached"><option :value="true">绑定</option><option :value="false">解绑</option></select></label>
          <button type="submit" :disabled="manualAttached ? !mayAttach : !mayDetach">预览恢复原操作</button>
        </fieldset>
      </form>
    </details>
    <section v-if="command" data-test="attachment-command" :aria-labelledby="`${heading}-command`">
      <h3 :id="`${heading}-command`">{{ phase === "confirmed" ? "原操作已确认" : phase === "preview" ? "二次确认原操作" : phase === "unknown" ? "原操作结果不明" : phase === "submitting" ? "正在等待原操作" : "本次操作被拒绝" }}</h3>
      <dl><div><dt>原租户 / 操作者</dt><dd>{{ sessionState.subject?.tenantId }} / {{ sessionState.subject?.userId }}</dd></div>
        <div><dt>固定类型 / 实体</dt><dd>{{ command.kind }} / {{ command.entityId }}</dd></div><div><dt>原文件 UUID</dt><dd>{{ command.fileId }}</dd></div>
        <div><dt>动作</dt><dd>{{ command.attached ? "绑定" : "解绑" }}</dd></div><div><dt>原 expectedVersion</dt><dd>{{ command.expectedVersion }}</dd></div>
        <div><dt>原 Idempotency-Key</dt><dd>{{ command.operation }}</dd></div></dl>
      <p class="hint">请记录完整原元组及当前操作者；不写入本机缓存。恢复不换键、版本或目标，不以当前数据重建未知请求。</p>
      <div v-if="phase === 'preview'" class="actions">
        <button type="button" data-test="attachment-confirm" :disabled="!canConfirm || busy" @click="submit">确认{{ restoring ? "恢复原操作" : command.attached ? "绑定" : "解绑" }}</button>
        <button type="button" data-test="attachment-cancel-preview" @click="cancelPreview">取消预览</button>
      </div>
      <template v-if="phase === 'unknown'">
        <button type="button" data-test="attachment-retry" :disabled="!canConfirm || busy" @click="submit">显式恢复原操作</button>
        <label class="acknowledge"><input v-model="acknowledged" type="checkbox" name="recordedOriginal" />我已记录完整原元组，理解退出不是回滚或未提交证明</label>
        <button type="button" data-test="attachment-exit" :disabled="!acknowledged" @click="exitUnknown">记录后退出本地等待</button>
      </template>
      <p v-if="receipt" data-test="attachment-receipt" role="status">原回执：{{ receipt.id }} / {{ receipt.version }} / {{ receipt.attached ? "已绑定事实" : "已解绑事实" }}；请显式重读当前目录。</p>
    </section>
  </section>
</template>

<style scoped>
.entity-attachments { min-width: 0; margin-block: 1.5rem; border-top: 1px solid var(--wes-color-border-soft); padding-block-start: 1rem; }
.actions { display: flex; flex-wrap: wrap; align-items: end; gap: .75rem; }
label { display: grid; gap: .375rem; margin-block: .5rem; }
fieldset, article, li, details, dl, dd, section { min-width: 0; }
.attachment-list { list-style: none; padding: 0; display: grid; gap: .75rem; }
li, article, [data-test="attachment-command"] { border: 1px solid var(--wes-color-border-soft); border-radius: var(--wes-radius); padding: 1rem; margin-block: 1rem; }
dl > div { display: grid; gap: .25rem; margin-block: .75rem; }
dd { margin: 0; }
h2, h3, p, dd, summary, label, button { overflow-wrap: anywhere; }
button, select, input, summary { min-height: 2.75rem; max-width: 100%; }
input { width: 100%; box-sizing: border-box; }
.acknowledge { display: flex; align-items: center; gap: .75rem; }
.acknowledge input { width: 1.5rem; flex-shrink: 0; }
</style>
