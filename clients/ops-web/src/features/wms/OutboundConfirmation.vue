<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, shallowRef, toRaw, useId, watch } from "vue";
import { ApiError, sessionState } from "../identity/identity-api";
import { executeOutbound, mayExecuteOutbound, outboundTitles, type OutboundCommand, type OutboundReceipt } from "./outbound";

const props = defineProps<{ command: OutboundCommand; summary: string }>();
const emit = defineEmits<{ attempted: []; read: [id: string]; close: [] }>();
const id = useId();
const request = structuredClone(toRaw(props.command));
const body = JSON.stringify(request.input, null, 2);
const phase = ref<"preview" | "submitting" | "unknown" | "rejected" | "confirmed" | "invalidated">("preview");
const uncertain = ref(false); const online = ref(navigator.onLine); const error = ref("");
const receipt = shallowRef<OutboundReceipt>();
const authorized = computed(() => mayExecuteOutbound(request.kind));
const scope = computed(() => JSON.stringify(sessionState.subject));
let sequence = 0; let controller = new AbortController(); let deadline: ReturnType<typeof setTimeout> | undefined;
function cancel() {
  ++sequence; controller.abort(); controller = new AbortController();
  if (deadline !== undefined) clearTimeout(deadline);
  deadline = undefined;
}
watch([scope, () => props.command], () => {
  cancel(); phase.value = "invalidated"; receipt.value = undefined; error.value = ""; uncertain.value = false;
}, { flush: "sync" });
function connection() {
  online.value = navigator.onLine;
  if (!online.value && phase.value === "submitting") {
    cancel(); uncertain.value = true; phase.value = "unknown";
    error.value = "连接中断，结果不明；恢复后只可手工恢复原操作。";
  }
}
onMounted(() => { addEventListener("online", connection); addEventListener("offline", connection); });
onUnmounted(() => { cancel(); removeEventListener("online", connection); removeEventListener("offline", connection); });
async function submit() {
  if (!online.value || !authorized.value || !["preview", "unknown", "rejected"].includes(phase.value)
    || phase.value === "rejected" && !uncertain.value) return;
  cancel(); const ticket = sequence; const signal = controller.signal; const fingerprint = scope.value;
  const current = () => ticket === sequence && fingerprint === scope.value && !signal.aborted;
  phase.value = "submitting"; error.value = ""; emit("attempted");
  deadline = setTimeout(() => {
    if (!current()) return;
    cancel(); uncertain.value = true; phase.value = "unknown"; error.value = "10 秒内未取得出库确认，结果不明；禁止换键盲发。";
  }, 10000);
  try {
    const result = await executeOutbound(request, signal);
    if (current()) { receipt.value = result; phase.value = "confirmed"; uncertain.value = false; }
  } catch (failure) {
    if (!current()) return;
    const definite = failure instanceof ApiError && [400, 401, 403, 404, 405, 409, 422].includes(failure.status);
    if (!definite) uncertain.value = true;
    phase.value = definite ? "rejected" : "unknown";
    error.value = failure instanceof ApiError && [404, 405].includes(failure.status)
      ? "出库入口未开放或单据/资源未找到；请核对独立部署开关与当前权限。"
      : failure instanceof Error ? failure.message : "出库结果未确认，请核对原操作。";
  } finally { if (ticket === sequence && deadline !== undefined) { clearTimeout(deadline); deadline = undefined; } }
}
</script>

<template>
  <section class="outbound-confirmation" :aria-labelledby="`${id}-title`" :aria-busy="phase === 'submitting'">
    <h2 :id="`${id}-title`">{{ outboundTitles[request.kind] }} · 二次确认</h2>
    <p class="notice">拣选不扣库存；短缺不自动释放。这里的回执仅确认原操作，不是当前版本、运输完成或现场证明。
      刷新或导航将丢失内存上下文，请记录原操作 UUID 及全部输入。</p>
    <p v-if="phase === 'invalidated' || !authorized" role="alert">主体或原输入已改变，或缺少完整字段权限；请关闭并重新准备。</p>
    <template v-else>
      <p>{{ summary }}</p>
      <dl><dt>操作类型</dt><dd>{{ request.kind }}</dd><dt>原操作 UUID</dt><dd data-test="outbound-operation">{{ request.operation }}</dd>
        <template v-if="request.kind !== 'create'"><dt>原出库单 UUID</dt><dd>{{ request.id }}</dd></template>
      </dl>
      <h3>已冻结的全部原输入</h3>
      <pre data-test="outbound-preview-body">{{ body }}</pre>
      <button v-if="phase === 'preview'" type="button" data-test="confirm-outbound"
        :disabled="!online || !authorized" @click="submit">确认{{ outboundTitles[request.kind] }}</button>
      <button v-if="phase === 'unknown' || phase === 'rejected' && uncertain" type="button" data-test="retry-outbound"
        :disabled="!online || !authorized" @click="submit">手工恢复原操作</button>
      <p v-if="phase === 'submitting'" role="status">正在确认原操作，请勿重复提交。</p>
      <p v-if="phase === 'unknown'" role="status">结果不明，不代表未执行；原类型、单据、键和全部输入已冻结。</p>
      <p v-if="phase === 'rejected'" role="status">本次请求被拒绝。{{ uncertain ? "先前不明结果仍需查证，禁止换键盲发。" : "请关闭后显式读取当前详情。" }}</p>
      <p v-if="receipt" role="status">已确认原执行身份：{{ receipt.id }}。原执行版本：{{ receipt.version }}；不是当前版本或运输到达证明。</p>
      <button v-if="receipt" type="button" data-test="read-outbound-after-write" :disabled="!online || !authorized"
        @click="emit('read', receipt.id)">读取出库单当前详情</button>
    </template>
    <p v-if="!online" role="status">当前离线，禁止提交、不排队，恢复连接不会自动重试。</p>
    <p v-if="error" role="alert">{{ error }}</p>
    <button type="button" :disabled="phase === 'submitting' || uncertain" @click="emit('close')">关闭出库确认</button>
  </section>
</template>

<style scoped>
.outbound-confirmation { min-width: 0; overflow-wrap: anywhere; }
pre { white-space: pre-wrap; overflow-wrap: anywhere; font-size: .9rem; }
dl { display: grid; grid-template-columns: minmax(0, 1fr) minmax(0, 3fr); gap: var(--wes-space-2); }
dd { margin: 0; }
button { max-width: 100%; }
@media (max-width: 40rem) { dl { grid-template-columns: minmax(0, 1fr); } }
</style>
