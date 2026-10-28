<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, shallowRef, useId, watch } from "vue";
import { ApiError, sessionState } from "../identity/identity-api";
import { mayReadStockResource, mayRegisterStockResource, readStockResource, registerStockResource, resourceTitles,
  stockResourceError, type StockResourceCommand, type StockResourceDetails, type StockResourceKind, type StockResourceReceipt } from "./stock-resources";

const emit = defineEmits<{ close: [] }>();
const label = useId();
const kind = ref<StockResourceKind>("units");
const code = ref(""); const name = ref(""); const countable = ref(true); const precision = ref("0");
const baseUnitId = ref(""); const batchRequired = ref(false); const warehouseId = ref(""); const locationId = ref("");
const itemId = ref(""); const batch = ref(""); const containerId = ref(""); const lookupId = ref("");
const phase = ref<"draft" | "preview" | "submitting" | "unknown" | "confirmed" | "rejected" | "invalidated">("draft");
const command = shallowRef<StockResourceCommand>(); const receipt = shallowRef<StockResourceReceipt>(); const details = shallowRef<StockResourceDetails>();
const error = ref(""); const reading = ref(false); const uncertain = ref(false); const online = ref(navigator.onLine);
const allowed = computed(() => mayRegisterStockResource(kind.value));
const readable = computed(() => kind.value !== "dimensions" && mayReadStockResource(kind.value));
const identity = computed(() => JSON.stringify(sessionState.subject));
let controller = new AbortController(); let sequence = 0; let timeout: ReturnType<typeof setTimeout> | undefined;
function cancel() {
  if (timeout !== undefined) clearTimeout(timeout);
  timeout = undefined; ++sequence; controller.abort(); controller = new AbortController();
}
watch(identity, () => {
  cancel(); phase.value = "invalidated"; command.value = undefined; receipt.value = undefined; details.value = undefined;
  error.value = ""; reading.value = false; clearFields();
}, { flush: "sync" });
watch(kind, () => { details.value = undefined; error.value = ""; lookupId.value = ""; clearFields(); });
function clearFields() {
  code.value = ""; name.value = ""; countable.value = true; precision.value = "0"; baseUnitId.value = "";
  batchRequired.value = false; warehouseId.value = ""; locationId.value = ""; itemId.value = ""; batch.value = ""; containerId.value = "";
}
function connection() {
  online.value = navigator.onLine;
  if (online.value) return;
  details.value = undefined;
  if (phase.value === "submitting") {
    cancel(); uncertain.value = true; phase.value = "unknown"; error.value = "连接中断，登记结果不明；仅可在线手工恢复原操作。";
  } else if (reading.value) {
    cancel(); reading.value = false; error.value = "连接中断，未取得当前资源详情。";
  }
}
onMounted(() => { addEventListener("online", connection); addEventListener("offline", connection); });
onUnmounted(() => { cancel(); removeEventListener("online", connection); removeEventListener("offline", connection); });
function preview() {
  if (!allowed.value || !online.value || reading.value || phase.value !== "draft") return;
  const operation = crypto.randomUUID();
  let proposed: StockResourceCommand;
  switch (kind.value) {
    case "units": proposed = { kind: kind.value, operation, input: { code: code.value, name: name.value, countable: countable.value,
      precision: /^[0-6]$/.test(precision.value) ? Number(precision.value) : -1 } }; break;
    case "items": proposed = { kind: kind.value, operation, input: { code: code.value, name: name.value,
      baseUnitId: baseUnitId.value, batchRequired: batchRequired.value } }; break;
    case "containers": proposed = { kind: kind.value, operation, input: { code: code.value, name: name.value, warehouseId: warehouseId.value } }; break;
    case "dimensions": proposed = { kind: kind.value, operation, input: { warehouseId: warehouseId.value, locationId: locationId.value,
      itemId: itemId.value, batch: batch.value, containerId: containerId.value } }; break;
  }
  error.value = stockResourceError(proposed); if (error.value) return;
  details.value = undefined; command.value = proposed; phase.value = "preview";
}
async function submit() {
  if (!command.value || !mayRegisterStockResource(command.value.kind) || !online.value || !["preview", "unknown"].includes(phase.value)) return;
  cancel(); const ticket = sequence; const signal = controller.signal; const scope = identity.value; const original = command.value;
  const current = () => sequence === ticket && scope === identity.value && !signal.aborted;
  phase.value = "submitting"; error.value = "";
  timeout = setTimeout(() => {
    if (!current()) return;
    cancel(); uncertain.value = true; phase.value = "unknown"; error.value = "10秒内未取得登记确认，结果不明；禁止换键盲发。";
  }, 10000);
  try {
    const result = await registerStockResource(original, signal);
    if (current()) { receipt.value = result; uncertain.value = false; phase.value = "confirmed"; }
  } catch (failure) {
    if (!current()) return;
    const definite = failure instanceof ApiError && [400, 401, 403, 404, 405, 409, 422].includes(failure.status);
    if (!definite) uncertain.value = true;
    phase.value = definite ? "rejected" : "unknown";
    error.value = failure instanceof ApiError && failure.status === 409
      ? "原操作、代码或维度冲突，或仓/位未启用/归属不符；不会自动换键重试。"
      : failure instanceof ApiError && failure.status === 422 ? "固定库位schema拒绝，未取得登记确认。"
      : failure instanceof Error ? failure.message : "登记未确认，请核对原操作。";
  } finally { if (ticket === sequence && timeout !== undefined) { clearTimeout(timeout); timeout = undefined; } }
}
async function read(id: string) {
  if (kind.value === "dimensions" || !readable.value || !online.value || reading.value
    || ["submitting", "preview", "unknown", "invalidated"].includes(phase.value)) return;
  cancel(); const ticket = sequence; const signal = controller.signal; const scope = identity.value; const resourceKind = kind.value;
  const current = () => sequence === ticket && scope === identity.value && kind.value === resourceKind && !signal.aborted;
  reading.value = true; details.value = undefined; error.value = "";
  timeout = setTimeout(() => {
    if (!current()) return;
    cancel(); reading.value = false; error.value = "10秒内未取得当前资源详情，请在线重新读取。";
  }, 10000);
  try {
    const result = await readStockResource(resourceKind, id, signal);
    if (current()) details.value = result;
  } catch (failure) {
    if (current()) error.value = failure instanceof Error ? failure.message : "未取得当前资源详情。";
  } finally {
    if (ticket === sequence) { reading.value = false; if (timeout !== undefined) clearTimeout(timeout); timeout = undefined; }
  }
}
function next() {
  if (uncertain.value || reading.value || !["confirmed", "rejected"].includes(phase.value)) return;
  cancel(); command.value = undefined; receipt.value = undefined; details.value = undefined; error.value = "";
  phase.value = "draft"; clearFields();
}
</script>

