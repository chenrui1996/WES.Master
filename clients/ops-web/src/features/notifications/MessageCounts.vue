<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, shallowRef, watch } from "vue";
import QueryState from "../../components/QueryState.vue";
import { ApiError, sessionState } from "../identity/identity-api";
import { allMessageFields, messageCountFields, MessageResponseError, queryMessageSummary, type MessageSummary } from "./messages";
import { mayReadCreationEventSummary, queryCreationEventSummary, type CreationEventSummary } from "./wms-creation-events";

const props = withDefaults(defineProps<{ revision?: number; owner?: "Platform" | "Wms" }>(), { revision: 0, owner: "Platform" });
const readable = computed(() => props.owner === "Wms" ? mayReadCreationEventSummary()
  : messageCountFields.every(field => allMessageFields("read").has(field)));
const scope = computed(() => JSON.stringify(sessionState.subject));
const counts = shallowRef<MessageSummary | CreationEventSummary | null>(null);
const busy = ref(false); const fresh = ref(false); const error = ref(""); const online = ref(navigator.onLine);
const state = computed(() => busy.value ? counts.value ? "stale" : "loading"
  : counts.value ? fresh.value ? "ready" : "stale" : error.value ? "error" : "empty");
let controller = new AbortController(); let sequence = 0;
function cancel() { sequence++; controller.abort(); controller = new AbortController(); busy.value = false; fresh.value = false; }
watch([scope, () => props.owner], () => { cancel(); counts.value = null; error.value = ""; }, { flush: "sync" });
watch(() => props.revision, cancel, { flush: "sync" });
function connection() { online.value = navigator.onLine; if (!online.value) cancel(); }
onMounted(() => { addEventListener("online", connection); addEventListener("offline", connection); });
onUnmounted(() => { cancel(); removeEventListener("online", connection); removeEventListener("offline", connection); });
async function load() {
  if (!readable.value || !online.value || busy.value) return;
  cancel(); busy.value = true; error.value = "";
  const ticket = sequence; const signal = controller.signal; const fingerprint = scope.value;
  const current = () => sequence === ticket && fingerprint === scope.value && !signal.aborted;
  try {
    const result = props.owner === "Wms" ? await queryCreationEventSummary(signal) : await queryMessageSummary(signal);
    if (current()) { counts.value = result; fresh.value = true; }
  } catch (failure) {
    if (!current()) return;
    if (failure instanceof MessageResponseError || failure instanceof ApiError && [401, 403, 404].includes(failure.status)) counts.value = null;
    error.value = failure instanceof Error ? failure.message : "消息统计未确认，请显式重试。";
  } finally { if (ticket === sequence) busy.value = false; }
}
</script>

<template>
  <section class="message-counts" aria-labelledby="message-counts-title">
    <h2 id="message-counts-title">{{ owner }} owner 计数</h2>
    <p v-if="owner === 'Platform'" class="hint">覆盖当前租户完整 Platform，不受消息列表筛选影响；两次独立读取，不是统一快照或 Broker 健康检查。
      Inbox 是现存 consumer/event 收据数，不是唯一业务效果数或待消费数；保留清理后可能下降。</p>
    <p v-else class="hint">仅统计当前完整统计字段 creator 交集内的 WMS 创建事件；独立于事件列表分页。
      不与 Platform 相加，不是统一快照或 Broker 健康检查；仅计 Pending，不包含投递中、已投递或隔离数，Inbox 尚未接入，不显示虚构零值。</p>
    <p v-if="!readable" role="status">缺少完整统计字段权限；消息行读取权限不自动授予计数。</p>
    <p v-if="!online" role="status">当前离线，已读统计是陈旧参考；恢复不自动重读。</p>
    <button type="button" data-test="read-counts" :disabled="!readable || !online || busy" @click="load">读取 {{ owner }} 计数</button>
    <p v-if="error && counts" role="alert" class="error">{{ error }}</p>
    <QueryState :label="`${owner} 消息计数`" :state="state" :busy="busy" :error-message="error"
      :stale-message="`陈旧统计，不代表当前积压；上次观察结束 UTC：${counts?.observedAt ?? '未确认'}`" @retry="load">
      <template #empty><p>尚未读取，不显示零作为未查询的替代。</p></template>
      <dl v-if="counts" data-test="message-counts">
        <dt>Owner</dt><dd>{{ counts.owner }}</dd>
        <dt>观察结束 UTC</dt><dd>{{ counts.observedAt }}</dd>
        <dt>待发布</dt><dd>{{ counts.pendingCount }}</dd>
        <template v-if="counts.owner === 'Platform'">
          <dt>发布租约中</dt><dd>{{ counts.leasedCount }}</dd>
          <dt>Broker 已确认</dt><dd>{{ counts.publishedCount }}</dd>
          <dt>已隔离</dt><dd>{{ counts.quarantinedCount }}</dd>
          <dt>现存 Inbox 收据</dt><dd>{{ counts.inboxReceiptCount }}</dd>
        </template>
      </dl>
    </QueryState>
  </section>
</template>

<style scoped>
.message-counts { min-width: 0; margin-block: var(--wes-space-4); overflow-wrap: anywhere; }
dl { display: grid; grid-template-columns: auto minmax(0, 1fr); gap: var(--wes-space-2); }
dd { margin: 0; }
@media (max-width: 30rem) { dl { grid-template-columns: minmax(0, 1fr); } }
</style>
