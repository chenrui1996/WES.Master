<script setup lang="ts">
import { computed, onUnmounted, ref, watch } from "vue";
import { apiBlob, ApiError, sessionState } from "../identity/identity-api";

const props = defineProps<{ snapshotId: string; expiresAt: string; ready: boolean }>();
const emit = defineEmits<{ invalidated: [message: string] }>();
const columns = [
  { key: "Id", label: "标识", required: true }, { key: "OccurredAt", label: "发生时间（UTC）", required: true },
  { key: "ActorId", label: "操作者" }, { key: "TargetId", label: "目标" },
  { key: "CorrelationId", label: "关联标识" }, { key: "Action", label: "动作码" },
  { key: "BeforeVersion", label: "原版本" }, { key: "AfterVersion", label: "后继版本" },
];
function permitted(permission: string) {
  return new Set(sessionState.subject?.grants.filter(grant => grant.permission === permission && [0, 2].includes(grant.scope))
    .flatMap(grant => grant.fields) ?? []);
}
const allowed = computed(() => columns.filter(column =>
  permitted("platform.audit.read").has(column.key) && permitted("platform.audit.export").has(column.key)));
const authorized = computed(() => ["Id", "OccurredAt"].every(key => allowed.value.some(column => column.key === key)));
const selected = ref(["Id", "OccurredAt"]);
const busy = ref(false); const expired = ref(false);
const error = ref(""); const message = ref("");
let operation: string | null = null; let controller = new AbortController(); let generation = 0;
let expiryTimer: ReturnType<typeof setTimeout> | undefined;
const fingerprint = computed(() => JSON.stringify([props.snapshotId, props.expiresAt, sessionState.subject,
  columns.filter(column => selected.value.includes(column.key)).map(column => column.key)]));
function cancel() {
  generation++; controller.abort(); controller = new AbortController(); busy.value = false;
}
watch(fingerprint, () => { cancel(); operation = null; error.value = ""; message.value = ""; }, { flush: "sync" });
watch(() => props.ready, ready => { if (!ready) cancel(); }, { flush: "sync" });
watch(() => props.expiresAt, value => {
  if (expiryTimer) clearTimeout(expiryTimer);
  const remaining = Date.parse(value) - Date.now();
  expired.value = !Number.isFinite(remaining) || remaining <= 0;
  if (!expired.value) expiryTimer = setTimeout(() => { expired.value = true; cancel(); }, Math.min(remaining, 600000));
}, { immediate: true, flush: "sync" });
onUnmounted(() => { cancel(); if (expiryTimer) clearTimeout(expiryTimer); });
async function submit() {
  if (!authorized.value || !props.ready || busy.value || expired.value) return;
  const selection = columns.filter(column => selected.value.includes(column.key)).map(column => column.key);
  if (!selection.every(key => allowed.value.some(column => column.key === key))) {
    error.value = "所选列已失去授权，请重新创建快照。"; return;
  }
  operation ??= crypto.randomUUID();
  const request = ++generation; const identity = fingerprint.value; const signal = controller.signal;
  const snapshot = props.snapshotId;
  busy.value = true; error.value = ""; message.value = "";
  try {
    const bytes = await apiBlob(`/api/platform/audit/snapshots/${encodeURIComponent(snapshot)}/export`, {
      method: "POST", headers: { "Idempotency-Key": operation }, body: JSON.stringify({ columns: selection }), signal,
    });
    if (request !== generation || identity !== fingerprint.value || !props.ready || signal.aborted || expired.value) return;
    if (bytes.type.split(";")[0] !== "text/csv" || bytes.size < 1 || bytes.size > 4194304)
      throw new Error("导出响应无效，未创建下载；请重新读取快照。");
    const url = URL.createObjectURL(bytes);
    try {
      const link = document.createElement("a");
      link.href = url; link.download = `audit-${snapshot}.csv`;
      document.body.append(link);
      try { link.click(); } finally { link.remove(); }
    } finally { URL.revokeObjectURL(url); }
    message.value = "导出审计已提交并请求浏览器下载，不代表客户端已收齐；再次明确下载复用原操作身份。";
  } catch (failure) {
    if (request !== generation || identity !== fingerprint.value || signal.aborted) return;
    error.value = failure instanceof Error ? failure.message : "导出结果未确认，请明确重试原快照和列。";
    if (failure instanceof ApiError && [401, 403, 410].includes(failure.status)) emit("invalidated", error.value);
  } finally { if (request === generation) busy.value = false; }
}
</script>

<template>
  <form v-if="authorized" aria-label="导出固定审计快照" class="audit-export" @submit.prevent="submit">
    <fieldset :disabled="busy || !ready || expired">
      <legend>显式选择导出列（固定快照全部成员）</legend>
      <label v-for="column in allowed" :key="column.key">
        <input v-model="selected" name="audit-export-column" type="checkbox" :value="column.key" :disabled="column.required">
        {{ column.label }}{{ column.required ? "（必选）" : "" }}
      </label>
      <button type="submit" data-test="export-audit">导出所选列 CSV</button>
    </fieldset>
    <p v-if="busy" role="status">正在重新核验权限并提交导出审计；结果未确认前不会交付文件。</p>
    <p v-if="expired" role="status">快照已过期，请重新创建；不能延长原快照。</p>
    <p v-if="error" role="alert">{{ error }}</p>
    <p v-if="message" role="status">{{ message }}</p>
  </form>
</template>

<style scoped>
.audit-export { margin-block: 1rem; }
fieldset { display: flex; flex-wrap: wrap; align-items: center; gap: 0.75rem; min-width: 0; }
label { display: inline-flex; align-items: center; min-height: 2.75rem; gap: 0.4rem; }
input { width: auto; min-width: 1rem; }
</style>
