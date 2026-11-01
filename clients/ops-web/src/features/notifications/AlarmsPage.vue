<script setup lang="ts">
import { computed, shallowRef, watch } from "vue";
import QueryState from "../../components/QueryState.vue";
import RealtimePager from "../../components/RealtimePager.vue";
import { sessionState } from "../identity/identity-api";
import NotificationActionPanel from "./NotificationActionPanel.vue";
import { alarmActionFields, alarmActionPermission, alarmReadFields, hasNotificationFields, manualAlarmFields,
  queryAlarm, queryAlarms, type AlarmAction, type AlarmRow } from "./alarms";
import { useNotificationQuery } from "./useNotificationQuery";

const readable = computed(() => hasNotificationFields("notifications.alarms.read", alarmReadFields));
const { rows, detail, busy, fresh, loaded, error, offset, limit, online, loadedAt, state, read, move, attempted, changed, denied } =
  useNotificationQuery(readable, queryAlarms, queryAlarm);
const target = shallowRef<{ kind: "alarm"; row: AlarmRow; action: AlarmAction } | { kind: "manual" } | null>(null);
watch(() => JSON.stringify(sessionState.subject), () => { target.value = null; }, { flush: "sync" });
const canCreate = computed(() => readable.value && hasNotificationFields("notifications.alarms.manual.create", manualAlarmFields));
function canAct(action: AlarmAction) {
  if (!detail.value || !online.value || busy.value || target.value) return false;
  if (!hasNotificationFields(alarmActionPermission(action), action === "silence" ? [...alarmActionFields, "Seconds"] : alarmActionFields)) return false;
  if (action === "clear") return detail.value.sourceKind === "Manual" && detail.value.state !== "Cleared";
  if (action === "silence") return detail.value.state !== "Cleared" && !["High", "Critical"].includes(detail.value.severity);
  return detail.value.state === "Active";
}
function prepare(action: AlarmAction) {
  if (detail.value && canAct(action)) target.value = { kind: "alarm", row: { ...detail.value }, action };
}
function load() { if (!target.value) void read(); }
function select(id: string) { if (!target.value && fresh.value) void read(id); }
</script>

<template>
  <section class="alarm-page" aria-labelledby="alarms-title">
    <h1 id="alarms-title">告警与原始事实</h1>
    <p class="notice">通知聚合不丢原始事实。确认、人工源清除和通知静默不改变设备联锁、预约、库存或责任问题状态。
      设备源不能人工伪造恢复；高危通知不能静默。</p>
    <p v-if="!readable" role="alert">缺少告警完整投影读取权限；资源范围由服务端当前授权过滤。</p>
    <p v-if="!online" role="status">当前离线，仅可查看陈旧参考；不排队写入，恢复连接后手工重新读取。</p>
    <div class="actions">
      <button type="button" data-test="read" :disabled="!readable || !online || busy || target !== null" @click="load">读取当前授权告警</button>
      <button type="button" data-test="manual" :disabled="!canCreate || !online || busy || target !== null" @click="target = { kind: 'manual' }">登记人工告警</button>
    </div>
    <NotificationActionPanel v-if="target" :target="target" @attempted="attempted" @changed="changed" @denied="denied" @close="target = null" />
    <p v-if="error && rows.length" class="error" role="alert">{{ error }}</p>
    <QueryState label="告警" :state="state" :busy="busy" :error-message="error"
      :stale-message="`陈旧告警，仅供参考；上次成功读取：${loadedAt || '未确认'}`" @retry="load">
      <template #empty><p>{{ loaded ? "当前授权范围没有告警，不表示设备已恢复或其他范围没有故障。" : "尚未查询，请显式读取。" }}</p></template>
      <ul v-if="rows.length" class="alarm-rows" data-test="alarm-rows">
        <li v-for="row in rows" :key="row.id">
          <h2>{{ row.code }} · {{ row.severity }} · {{ row.state }}</h2>
          <dl><dt>告警 UUID</dt><dd>{{ row.id }}</dd><dt>来源 / 资源</dt><dd>{{ row.sourceOwner }} / {{ row.resourceId }}</dd>
            <dt>来源类别</dt><dd>{{ row.sourceKind === "Manual" ? "人工源" : "设备源" }}</dd>
            <dt>首次 / 最近源 UTC</dt><dd>{{ row.firstOccurredAt }} / {{ row.lastOccurredAt }}</dd>
            <dt>独立事实次数 / 版本</dt><dd>{{ row.occurrenceCount }} / {{ row.version }}</dd>
            <dt>确认人 / 确认 UTC</dt><dd>{{ row.acknowledgedBy ?? "未确认" }} / {{ row.acknowledgedAt ?? "未确认" }}</dd>
            <dt>通知静默到期 UTC</dt><dd>{{ row.silenceUntil ?? "未静默" }}</dd></dl>
          <button type="button" data-test="alarm-detail" :disabled="!fresh || !online || busy || target !== null" @click="select(row.id)">读取原事实详情</button>
        </li>
      </ul>
    </QueryState>
    <RealtimePager label="告警实时分页" :offset="offset" :limit="limit" :count="rows.length"
      :busy="busy || !online || !readable || target !== null" :failed="!fresh || !!error" @change="move" />
    <section v-if="detail" class="alarm-detail" aria-label="告警详情">
      <h2>{{ detail.code }} · {{ detail.state }} · 原版本 {{ detail.version }}</h2>
      <p>确认人 / 确认 UTC：{{ detail.acknowledgedBy ?? "未确认" }} / {{ detail.acknowledgedAt ?? "未确认" }}</p>
      <p>最多显示最近 100 个原始事实；总次数 {{ detail.occurrenceCount }}。摘要为文本，不执行 HTML。</p>
      <ul data-test="occurrences"><li v-for="fact in detail.occurrences" :key="fact.id">
        <dl><dt>事实 / 来源事件</dt><dd>{{ fact.id }} / {{ fact.sourceEventId }}</dd>
          <dt>源时间 / 接收 UTC</dt><dd>{{ fact.occurredAt }} / {{ fact.receivedAt }}</dd>
          <dt>关联 UUID</dt><dd>{{ fact.correlationId }}</dd><dt>脱敏摘要</dt><dd>{{ fact.summary }}</dd></dl>
      </li></ul>
      <div class="actions">
        <button type="button" data-test="acknowledge" :disabled="!canAct('acknowledge')" @click="prepare('acknowledge')">准备确认</button>
        <button type="button" data-test="clear" :disabled="!canAct('clear')" @click="prepare('clear')">准备人工源清除</button>
        <button type="button" data-test="silence" :disabled="!canAct('silence')" @click="prepare('silence')">准备普通通知静默</button>
      </div>
    </section>
  </section>
</template>

<style scoped>
.alarm-page { min-width: 0; overflow-wrap: anywhere; }
.alarm-rows { display: grid; gap: var(--wes-space-3); padding: 0; list-style: none; }
.alarm-rows > li, .alarm-detail { padding: var(--wes-space-4); border: 1px solid var(--wes-color-border-soft); border-radius: var(--wes-radius); min-width: 0; }
dl { display: grid; grid-template-columns: auto minmax(0, 1fr); gap: var(--wes-space-2); }
dd { margin: 0; white-space: pre-wrap; }
@media (max-width: 30rem) { dl { grid-template-columns: minmax(0, 1fr); } }
</style>
