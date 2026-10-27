<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, shallowRef, watch } from "vue";
import QueryState from "../../components/QueryState.vue";
import RealtimePager from "../../components/RealtimePager.vue";
import { ApiError, sessionState } from "../identity/identity-api";
import { CreationEventResponseError, mayReadCreationEvents, queryCreationEvents, type CreationEvent } from "./wms-creation-events";

const readable = computed(mayReadCreationEvents);
const scope = computed(() => JSON.stringify(sessionState.subject));
const rows = shallowRef<CreationEvent[]>([]);
const offset = ref(0); const limit = ref(25);
const busy = ref(false); const fresh = ref(false); const loaded = ref(false); const error = ref("");
const loadedAt = ref(""); const online = ref(navigator.onLine);
const state = computed(() => busy.value ? rows.value.length ? "stale" : "loading"
  : rows.value.length ? fresh.value ? "ready" : "stale" : error.value ? "error" : "empty");
let controller = new AbortController(); let sequence = 0;
function cancel() { ++sequence; controller.abort(); controller = new AbortController(); busy.value = false; fresh.value = false; }
function clear() { cancel(); rows.value = []; loaded.value = false; loadedAt.value = ""; error.value = ""; }
watch(scope, () => { clear(); offset.value = 0; }, { flush: "sync" });
function connection() { online.value = navigator.onLine; if (!online.value) cancel(); }
onMounted(() => { addEventListener("online", connection); addEventListener("offline", connection); });
onUnmounted(() => { cancel(); removeEventListener("online", connection); removeEventListener("offline", connection); });
async function load() {
  if (!readable.value || !online.value || busy.value) return;
  cancel(); busy.value = true; error.value = "";
  const ticket = sequence; const signal = controller.signal; const fingerprint = scope.value;
  const current = () => ticket === sequence && fingerprint === scope.value && !signal.aborted;
  try {
    const result = await queryCreationEvents(offset.value, limit.value, signal);
    if (current()) { rows.value = result; fresh.value = true; loaded.value = true; loadedAt.value = new Date().toISOString(); }
  } catch (failure) {
    if (!current()) return;
    if (failure instanceof CreationEventResponseError || failure instanceof ApiError && [401, 403, 404].includes(failure.status)) {
      rows.value = []; loaded.value = false; loadedAt.value = "";
    }
    error.value = failure instanceof Error ? failure.message : "创建事件读取未确认，请显式重试。";
  } finally { if (ticket === sequence) busy.value = false; }
}
function move(nextOffset: number, nextLimit: number) {
  if (!online.value || !readable.value || busy.value) return;
  clear(); offset.value = nextOffset; limit.value = nextLimit; void load();
}
</script>

<template>
  <section class="wms-events-page" aria-labelledby="wms-events-title">
    <h1 id="wms-events-title">消息诊断 · WMS 创建事件</h1>
    <p class="notice">仅显示当前授权 creator 范围的 WMS 创建事件元数据，不提供消息正文。
      Pending 不是已投递、业务完成或仓库启用许可；WMS 发布器、Inbox、统计与重放尚未接入，不显示虚构零值。</p>
    <p v-if="!readable" role="alert">缺少当前 WMS 消息读取的完整九字段权限，不能查询。</p>
    <p v-if="!online" class="notice" role="status">当前离线，已加载事件仅为陈旧参考；恢复连接后请手工重新读取。</p>
    <button type="button" data-test="read-wms-events" :disabled="!readable || !online || busy" @click="load">读取 WMS 创建事件</button>
    <p v-if="error && rows.length" class="error" role="alert">{{ error }}</p>
    <QueryState label="WMS 创建事件" :state="state" :busy="busy" :error-message="error"
      :stale-message="`陈旧事件，仅供参考；上次成功读取：${loadedAt || '未确认'}`" @retry="load">
      <template #empty><p>{{ loaded ? "当前授权范围没有创建事件，不表示其他 owner 无积压或 Broker 健康。" : "尚未查询；请显式读取当前授权的 WMS 创建事件。" }}</p></template>
      <ul v-if="rows.length" class="event-rows" data-test="wms-events">
        <li v-for="row in rows" :key="row.eventId">
          <h2>待发布（Pending）</h2>
          <dl>
            <dt>事件 UUID</dt><dd>{{ row.eventId }}</dd>
            <dt>Owner</dt><dd>{{ row.owner }}</dd>
            <dt>契约</dt><dd>{{ row.contractName }}</dd>
            <dt>契约版本</dt><dd>{{ row.schemaVersion }}</dd>
            <dt>资源 UUID</dt><dd>{{ row.aggregateId }}</dd>
            <dt>资源版本</dt><dd>{{ row.aggregateVersion }}</dd>
            <dt>关联 UUID</dt><dd>{{ row.correlationId }}</dd>
            <dt>发生 UTC</dt><dd><time :datetime="row.occurredAt">{{ row.occurredAt }}</time></dd>
          </dl>
        </li>
      </ul>
    </QueryState>
    <RealtimePager label="WMS 创建事件分页" :offset="offset" :limit="limit" :count="rows.length"
      :busy="busy || !online || !readable" :failed="!!error || !fresh || offset >= 10000" @change="move" />
  </section>
</template>

<style scoped>
.wms-events-page { min-width: 0; }
.event-rows { list-style: none; padding: 0; display: grid; gap: var(--wes-space-3); }
.event-rows li { border: 1px solid var(--wes-color-border-soft); border-radius: var(--wes-radius); background: var(--wes-color-surface); padding: var(--wes-space-4); min-width: 0; }
dl { display: grid; grid-template-columns: minmax(0, 1fr) minmax(0, 3fr); gap: var(--wes-space-2); }
dd { margin: 0; overflow-wrap: anywhere; }
@media (max-width: 40rem) { dl { grid-template-columns: minmax(0, 1fr); } }
</style>
