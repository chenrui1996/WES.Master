<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, shallowRef, useId, watch } from "vue";
import { ApiError, sessionState } from "../identity/identity-api";
import type { MasterData } from "./master-data";
import { changeWarehouseState, mayChangeWarehouseState, warehouseStateError, type WarehouseStateCommand, type WarehouseStateReceipt } from "./warehouse-state";

const props = defineProps<{ warehouse: Extract<MasterData, { kind: "warehouses" }> }>();
const emit = defineEmits<{ attempted: []; read: [id: string]; close: [] }>();
const label = useId();
const phase = ref<"draft" | "preview" | "submitting" | "unknown" | "confirmed" | "rejected" | "invalidated">("draft");
const reason = ref(""); const error = ref(""); const uncertain = ref(false); const online = ref(navigator.onLine);
const command = shallowRef<WarehouseStateCommand>(); const receipt = shallowRef<WarehouseStateReceipt>();
const authorized = computed(mayChangeWarehouseState);
const fingerprint = computed(() => JSON.stringify([sessionState.subject, props.warehouse.definition.id, props.warehouse.definition.version]));
const action = computed(() => props.warehouse.definition.enabled ? "禁用" : "启用");
let controller = new AbortController(); let sequence = 0; let deadline: ReturnType<typeof setTimeout> | undefined;
function cancel() {
  if (deadline !== undefined) clearTimeout(deadline);
  deadline = undefined; ++sequence; controller.abort(); controller = new AbortController();
}
watch(fingerprint, () => {
  cancel(); command.value = undefined; receipt.value = undefined; reason.value = ""; error.value = ""; phase.value = "invalidated";
}, { flush: "sync" });
function connection() {
  online.value = navigator.onLine;
  if (!online.value && phase.value === "submitting") {
    cancel(); uncertain.value = true; phase.value = "unknown";
    error.value = "连接中断，启停结果不明；恢复后只可手工恢复原操作。";
  }
}
onMounted(() => { addEventListener("online", connection); addEventListener("offline", connection); });
onUnmounted(() => { cancel(); removeEventListener("online", connection); removeEventListener("offline", connection); });
function preview() {
  if (!authorized.value || !online.value || phase.value !== "draft") return;
  const definition = props.warehouse.definition;
  const request = { id: definition.id, expectedVersion: definition.version, enabled: !definition.enabled, reason: reason.value, operation: crypto.randomUUID() };
  error.value = warehouseStateError(request); if (error.value) return;
  command.value = request; phase.value = "preview";
}
async function submit() {
  if (!authorized.value || !online.value || !command.value || !["preview", "unknown"].includes(phase.value)) return;
  cancel(); const ticket = sequence; const signal = controller.signal; const scope = fingerprint.value; const request = command.value;
  phase.value = "submitting"; error.value = ""; emit("attempted");
  const current = () => sequence === ticket && scope === fingerprint.value && !signal.aborted;
  deadline = setTimeout(() => {
    if (!current()) return;
    cancel(); uncertain.value = true; phase.value = "unknown"; error.value = "10秒内未取得启停确认，结果不明；不能换键盲发。";
  }, 10000);
  try {
    const result = await changeWarehouseState(request, signal);
    if (current()) { receipt.value = result; phase.value = "confirmed"; }
  } catch (failure) {
    if (!current()) return;
    const definite = failure instanceof ApiError && [400, 401, 403, 404, 405, 409].includes(failure.status);
    if (!definite) uncertain.value = true;
    phase.value = definite ? "rejected" : "unknown";
    error.value = failure instanceof ApiError && failure.status === 409
      ? "仓库版本、状态或原操作已变化，或存在活动入库、预留、库存、启用库位；请另读当前事实，不会自动重试。"
      : failure instanceof ApiError && [404, 405].includes(failure.status)
        ? "仓库启停入口未开放或原仓库未找到；不会自动重试。"
        : failure instanceof Error ? failure.message : "仓库启停结果未确认，请核对原操作。";
  } finally { if (ticket === sequence && deadline !== undefined) { clearTimeout(deadline); deadline = undefined; } }
}
</script>

<template>
  <section class="warehouse-state" :aria-labelledby="`${label}-title`" :aria-busy="phase === 'submitting'">
    <h2 :id="`${label}-title`">{{ action }}仓库逻辑状态</h2>
    <p class="notice">仅改变仓库逻辑状态，不修改库存、不启停子库位，不下发设备或运输命令；启用不代表可收货、可分配或物理安全许可。
      未确认不可换键盲发；关闭、导航或刷新会丢失内存上下文，请先记录原操作 UUID 和全部输入。</p>
    <p v-if="!online" role="status">当前离线，禁止提交、不排队，恢复后不会自动重试。</p>
    <p v-if="phase === 'invalidated' || !authorized" role="alert">当前主体、资源或字段权限已改变，请关闭并重新准备。</p>
    <form v-else-if="phase === 'draft'" @submit.prevent="preview">
      <p>原仓库：{{ warehouse.definition.code }}；UUID：{{ warehouse.definition.id }}；期望版本：{{ warehouse.definition.version }}</p>
      <label :for="`${label}-reason`">仓库启停原因</label>
      <textarea :id="`${label}-reason`" v-model="reason" name="warehouseStateReason" maxlength="256" :disabled="!online" />
      <button type="submit" :disabled="!online">预览仓库启停</button>
    </form>
    <div v-if="command" data-test="warehouse-state-preview">
      <dl><dt>原仓库 UUID</dt><dd>{{ command.id }}</dd><dt>期望版本</dt><dd>{{ command.expectedVersion }}</dd>
        <dt>目标逻辑状态</dt><dd>{{ command.enabled ? "启用" : "禁用" }}</dd><dt>原因</dt><dd>{{ command.reason }}</dd>
        <dt>操作 UUID</dt><dd>{{ command.operation }}</dd></dl>
      <button v-if="phase === 'preview'" type="button" data-test="confirm-warehouse-state" :disabled="!online || !authorized" @click="submit">确认仓库逻辑启停</button>
      <button v-if="phase === 'unknown'" type="button" data-test="retry-warehouse-state" :disabled="!online || !authorized" @click="submit">手工恢复原启停操作</button>
    </div>
    <p v-if="error" role="alert">{{ error }}</p>
    <p v-if="phase === 'unknown'" role="status">结果不明，不代表状态未改变。</p>
    <p v-if="phase === 'rejected'" role="status">本次请求被拒绝。{{ uncertain ? "先前不明结果仍需查证，禁止换键盲发。" : "未取得启停确认。" }}</p>
    <p v-if="receipt" role="status">已确认原启停操作：{{ receipt.id }}；原版本：{{ receipt.version }}。这不是当前状态或物理安全确认。</p>
    <button v-if="receipt" type="button" data-test="read-state-warehouse" :disabled="!online || !authorized" @click="emit('read', receipt.id)">读取仓库当前详情</button>
    <button type="button" :disabled="phase === 'submitting'" @click="emit('close')">关闭仓库启停面板</button>
  </section>
</template>

<style scoped>
.warehouse-state { min-width: 0; margin-block: var(--wes-space-6); overflow-wrap: anywhere; }
form { display: grid; gap: var(--wes-space-2); }
textarea, button { max-width: 100%; min-width: 0; }
textarea { width: 100%; }
dl { display: grid; grid-template-columns: minmax(0, 1fr) minmax(0, 3fr); gap: var(--wes-space-2); }
dd { margin: 0; }
@media (max-width: 40rem) { dl { grid-template-columns: minmax(0, 1fr); } }
</style>
