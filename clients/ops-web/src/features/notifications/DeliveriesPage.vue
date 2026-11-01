<script setup lang="ts">
import { computed, shallowRef, watch } from "vue";
import QueryState from "../../components/QueryState.vue";
import RealtimePager from "../../components/RealtimePager.vue";
import { sessionState } from "../identity/identity-api";
import NotificationActionPanel from "./NotificationActionPanel.vue";
import { alarmReadFields, deliveryActionFields, deliveryReadFields, hasNotificationFields, queryDeliveries, queryDelivery, type DeliveryRow } from "./alarms";
import { useNotificationQuery } from "./useNotificationQuery";

const readable = computed(() => hasNotificationFields("notifications.deliveries.read", deliveryReadFields)
  && hasNotificationFields("notifications.alarms.read", alarmReadFields));
const { rows, detail, busy, fresh, loaded, error, offset, limit, online, loadedAt, state, read, move, attempted, changed, denied } =
  useNotificationQuery(readable, queryDeliveries, queryDelivery);
const target = shallowRef<{ kind: "delivery"; row: DeliveryRow } | null>(null);
watch(() => JSON.stringify(sessionState.subject), () => { target.value = null; }, { flush: "sync" });
const retryable = computed(() => !!detail.value && online.value && !busy.value && !target.value
  && ["Pending", "Failed", "Unavailable"].includes(detail.value.state)
  && hasNotificationFields("notifications.deliveries.retry", deliveryActionFields));
function load() { if (!target.value) void read(); }
function select(id: string) { if (!target.value && fresh.value) void read(id); }
function prepare() { if (retryable.value && detail.value) target.value = { kind: "delivery", row: { ...detail.value } }; }
</script>

<template>
  <section class="delivery-page" aria-labelledby="deliveries-title">
    <h1 id="deliveries-title">独立通知投递</h1>
    <p class="notice">站内通知通过当前授权查询恢复；Sent 不表示已读，不代表告警已清除或设备恢复。
      外部邮件或在线推送未配置时不得当作已送达。</p>
    <p v-if="!readable" role="alert">缺少独立投递完整字段读取权限；当前资源范围由服务端复验。</p>
    <p v-if="!online" role="status">当前离线，已加载投递仅供陈旧参考；不自动重试或排队。</p>
    <button type="button" data-test="read" :disabled="!readable || !online || busy || target !== null" @click="load">读取当前授权投递</button>
    <NotificationActionPanel v-if="target" :target="target" @attempted="attempted" @changed="changed" @denied="denied" @close="target = null" />
    <p v-if="error && rows.length" class="error" role="alert">{{ error }}</p>
    <QueryState label="独立投递" :state="state" :busy="busy" :error-message="error"
      :stale-message="`陈旧投递，仅供参考；上次成功读取：${loadedAt || '未确认'}`" @retry="load">
      <template #empty><p>{{ loaded ? "当前授权范围没有投递，不表示渠道健康或所有告警已读。" : "尚未查询，请显式读取。" }}</p></template>
      <ul v-if="rows.length" class="delivery-rows" data-test="delivery-rows"><li v-for="row in rows" :key="row.id">
        <h2>{{ row.channel === "InApp" ? "站内" : row.channel }} · {{ row.state }}</h2>
        <dl><dt>投递 / 告警 UUID</dt><dd>{{ row.id }} / {{ row.alarmId }}</dd>
          <dt>聚合事实次数 / 尝试数</dt><dd>{{ row.occurrenceCount }} / {{ row.attemptCount }}</dd>
          <dt>到期 UTC / 下次尝试</dt><dd>{{ row.dueAt }} / {{ row.nextAttemptAt ?? "无安排" }}</dd>
          <dt>安全错误 / 版本</dt><dd>{{ row.errorCode ?? "未记录错误" }} / {{ row.version }}</dd></dl>
        <button type="button" data-test="delivery-detail" :disabled="!fresh || !online || busy || target !== null" @click="select(row.id)">读取投递尝试详情</button>
      </li></ul>
    </QueryState>
    <RealtimePager label="投递实时分页" :offset="offset" :limit="limit" :count="rows.length"
      :busy="busy || !online || !readable || target !== null" :failed="!fresh || !!error" @change="move" />
    <section v-if="detail" class="delivery-detail" aria-label="投递尝试">
      <h2>投递 {{ detail.id }} · {{ detail.state }}</h2>
      <p>最多显示最近 100 次尝试；总尝试数 {{ detail.attemptCount }}。不展示地址、凭据或协议报文。</p>
      <ul data-test="attempts"><li v-for="attempt in detail.attempts" :key="attempt.id">
        {{ attempt.id }} · {{ attempt.attemptedAt }} · {{ attempt.state }} · {{ attempt.errorCode ?? "未记录错误" }}
      </li></ul>
      <button type="button" data-test="retry-delivery" :disabled="!retryable" @click="prepare">准备独立投递重试</button>
    </section>
  </section>
</template>

<style scoped>
.delivery-page { min-width: 0; overflow-wrap: anywhere; }
.delivery-rows { display: grid; gap: var(--wes-space-3); list-style: none; padding: 0; }
.delivery-rows > li, .delivery-detail { min-width: 0; padding: var(--wes-space-4); border: 1px solid var(--wes-color-border-soft); border-radius: var(--wes-radius); }
dl { display: grid; grid-template-columns: auto minmax(0, 1fr); gap: var(--wes-space-2); }
dd { margin: 0; white-space: pre-wrap; }
@media (max-width: 30rem) { dl { grid-template-columns: minmax(0, 1fr); } }
</style>
