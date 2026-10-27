<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, shallowRef, useId, watch } from "vue";
import { ApiError, sessionState } from "../identity/identity-api";
import { createLocation, createWarehouse, locationDraftError, mayCreateLocation, mayCreateWarehouse, warehouseDraftError,
  type LocationCreationCommand, type MasterDataCreationReceipt, type ResourceKind, type WarehouseCreationCommand } from "./master-data";

const props = withDefaults(defineProps<{ kind?: ResourceKind }>(), { kind: "warehouses" });
const emit = defineEmits<{ attempted: []; read: [id: string]; close: [] }>();
const codeId = useId(); const nameId = useId(); const headingId = useId();
const parentId = useId(); const packInputId = useId(); const schemaId = useId();
const isLocation = computed(() => props.kind === "locations");
const noun = computed(() => isLocation.value ? "库位" : "仓库");
const key = computed(() => isLocation.value ? "location" : "warehouse");
const phase = ref<"draft" | "preview" | "submitting" | "unknown" | "confirmed" | "rejected" | "invalidated">("draft");
const code = ref(""); const name = ref(""); const error = ref(""); const uncertain = ref(false);
const warehouseId = ref(""); const packId = ref(""); const schemaVersion = ref("");
const online = ref(navigator.onLine);
const command = shallowRef<WarehouseCreationCommand | LocationCreationCommand | null>(null);
const receipt = shallowRef<MasterDataCreationReceipt | null>(null);
const locationCommand = computed(() => command.value && "warehouseId" in command.value ? command.value : null);
const authorized = computed(() => isLocation.value ? mayCreateLocation() : mayCreateWarehouse());
const fingerprint = computed(() => JSON.stringify([props.kind, sessionState.subject]));
let controller = new AbortController(); let sequence = 0;
let deadline: ReturnType<typeof setTimeout> | undefined;
function cancel() {
  if (deadline !== undefined) clearTimeout(deadline);
  deadline = undefined; sequence++; controller.abort(); controller = new AbortController();
}
watch(fingerprint, () => {
  cancel(); command.value = null; receipt.value = null; code.value = ""; name.value = "";
  warehouseId.value = ""; packId.value = ""; schemaVersion.value = ""; error.value = ""; phase.value = "invalidated";
}, { flush: "sync" });
function connection() {
  online.value = navigator.onLine;
  if (!online.value && phase.value === "submitting") {
    cancel(); uncertain.value = true; phase.value = "unknown";
    error.value = "连接中断，创建结果不明；恢复后只可显式重试原操作。";
  }
}
onMounted(() => { addEventListener("online", connection); addEventListener("offline", connection); });
onUnmounted(() => { cancel(); removeEventListener("online", connection); removeEventListener("offline", connection); });
function preview() {
  if (phase.value !== "draft" || !authorized.value || !online.value) return;
  error.value = isLocation.value
    ? locationDraftError(code.value, name.value, warehouseId.value, packId.value, schemaVersion.value)
    : warehouseDraftError(code.value, name.value);
  if (error.value) return;
  const common = { code: code.value.toUpperCase(), name: name.value, operation: crypto.randomUUID() };
  command.value = isLocation.value
    ? { ...common, warehouseId: warehouseId.value, packId: packId.value, schemaVersion: schemaVersion.value } : common;
  phase.value = "preview";
}
async function submit() {
  if (!authorized.value || !online.value || !command.value || !["preview", "unknown"].includes(phase.value)) return;
  cancel(); const ticket = sequence; const signal = controller.signal; const scope = fingerprint.value;
  const request = command.value;
  phase.value = "submitting"; error.value = ""; emit("attempted");
  const current = () => sequence === ticket && scope === fingerprint.value && !signal.aborted;
  deadline = setTimeout(() => {
    if (!current()) return;
    cancel(); uncertain.value = true; phase.value = "unknown";
    error.value = "10 秒内未取得创建确认，结果不明；请保留原操作，不能换键盲发。";
  }, 10000);
  try {
    const result = "warehouseId" in request ? await createLocation(request, signal) : await createWarehouse(request, signal);
    if (current()) { receipt.value = result; phase.value = "confirmed"; }
  } catch (failure) {
    if (!current()) return;
    const definite = failure instanceof ApiError && [400, 401, 403, 404, 405, 409, ...(isLocation.value ? [422] : [])].includes(failure.status);
    if (!definite) uncertain.value = true;
    phase.value = definite ? "rejected" : "unknown";
    error.value = failure instanceof ApiError && [404, 405].includes(failure.status)
      ? isLocation.value ? "库位创建入口未开放或父仓未找到，请核对独立写开关、父仓及具名 schema 装配；不会自动重试。"
        : "仓库创建入口未开放，请由部署管理员核对独立写开关；不会自动重试。"
      : failure instanceof Error ? failure.message : "创建结果未确认，请核对原操作。";
  } finally {
    if (ticket === sequence && deadline !== undefined) { clearTimeout(deadline); deadline = undefined; }
  }
}
</script>

