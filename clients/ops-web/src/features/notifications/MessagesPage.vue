<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, shallowRef, useId, watch } from "vue";
import QueryState from "../../components/QueryState.vue";
import RealtimePager from "../../components/RealtimePager.vue";
import { ApiError, sessionState } from "../identity/identity-api";
import MessageReplayPanel from "./MessageReplayPanel.vue";
import MessageCounts from "./MessageCounts.vue";
import { allMessageFields, MessageResponseError, messageStatuses, queryMessages, replayableMessage, replayMessageFields, requiredMessageFields, type MessageRow } from "./messages";

const fields = computed(() => allMessageFields("read"));
const readable = computed(() => requiredMessageFields.every(field => fields.value.has(field)));
const canPrepare = computed(() => readable.value && ["ContractName", "SchemaVersion", "Attempts"].every(field => fields.value.has(field))
  && replayMessageFields.every(field => allMessageFields("replay").has(field)));
const selected = shallowRef<MessageRow | null>(null);
const emit = defineEmits<{ replayOpen: [open: boolean] }>();
watch(selected, value => emit("replayOpen", value !== null), { flush: "sync" });
const countRevision = ref(0);
const filterId = useId();
const status = ref(""); const createdFrom = ref(""); const createdBefore = ref("");
const scope = computed(() => JSON.stringify(sessionState.subject));
const rows = shallowRef<MessageRow[]>([]);
const offset = ref(0); const limit = ref(25);
const busy = ref(false); const fresh = ref(false); const loaded = ref(false); const error = ref("");
const loadedAt = ref(""); const online = ref(navigator.onLine);
const state = computed(() => busy.value ? rows.value.length ? "stale" : "loading"
  : rows.value.length ? fresh.value ? "ready" : "stale" : error.value ? "error" : "empty");
let controller = new AbortController(); let sequence = 0;
function cancel() { ++sequence; controller.abort(); controller = new AbortController(); busy.value = false; fresh.value = false; }
function clear() { cancel(); rows.value = []; loaded.value = false; loadedAt.value = ""; error.value = ""; selected.value = null; }
watch(scope, () => { clear(); offset.value = 0; }, { flush: "sync" });
watch([status, createdFrom, createdBefore], () => { clear(); offset.value = 0; }, { flush: "sync" });
function connection() { online.value = navigator.onLine; if (!online.value) cancel(); }
onMounted(() => { addEventListener("online", connection); addEventListener("offline", connection); });
onUnmounted(() => { cancel(); removeEventListener("online", connection); removeEventListener("offline", connection); });
async function load(prepareEventId?: string) {
  if (!readable.value || !online.value || busy.value || selected.value !== null) return;
  cancel(); busy.value = true; error.value = ""; selected.value = null;
  const ticket = sequence; const signal = controller.signal; const fingerprint = scope.value;
  const current = () => ticket === sequence && fingerprint === scope.value && !signal.aborted;
  try {
    const time = (value: string) => value ? `${value}${value.length === 16 ? ":00" : ""}Z` : undefined;
    const result = await queryMessages(offset.value, limit.value, signal, {
      status: status.value === "" ? undefined : Number(status.value),
      createdFrom: time(createdFrom.value), createdBefore: time(createdBefore.value),
    });
    if (current()) {
      rows.value = result; fresh.value = true; loaded.value = true; loadedAt.value = new Date().toISOString();
      if (prepareEventId) {
        const candidate = result.find(row => row.eventId === prepareEventId);
        if (canPrepare.value && candidate && replayableMessage(candidate)) selected.value = candidate;
        else error.value = "事件已移出当前页、状态或权限已改变，不能准备重放；请重新核对。";
      }
    }
  } catch (failure) {
    if (!current()) return;
    if (failure instanceof MessageResponseError || failure instanceof ApiError && [401, 403, 404].includes(failure.status)) {
      rows.value = []; loaded.value = false; loadedAt.value = "";
    }
    error.value = failure instanceof Error ? failure.message : "消息诊断未确认，请显式重试。";
  } finally { if (ticket === sequence) busy.value = false; }
}
function move(nextOffset: number, nextLimit: number) {
  if (!online.value || !readable.value || busy.value || selected.value !== null) return;
  clear(); offset.value = nextOffset; limit.value = nextLimit; void load();
}
function changed() { cancel(); rows.value = []; loaded.value = false; loadedAt.value = ""; }
function denied(message: string) { clear(); error.value = message; }
function attempted() { fresh.value = false; countRevision.value++; }
function prepare(row: MessageRow) {
  if (fresh.value && online.value && !selected.value && canPrepare.value && replayableMessage(row)) void load(row.eventId);
}
</script>

