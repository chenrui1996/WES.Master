<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, shallowRef, useId, watch } from "vue";
import { ApiError, sessionState } from "../identity/identity-api";
import { alarmActionFields, alarmActionPermission, alarmReadFields, deliveryActionFields, deliveryReadFields, createManualAlarm, hasNotificationFields, manualAlarmFields,
  mutateAlarm, mutateDelivery, severityNames, validNotificationId, type AlarmAction, type AlarmCommand,
  type AlarmRow, type AlarmSeverity, type DeliveryRow, type ManualAlarmCommand } from "./alarms";

type Target = { kind: "alarm"; row: AlarmRow; action: AlarmAction } | { kind: "delivery"; row: DeliveryRow } | { kind: "manual" };
type Frozen = { kind: "alarm"; id: string; action: AlarmAction; body: AlarmCommand }
  | { kind: "delivery"; id: string; body: AlarmCommand } | { kind: "manual"; body: ManualAlarmCommand };
const props = defineProps<{ target: Target }>();
const emit = defineEmits<{ attempted: []; changed: []; denied: [message: string]; close: [] }>();
const inputId = useId();
const phase = ref<"draft" | "preview" | "submitting" | "unknown" | "confirmed" | "rejected" | "invalidated">("draft");
const reason = ref(""); const resourceId = ref(""); const code = ref(""); const summary = ref("");
const severity = ref<AlarmSeverity>("Warning"); const seconds = ref(900);
const command = shallowRef<Frozen | null>(null);
const online = ref(navigator.onLine); const error = ref("");
const permission = computed(() => props.target.kind === "manual" ? "notifications.alarms.manual.create"
  : props.target.kind === "delivery" ? "notifications.deliveries.retry" : alarmActionPermission(props.target.action));
const authorized = computed(() => hasNotificationFields("notifications.alarms.read", alarmReadFields)
  && (props.target.kind !== "delivery" || hasNotificationFields("notifications.deliveries.read", deliveryReadFields))
  && hasNotificationFields(permission.value, props.target.kind === "manual" ? manualAlarmFields
    : props.target.kind === "delivery" ? deliveryActionFields
      : props.target.action === "silence" ? [...alarmActionFields, "Seconds"] : alarmActionFields));
const allowed = computed(() => {
  if (!authorized.value) return false;
  if (props.target.kind === "delivery") return ["Pending", "Failed", "Unavailable"].includes(props.target.row.state);
  if (props.target.kind === "manual") return true;
  if (props.target.action === "clear") return props.target.row.sourceKind === "Manual" && props.target.row.state !== "Cleared";
  if (props.target.action === "silence") return !["High", "Critical"].includes(props.target.row.severity) && props.target.row.state !== "Cleared";
  return props.target.row.state === "Active";
});
const title = computed(() => props.target.kind === "manual" ? "人工告警登记"
  : props.target.kind === "delivery" ? "独立投递重试"
    : { acknowledge: "确认告警", clear: "清除人工源告警", silence: "普通通知静默" }[props.target.action]);
const fingerprint = computed(() => JSON.stringify([sessionState.subject, props.target]));
let controller = new AbortController(); let sequence = 0;
function cancel() { ++sequence; controller.abort(); controller = new AbortController(); }
watch(fingerprint, () => {
  cancel(); command.value = null; reason.value = ""; resourceId.value = ""; code.value = ""; summary.value = "";
  error.value = ""; phase.value = "invalidated";
}, { flush: "sync" });
function connection() {
  online.value = navigator.onLine;
  if (!online.value && phase.value === "submitting") {
    cancel(); phase.value = "unknown"; error.value = "连接中断，结果不明；仅可恢复原操作。";
  }
}
onMounted(() => { addEventListener("online", connection); addEventListener("offline", connection); });
onUnmounted(() => { cancel(); removeEventListener("online", connection); removeEventListener("offline", connection); });
function preview() {
  if (phase.value !== "draft" || !allowed.value || !online.value) return;
  if (props.target.kind === "manual") {
    if (!validNotificationId(resourceId.value) || !/^[A-Za-z][A-Za-z0-9_.-]{0,63}$/.test(code.value)
      || !summary.value.trim() || summary.value.length > 512) {
      error.value = "请输入非空资源 UUID、1 至 64 位合法业务码和非空摘要（最多 512 字）。"; return;
    }
    command.value = { kind: "manual", body: { operationId: crypto.randomUUID(), resourceId: resourceId.value,
      code: code.value, severity: severity.value, summary: summary.value } };
  } else {
    if (reason.value.trim().length < 3 || reason.value.length > 256 || /[\u0000-\u001f\u007f-\u009f]/.test(reason.value)
      || props.target.kind === "alarm" && props.target.action === "silence" &&
      (!Number.isSafeInteger(seconds.value) || seconds.value < 1 || seconds.value > 3600)) {
      error.value = "理由须为 3 至 256 个可打印字符；静默为 1 至 3600 秒。"; return;
    }
    const body: AlarmCommand = { operationId: crypto.randomUUID(), expectedVersion: props.target.row.version, reason: reason.value.trim() };
    if (props.target.kind === "alarm") {
      if (props.target.action === "silence") body.seconds = seconds.value;
      command.value = { kind: "alarm", id: props.target.row.id, action: props.target.action, body };
    } else command.value = { kind: "delivery", id: props.target.row.id, body };
  }
  error.value = ""; phase.value = "preview";
}
async function submit() {
  if (!allowed.value || !online.value || !command.value || !["preview", "unknown"].includes(phase.value)) return;
  const frozen = command.value;
  cancel(); const ticket = sequence; const signal = controller.signal; const scope = fingerprint.value;
  const current = () => ticket === sequence && scope === fingerprint.value && !signal.aborted;
  phase.value = "submitting"; error.value = ""; emit("attempted");
  try {
    if (frozen.kind === "manual") await createManualAlarm(frozen.body, signal);
    else if (frozen.kind === "alarm") await mutateAlarm(frozen.id, frozen.action, frozen.body, signal);
    else await mutateDelivery(frozen.id, frozen.body, signal);
    if (current()) { phase.value = "confirmed"; emit("changed"); }
  } catch (failure) {
    if (!current()) return;
    const definite = failure instanceof ApiError && [400, 401, 403, 404, 409].includes(failure.status);
    phase.value = definite ? "rejected" : "unknown";
    error.value = failure instanceof Error ? failure.message : "请求未取得确认，请核对原操作。";
    if (failure instanceof ApiError && [401, 403].includes(failure.status)) emit("denied", error.value);
  }
}
</script>

