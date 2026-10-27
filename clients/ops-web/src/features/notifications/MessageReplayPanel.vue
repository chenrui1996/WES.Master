<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, shallowRef, useId, watch } from "vue";
import { ApiError, sessionState } from "../identity/identity-api";
import { allMessageFields, replayableMessage, replayMessage, replayMessageFields, type MessageReplayRequest, type MessageRow } from "./messages";

const props = defineProps<{ row: MessageRow }>();
const emit = defineEmits<{ attempted: []; changed: []; denied: [message: string]; close: [] }>();
const reasonId = useId();
const phase = ref<"draft" | "preview" | "submitting" | "unknown" | "confirmed" | "rejected" | "invalidated">("draft");
const reason = ref(""); const error = ref(""); const online = ref(navigator.onLine);
const command = shallowRef<MessageReplayRequest | null>(null);
const uncertain = ref(false);
const authorized = computed(() => replayMessageFields.every(field => allMessageFields("replay").has(field)));
const fingerprint = computed(() => JSON.stringify([sessionState.subject, props.row]));
let controller = new AbortController(); let sequence = 0;
function cancel() { sequence++; controller.abort(); controller = new AbortController(); }
watch(fingerprint, () => {
  cancel(); command.value = null; reason.value = ""; error.value = ""; phase.value = "invalidated";
}, { flush: "sync" });
function connection() {
  online.value = navigator.onLine;
  if (!online.value && phase.value === "submitting") {
    cancel(); uncertain.value = true; phase.value = "unknown";
    error.value = "连接中断，结果不明；恢复后仅可显式重试原操作。";
  }
}
onMounted(() => { addEventListener("online", connection); addEventListener("offline", connection); });
onUnmounted(() => { cancel(); removeEventListener("online", connection); removeEventListener("offline", connection); });
function preview() {
  if (phase.value !== "draft" || !authorized.value || !online.value || !replayableMessage(props.row)) return;
  const text = reason.value.trim();
  if (text.length < 3 || text.length > 256 || /[\u0000-\u001f\u007f-\u009f]/.test(text)) {
    error.value = "理由须为 3 至 256 个可打印字符。"; return;
  }
  if (props.row.attempts === null) return;
  command.value = { eventId: props.row.eventId, expectedAttempts: props.row.attempts, reason: text, operation: crypto.randomUUID() };
  error.value = ""; phase.value = "preview";
}
async function submit() {
  if (!authorized.value || !online.value || !command.value || !["preview", "unknown"].includes(phase.value)) return;
  cancel(); const ticket = sequence; const signal = controller.signal; const scope = fingerprint.value;
  const request = command.value;
  phase.value = "submitting"; error.value = "";
  emit("attempted");
  const current = () => sequence === ticket && scope === fingerprint.value && !signal.aborted;
  try {
    await replayMessage(request, signal);
    if (current()) { phase.value = "confirmed"; emit("changed"); }
  } catch (failure) {
    if (!current()) return;
    const definite = failure instanceof ApiError && [400, 401, 403, 404, 409].includes(failure.status);
    if (!definite) uncertain.value = true;
    phase.value = definite ? "rejected" : "unknown";
    error.value = failure instanceof Error ? failure.message : "请求结果未确认，请核对原操作。";
    if (failure instanceof ApiError && [401, 403].includes(failure.status)) emit("denied", error.value);
  }
}
</script>

<template>
  <section class="replay-panel" aria-labelledby="replay-title" :aria-busy="phase === 'submitting'">
    <h2 id="replay-title">Platform 审计消息重放预览</h2>
    <p class="notice">只重置原事件为待发布，不修改消息正文，不代表投递或业务完成。
      未确认时不可换新键盲目再发；导航、关闭或刷新会丢失内存上下文，请先记录操作 UUID。</p>
    <p v-if="!online" role="status">当前离线，禁止提交；不排队，恢复后不会自动重试。</p>
    <p v-if="phase === 'invalidated' || !authorized" role="alert">当前主体、权限或事件已改变，请关闭并重新读取后准备。</p>
    <form v-else-if="phase === 'draft'" @submit.prevent="preview">
      <label :for="reasonId">重放理由</label>
      <textarea :id="reasonId" v-model="reason" maxlength="256" :disabled="!online" />
      <button type="submit" :disabled="!online">预览原事件重放</button>
    </form>
    <div v-if="command" data-test="replay-preview">
      <dl>
        <dt>原事件 UUID</dt><dd>{{ command.eventId }}</dd>
        <dt>Expected attempts</dt><dd>{{ command.expectedAttempts }}</dd>
        <dt>理由</dt><dd>{{ command.reason }}</dd>
        <dt>操作 UUID</dt><dd>{{ command.operation }}</dd>
      </dl>
      <button v-if="phase === 'preview'" type="button" data-test="confirm-replay" :disabled="!online || !authorized" @click="submit">确认提交此原事件</button>
      <button v-if="phase === 'unknown'" type="button" data-test="retry-replay" :disabled="!online || !authorized" @click="submit">仅重试此原操作</button>
    </div>
    <p v-if="phase === 'submitting'" role="status">提交中，请勿关闭；取消网络请求不等于业务回滚。</p>
    <p v-if="phase === 'unknown'" role="alert">结果不明：请求可能已提交，内容与操作键已冻结。</p>
    <p v-if="phase === 'rejected'" role="alert">本次请求被拒绝，请重新读取。{{ uncertain ? "此前不明结果仍需查证，不能据此认定先前未提交。" : "" }}</p>
    <p v-if="phase === 'confirmed'" role="status">已提交或已识别原操作；请显式重读消息状态，不代表投递或业务完成。</p>
    <p v-if="error" class="error" role="alert">{{ error }}</p>
    <button type="button" :disabled="phase === 'submitting'" @click="emit('close')">{{ phase === "unknown" ? "已记录操作，关闭不明结果" : "关闭重放面板" }}</button>
  </section>
</template>

<style scoped>
.replay-panel { min-width: 0; overflow-wrap: anywhere; padding: var(--wes-space-4); border: 1px solid var(--wes-color-border-soft); border-radius: var(--wes-radius); margin-block: var(--wes-space-4); }
form { display: grid; gap: var(--wes-space-2); }
textarea { width: 100%; min-width: 0; min-height: 6rem; }
dl { display: grid; grid-template-columns: auto minmax(0, 1fr); gap: var(--wes-space-2); }
dd { margin: 0; white-space: pre-wrap; }
button { max-width: 100%; white-space: normal; }
@media (max-width: 30rem) { dl { grid-template-columns: minmax(0, 1fr); } }
</style>