<template>
  <section class="messages-page" aria-labelledby="messages-title">
    <h1 id="messages-title">消息诊断 · 仅 Platform</h1>
    <p class="notice">仅显示当前租户 Platform Outbox 元数据，不是跨模块聚合或 Broker 健康检查。
      Broker 已确认不表示消费成功或业务完成；不提供消息正文，重放须另行受权及二次确认。</p>
    <p v-if="!readable" role="alert">缺少 Platform 消息 All 范围及必要字段读取权限，不能查询。</p>
    <p v-if="!online" class="notice" role="status">当前离线，已加载消息仅为陈旧参考；恢复连接后请手工重新读取。</p>
    <MessageCounts :revision="countRevision" />
    <form class="message-filters" @submit.prevent="load()">
      <div><label :for="`${filterId}-status`">消息状态</label>
        <select :id="`${filterId}-status`" v-model="status" name="messageStatus" :disabled="selected !== null">
          <option value="">全部授权状态</option>
          <option v-for="(label, value) in messageStatuses" :key="value" :value="String(value)">{{ label }}</option>
        </select></div>
      <div><label :for="`${filterId}-from`">创建开始（UTC，含）</label>
        <input :id="`${filterId}-from`" v-model="createdFrom" name="messageFrom" type="datetime-local" :disabled="selected !== null" /></div>
      <div><label :for="`${filterId}-before`">创建结束（UTC，不含）</label>
        <input :id="`${filterId}-before`" v-model="createdBefore" name="messageBefore" type="datetime-local" :disabled="selected !== null" /></div>
      <button type="submit" :disabled="busy || !online || !readable || selected !== null">读取 Platform 消息</button>
    </form>
    <MessageReplayPanel v-if="selected" :key="selected.eventId" :row="selected" @attempted="attempted"
      @changed="changed" @denied="denied" @close="selected = null" />
    <p v-if="error && rows.length" class="error" role="alert">{{ error }}</p>
    <QueryState label="Platform 消息诊断" :state="state" :busy="busy" :error-message="error"
      :stale-message="`陈旧消息，仅供参考；上次成功读取：${loadedAt || '未确认'}`" @retry="load()">
      <template #empty><p>{{ loaded ? "当前查询没有消息行，不表示其他 owner 无积压或 Broker 健康。" : "尚未查询；请显式读取当前授权的 Platform 消息。" }}</p></template>
      <ul v-if="rows.length" class="message-rows" data-test="messages">
        <li v-for="row in rows" :key="row.eventId">
          <h2>{{ messageStatuses[row.status] }}</h2>
          <dl>
            <dt>事件 UUID</dt><dd>{{ row.eventId }}</dd>
            <dt>Owner</dt><dd>{{ row.owner }}</dd>
            <dt>创建 UTC</dt><dd>{{ row.createdAt }}</dd>
            <template v-if="fields.has('ContractName')"><dt>契约</dt><dd>{{ row.contractName ?? "未提供" }}</dd></template>
            <template v-if="fields.has('SchemaVersion')"><dt>契约版本</dt><dd>{{ row.schemaVersion ?? "未提供" }}</dd></template>
            <template v-if="fields.has('Attempts')"><dt>尝试数</dt><dd>{{ row.attempts ?? "未提供" }}</dd></template>
            <template v-if="fields.has('NextAttemptAt')"><dt>最早重试 UTC</dt><dd>{{ row.nextAttemptAt ?? "未提供" }}</dd></template>
            <template v-if="fields.has('PublishedAt')"><dt>Broker 确认 UTC</dt><dd>{{ row.publishedAt ?? "未提供" }}</dd></template>
            <template v-if="fields.has('LastErrorCategory')"><dt>错误类别</dt><dd>{{ row.lastErrorCategory ?? "无记录" }}</dd></template>
          </dl>
          <button v-if="canPrepare && replayableMessage(row)" type="button" data-test="prepare-replay"
            :disabled="!fresh || busy || !online || selected !== null" @click="prepare(row)">重新读取并准备此事件重放</button>
        </li>
      </ul>
    </QueryState>
    <RealtimePager label="Platform 消息实时分页" :offset="offset" :limit="limit" :count="rows.length"
      :busy="busy || !online || !readable || selected !== null" :failed="!fresh || offset + limit > 10000" @change="move" />
  </section>
</template>

<style scoped>
.messages-page { min-width: 0; }
.message-filters { display: flex; flex-wrap: wrap; align-items: end; gap: var(--wes-space-3); }
.message-filters > div { display: grid; gap: var(--wes-space-2); flex: 1 1 12rem; min-width: 0; max-width: 100%; }
.message-filters input, .message-filters select { width: 100%; min-width: 0; }
.message-rows { list-style: none; padding: 0; display: grid; grid-template-columns: repeat(auto-fit, minmax(min(100%, 24rem), 1fr)); gap: var(--wes-space-3); }
.message-rows li { min-width: 0; overflow-wrap: anywhere; padding: var(--wes-space-4); background: var(--wes-color-surface); border: 1px solid var(--wes-color-border-soft); border-radius: var(--wes-radius); }
dl { display: grid; grid-template-columns: auto minmax(0, 1fr); gap: var(--wes-space-2); }
dd { margin: 0; }
@media (max-width: 30rem) { dl { grid-template-columns: minmax(0, 1fr); } dd { margin-block-end: var(--wes-space-2); } }
</style>
