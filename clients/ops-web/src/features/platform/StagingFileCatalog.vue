<script setup lang="ts">
import { computed, onUnmounted, ref, watch } from "vue";
import QueryState from "../../components/QueryState.vue";
import RealtimePager from "../../components/RealtimePager.vue";
import { api, ApiError, isRecord, sessionState } from "../identity/identity-api";
import { labels, parseStagingFile, type StagingFile } from "./staging-file";

const props = defineProps<{ busy: boolean }>();
const emit = defineEmits<{ select: [id: string] }>();
const rows = ref<StagingFile[]>([]);
const offset = ref(0); const limit = ref(25); const state = ref("");
const loading = ref(false); const fresh = ref(false); const attempted = ref(false); const error = ref("");
let controller = new AbortController(); let generation = 0;
const fingerprint = computed(() => JSON.stringify([sessionState.subject, offset.value, limit.value, state.value]));
function cancel() { ++generation; controller.abort(); controller = new AbortController(); loading.value = false; }
watch(state, () => { offset.value = 0; }, { flush: "sync" });
watch(fingerprint, () => {
  cancel(); rows.value = []; fresh.value = false; attempted.value = false; error.value = "";
}, { flush: "sync" });
onUnmounted(cancel);
const queryState = computed(() => loading.value ? rows.value.length ? "stale" : "loading"
  : error.value ? rows.value.length ? "stale" : "error" : rows.value.length ? "ready" : "empty");
async function read() {
  if (props.busy || loading.value) return;
  const ticket = ++generation; const identity = fingerprint.value; const signal = controller.signal;
  const start = offset.value; const size = limit.value; const filter = state.value;
  const current = () => ticket === generation && identity === fingerprint.value && !signal.aborted;
  loading.value = true; fresh.value = false; attempted.value = true; error.value = "";
  try {
    const value: unknown = await api(`/api/platform/files/staging?offset=${start}&limit=${size}${filter ? `&state=${filter}` : ""}`, { signal });
    if (!current()) return;
    if (!isRecord(value) || value.decision !== 0 || value.offset !== start || value.limit !== size
      || !Array.isArray(value.rows) || value.rows.length > size) throw new Error("文件目录响应无效，已拒绝选择。");
    const files = value.rows.map(parseStagingFile);
    if (new Set(files.map(file => file.id.toLowerCase())).size !== files.length
      || files.some(file => filter !== "" && file.state !== Number(filter)))
      throw new Error("文件目录响应无效，已拒绝选择。");
    rows.value = files; fresh.value = true;
  } catch (failure) {
    if (!current()) return;
    if (failure instanceof ApiError && [401, 403].includes(failure.status)) rows.value = [];
    error.value = failure instanceof Error ? failure.message : "文件目录未确认，请明确重试。";
  } finally { if (ticket === generation) loading.value = false; }
}
function page(start: number, size: number) {
  if (start > 10000) { error.value = "实时查询已达偏移上限，请重新从第一页读取。"; fresh.value = false; return; }
  offset.value = start; limit.value = size; void read();
}
function select(id: string) {
  if (fresh.value && !loading.value && !props.busy) emit("select", id);
}
</script>

<template>
  <section data-test="file-catalog" aria-labelledby="file-catalog-heading" class="file-catalog">
    <h2 id="file-catalog-heading">当前授权的暂存目录</h2>
    <p class="hint">仅未绑定文件；选择后重新读取当前权限与版本。实时目录不是固定快照。</p>
    <div class="actions">
      <label>目录处理状态
        <select v-model="state" name="catalogState" :disabled="busy">
          <option value="">全部状态</option>
          <option v-for="(label, index) in labels" :key="index" :value="String(index)">{{ label }}</option>
        </select>
      </label>
      <button type="button" data-test="file-catalog-read" :disabled="busy || loading" @click="read">读取暂存目录</button>
    </div>
    <QueryState label="暂存文件目录" :state="queryState" :busy="loading" :error-message="error"
      :stale-message="loading ? '正在刷新，旧目录不可选择。' : `刷新失败，旧目录不可选择：${error}`" @retry="read">
      <template #empty><p>{{ attempted ? "当前授权及条件下没有暂存文件。" : "请显式读取当前暂存目录。" }}</p></template>
      <ul class="file-list">
        <li v-for="file in rows" :key="file.id">
          <h3>{{ file.fileName }}</h3>
          <p>{{ labels[file.state] }} · {{ file.byteCount }} 字节 · {{ file.contentType }}</p>
          <p class="identifier">{{ file.id }} · UTC {{ file.createdAt }}</p>
          <p v-if="file.processingProblem" role="status">隔离处理超过 600 秒，不能绕过扫描。</p>
          <button type="button" data-test="file-catalog-select" :aria-label="`读取文件 ${file.fileName}`"
            :disabled="busy || loading || !fresh" @click="select(file.id)">选择并重新读取</button>
        </li>
      </ul>
    </QueryState>
    <RealtimePager label="暂存文件目录分页" :offset="offset" :limit="limit" :count="rows.length"
      :busy="busy || loading" :failed="!fresh" @change="page" />
  </section>
</template>

<style scoped>
.file-catalog { min-width: 0; margin-block: 1.5rem; }
.actions, label { display: flex; flex-wrap: wrap; align-items: center; gap: .75rem; }
.file-list { list-style: none; padding: 0; display: grid; gap: .75rem; }
li { border: 1px solid var(--wes-color-border-soft); padding: 1rem; border-radius: var(--wes-radius); min-width: 0; }
h3, p { overflow-wrap: anywhere; }
button, select { min-height: 2.75rem; }
</style>