<template>
  <section class="notification-action" :aria-labelledby="`${inputId}-title`" :aria-busy="phase === 'submitting'">
    <h2 :id="`${inputId}-title`">{{ title }} · 二次确认</h2>
    <p class="notice">只改变软件告警或投递记录，不改变设备联锁、库存、预约或问题状态。
      结果不明时保留原键，不自动重发；关闭、导航或刷新会丢失内存上下文，请先记录操作 UUID。</p>
    <p v-if="!online" role="status">当前离线，禁止写入，不排队、不自动恢复提交。</p>
    <p v-if="phase === 'invalidated'" role="alert">当前主体、权限或目标已改变，请关闭并重新读取。</p>
    <p v-else-if="!allowed" role="alert">当前权限、来源或状态不能执行此操作。</p>
    <form v-else-if="phase === 'draft'" @submit.prevent="preview">
      <template v-if="target.kind === 'manual'">
        <label :for="`${inputId}-resource`">资源 UUID</label><input :id="`${inputId}-resource`" v-model="resourceId" :disabled="!online" />
        <label :for="`${inputId}-code`">告警业务码</label><input :id="`${inputId}-code`" v-model="code" maxlength="64" :disabled="!online" />
        <label :for="`${inputId}-severity`">等级</label>
        <select :id="`${inputId}-severity`" v-model="severity" :disabled="!online"><option v-for="name in severityNames" :key="name">{{ name }}</option></select>
        <label :for="`${inputId}-summary`">脱敏摘要（不填凭据）</label><textarea :id="`${inputId}-summary`" v-model="summary" maxlength="512" :disabled="!online" />
      </template>
      <template v-else>
        <label :for="`${inputId}-reason`">操作理由</label><textarea :id="`${inputId}-reason`" v-model="reason" maxlength="256" :disabled="!online" />
        <template v-if="target.kind === 'alarm' && target.action === 'silence'">
          <label :for="`${inputId}-seconds`">通知静默秒数（默认 900，最多 3600）</label>
          <input :id="`${inputId}-seconds`" v-model.number="seconds" type="number" min="1" max="3600" :disabled="!online" />
        </template>
      </template>
      <button type="submit" :disabled="!online">预览此操作</button>
    </form>
    <div v-if="command" data-test="operation-preview">
      <dl><dt>操作 UUID</dt><dd>{{ command.body.operationId }}</dd>
        <template v-if="command.kind !== 'manual'"><dt>目标 UUID</dt><dd>{{ command.id }}</dd><dt>原版本</dt><dd>{{ command.body.expectedVersion }}</dd></template>
      </dl>
      <pre>{{ JSON.stringify(command.body, null, 2) }}</pre>
      <button v-if="phase === 'preview'" type="button" data-test="confirm" :disabled="!online || !allowed" @click="submit">确认提交原操作</button>
      <button v-if="phase === 'unknown'" type="button" data-test="recover" :disabled="!online || !allowed" @click="submit">仅恢复此原键与原输入</button>
    </div>
    <p v-if="phase === 'submitting'" role="status">提交中；中断网络不表示事务回滚。</p>
    <p v-if="phase === 'unknown'" role="alert">结果不明：请求可能已提交。不要换键重复执行。</p>
    <p v-if="phase === 'confirmed'" role="status">已确认或识别原操作，请显式重新读取；不表示设备恢复或人已阅读。</p>
    <p v-if="phase === 'rejected'" role="alert">本次请求被拒绝，请重新核对；拒绝不证明此前不明请求未提交。</p>
    <p v-if="error" class="error" role="alert">{{ error }}</p>
    <button type="button" :disabled="phase === 'submitting'" @click="emit('close')">已记录操作，关闭面板</button>
  </section>
</template>

<style scoped>
.notification-action { min-width: 0; margin-block: var(--wes-space-4); padding: var(--wes-space-4); border: 1px solid var(--wes-color-border-soft); border-radius: var(--wes-radius); overflow-wrap: anywhere; }
form { display: grid; gap: var(--wes-space-2); max-width: 40rem; }
input, textarea, select { min-width: 0; width: 100%; }
textarea { min-height: 5rem; }
dl { display: grid; grid-template-columns: auto minmax(0, 1fr); gap: var(--wes-space-2); }
dd { margin: 0; }
pre { white-space: pre-wrap; overflow-wrap: anywhere; }
button { max-width: 100%; white-space: normal; }
</style>