<template>
  <section class="master-data-creation" :aria-labelledby="headingId" :aria-busy="phase === 'submitting'">
    <h2 :id="headingId">创建禁用{{ noun }}</h2>
    <p class="notice">创建后默认禁用，不代表可分配或物理安全许可；{{ isLocation ? "父仓及固定 Pack/schema 须由服务端复验，不是可用 Pack 目录；不会发送设备命令。" : "不会创建库位或发送设备命令。" }}
      未确认时不可换新键盲发。关闭、导航或刷新会丢失内存上下文，请先记录操作 UUID 和原输入。</p>
    <p v-if="!online" role="status">当前离线，禁止提交；不排队，恢复后不会自动重试。</p>
    <p v-if="phase === 'invalidated' || !authorized" role="alert">当前主体或字段权限已改变，请关闭并重新准备。</p>
    <form v-else-if="phase === 'draft'" @submit.prevent="preview">
      <label :for="codeId">新{{ noun }}代码</label>
      <input :id="codeId" v-model="code" :name="`${key}Code`" maxlength="64" autocomplete="off" spellcheck="false" :disabled="!online" />
      <label :for="nameId">新{{ noun }}名称</label>
      <input :id="nameId" v-model="name" :name="`${key}Name`" maxlength="128" autocomplete="off" :disabled="!online" />
      <template v-if="isLocation">
        <label :for="parentId">父仓 UUID（提交时复验当前读取权）</label>
        <input :id="parentId" v-model="warehouseId" name="warehouseId" maxlength="36" autocomplete="off" spellcheck="false" :disabled="!online" />
        <label :for="packInputId">固定 Pack UUID</label>
        <input :id="packInputId" v-model="packId" name="packId" maxlength="36" autocomplete="off" spellcheck="false" :disabled="!online" />
        <label :for="schemaId">固定 schema 版本</label>
        <input :id="schemaId" v-model="schemaVersion" name="schemaVersion" maxlength="64" autocomplete="off" spellcheck="false" :disabled="!online" />
      </template>
      <button type="submit" :disabled="!online">预览禁用{{ noun }}创建</button>
    </form>
    <div v-if="command" :data-test="`${key}-preview`">
      <dl>
        <dt>规范代码</dt><dd>{{ command.code }}</dd>
        <dt>名称</dt><dd>{{ command.name }}</dd>
        <dt>创建后状态</dt><dd>禁用</dd>
        <dt>操作 UUID</dt><dd>{{ command.operation }}</dd>
        <template v-if="locationCommand">
          <dt>固定父仓 UUID</dt><dd>{{ locationCommand.warehouseId }}</dd>
          <dt>固定 Pack UUID</dt><dd>{{ locationCommand.packId }}</dd>
          <dt>固定 schema</dt><dd>{{ locationCommand.schemaVersion }}</dd>
        </template>
      </dl>
      <button v-if="phase === 'preview'" type="button" :data-test="`confirm-${key}`" :disabled="!online || !authorized" @click="submit">确认创建禁用{{ noun }}</button>
      <button v-if="phase === 'unknown'" type="button" :data-test="`retry-${key}`" :disabled="!online || !authorized" @click="submit">仅重试此原创建操作</button>
    </div>
    <p v-if="phase === 'submitting'" role="status">提交中，请勿关闭；取消网络请求不等于业务回滚。</p>
    <p v-if="phase === 'unknown'" role="alert">结果不明：请求可能已提交，原输入与操作键已冻结。</p>
    <p v-if="phase === 'rejected'" role="alert">本次请求被拒绝，请核对当前授权、输入或部署。
      {{ uncertain ? "先前不明结果仍需查证，不能据此认定先前未提交。" : "" }}</p>
    <div v-if="receipt" :data-test="`${key}-receipt`">
      <p role="status">已确认禁用{{ noun }}{{ receipt.decision === 1 ? "（已识别原操作）" : "" }}；未启用，不代表可分配。</p>
      <dl><dt>资源 UUID</dt><dd>{{ receipt.id }}</dd><dt>版本</dt><dd>{{ receipt.version }}</dd></dl>
      <button type="button" :data-test="`read-created-${key}`" :disabled="!online || !authorized" @click="emit('read', receipt.id)">用回执 UUID 读取当前{{ noun }}</button>
    </div>
    <p v-if="error" class="error" role="alert">{{ error }}</p>
    <button type="button" :disabled="phase === 'submitting'" @click="emit('close')">{{ phase === "unknown" ? "已记录原操作，关闭不明结果" : "关闭创建面板" }}</button>
  </section>
</template>

<style scoped>
.master-data-creation { min-width: 0; overflow-wrap: anywhere; padding: var(--wes-space-4); border: 1px solid var(--wes-color-border-soft); border-radius: var(--wes-radius); margin-block: var(--wes-space-4); }
form { display: grid; gap: var(--wes-space-2); }
input { width: 100%; min-width: 0; box-sizing: border-box; }
dl { display: grid; grid-template-columns: auto minmax(0, 1fr); gap: var(--wes-space-2); }
dd { margin: 0; white-space: pre-wrap; }
button { max-width: 100%; white-space: normal; }
@media (max-width: 30rem) { dl { grid-template-columns: minmax(0, 1fr); } }
</style>