<template>
  <section class="stock-resources" :aria-labelledby="`${label}-title`" :aria-busy="phase === 'submitting' || reading">
    <h2 :id="`${label}-title`">单位、物料、容器与空账维度</h2>
    <p class="notice">登记不入账、不占位、不下发运输。空账维度仅创建零余额，精度由物料基准单位派生。
      未确认不可换键；关闭、导航或刷新会丢失内存上下文，请先记录原操作 UUID 与全部输入。服务端仍核真实关联和当前权限。</p>
    <p v-if="!online" role="status">当前离线，不排队，恢复后不会自动重试。</p>
    <label :for="`${label}-kind`">登记资源类型</label>
    <select :id="`${label}-kind`" v-model="kind" name="stockResourceKind" :disabled="phase !== 'draft' || reading">
      <option value="units">基准单位</option><option value="items">物料</option><option value="containers">容器</option><option value="dimensions">空账维度</option>
    </select>
    <p v-if="phase === 'invalidated'" role="alert">当前主体或权限已改变，请关闭后重新准备。</p>
    <p v-else-if="!allowed" role="alert">缺少当前完整写字段或关联读取权限，不能登记此类资源。</p>
    <form v-if="phase === 'draft' && allowed" data-test="stock-registration" @submit.prevent="preview">
      <template v-if="kind !== 'dimensions'">
        <label :for="`${label}-code`">资源代码</label><input :id="`${label}-code`" v-model="code" name="stockResourceCode" maxlength="64" :disabled="!online || reading" />
        <label :for="`${label}-name`">资源名称</label><input :id="`${label}-name`" v-model="name" name="stockResourceName" maxlength="128" :disabled="!online || reading" />
      </template>
      <template v-if="kind === 'units'">
        <label><input v-model="countable" name="stockCountable" type="checkbox" :disabled="!online || reading" />计件单位（必须精度0）</label>
        <label :for="`${label}-precision`">基准单位精度（0至6）</label><input :id="`${label}-precision`" v-model="precision" name="stockPrecision" inputmode="numeric" :disabled="!online || reading" />
      </template>
      <template v-if="kind === 'items'">
        <label :for="`${label}-unit`">已登记基准单位 UUID</label><input :id="`${label}-unit`" v-model="baseUnitId" name="stockBaseUnitId" :disabled="!online || reading" />
        <label><input v-model="batchRequired" name="stockBatchRequired" type="checkbox" :disabled="!online || reading" />必须批次（不支持序列或效期登记）</label>
      </template>
      <template v-if="kind === 'containers' || kind === 'dimensions'">
        <label :for="`${label}-warehouse`">真实仓库 UUID</label><input :id="`${label}-warehouse`" v-model="warehouseId" name="stockWarehouseId" :disabled="!online || reading" />
      </template>
      <template v-if="kind === 'dimensions'">
        <label :for="`${label}-location`">已启用库位 UUID</label><input :id="`${label}-location`" v-model="locationId" name="stockLocationId" :disabled="!online || reading" />
        <label :for="`${label}-item`">已登记物料 UUID</label><input :id="`${label}-item`" v-model="itemId" name="stockItemId" :disabled="!online || reading" />
        <label :for="`${label}-batch`">精确批次（按物料政策）</label><input :id="`${label}-batch`" v-model="batch" name="stockBatch" maxlength="128" :disabled="!online || reading" />
        <label :for="`${label}-container`">同仓已登记容器 UUID</label><input :id="`${label}-container`" v-model="containerId" name="stockContainerId" :disabled="!online || reading" />
      </template>
      <button type="submit" :disabled="!online || reading">预览登记{{ resourceTitles[kind] }}</button>
    </form>
    <div v-if="command" data-test="stock-resource-preview">
      <h3>登记{{ resourceTitles[command.kind] }}完整预览</h3>
      <p>原操作 UUID：<span data-test="stock-operation">{{ command.operation }}</span></p>
      <pre>{{ JSON.stringify(command.input, null, 2) }}</pre>
      <button v-if="phase === 'preview'" type="button" :disabled="!online || !allowed" data-test="confirm-stock-resource" @click="submit">确认登记{{ resourceTitles[command.kind] }}</button>
      <button v-if="phase === 'unknown'" type="button" :disabled="!online || !allowed" data-test="retry-stock-resource" @click="submit">手工恢复原登记操作</button>
    </div>
    <p v-if="error" role="alert">{{ error }}</p>
    <p v-if="phase === 'unknown'" role="status">结果不明，不代表未登记；仅可原键原输入恢复。</p>
    <p v-if="phase === 'rejected'" role="status">本次请求被拒绝。{{ uncertain ? "先前不明结果仍需查证，禁止换键盲发。" : "未取得登记确认。" }}</p>
    <p v-if="receipt" role="status" data-test="stock-resource-receipt">已确认原登记：{{ receipt.id }}；原版本：{{ receipt.version }}。不是当前库存/位置或现场安全确认。</p>
    <button v-if="receipt && kind !== 'dimensions'" type="button" :disabled="!online || !readable || reading" data-test="read-stock-resource" @click="read(receipt.id)">另读当前{{ resourceTitles[kind] }}</button>
    <p v-if="receipt && kind === 'dimensions'">请另从库存查询按仓/位/物料/批次/容器核对当前余额；此原版本不会随记账变化。</p>
    <button v-if="['confirmed', 'rejected'].includes(phase)" type="button" :disabled="uncertain || reading" @click="next">开始下一项登记</button>
    <form v-if="kind !== 'dimensions' && phase !== 'invalidated'" data-test="stock-lookup" @submit.prevent="read(lookupId)">
      <label :for="`${label}-lookup`">读取已登记资源 UUID</label>
      <input :id="`${label}-lookup`" v-model="lookupId" name="stockLookupId" :disabled="!online || reading || !['draft', 'confirmed', 'rejected'].includes(phase)" />
      <button type="submit" :disabled="!online || reading || !readable || !['draft', 'confirmed', 'rejected'].includes(phase)">读取当前{{ resourceTitles[kind] }}</button>
    </form>
    <article v-if="details" data-test="stock-resource-details">
      <h3>当前读取已确认 · {{ resourceTitles[details.kind] }}</h3>
      <dl><template v-for="(value, key) in details.definition" :key="key"><dt>{{ key }}</dt><dd>{{ value }}</dd></template></dl>
    </article>
    <button type="button" :disabled="phase === 'submitting' || reading" @click="emit('close')">关闭资源面板</button>
  </section>
</template>

<style scoped>
.stock-resources { min-width: 0; overflow-wrap: anywhere; }
form { display: grid; gap: var(--wes-space-2); margin-block: var(--wes-space-4); }
input, select, button { max-width: 100%; min-width: 0; }
pre { white-space: pre-wrap; overflow-wrap: anywhere; }
dl { display: grid; grid-template-columns: minmax(0, 1fr) minmax(0, 3fr); gap: var(--wes-space-2); }
dd { margin: 0; }
@media (max-width: 40rem) { dl { grid-template-columns: minmax(0, 1fr); } }
</style>
