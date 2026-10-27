<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, shallowRef, watch } from "vue";
import QueryState from "../../components/QueryState.vue";
import MasterDataCatalog from "./MasterDataCatalog.vue";
import MasterDataCreation from "./MasterDataCreation.vue";
import EntityAttachments from "./EntityAttachments.vue";
import WarehouseStateChange from "./WarehouseStateChange.vue";
import { mayChangeWarehouseState } from "./warehouse-state";
import { ApiError, sessionState } from "../identity/identity-api";
import { locationFields, MasterDataResponseError, mayCreateLocation, mayCreateWarehouse, readMasterData, validIdentity, warehouseFields, type MasterData, type ResourceKind } from "./master-data";
import { mayReadBusinessFiles } from "./business-files";

const resourceKind = ref<ResourceKind>(mayRead("warehouses") ? "warehouses" : "locations");
const resourceId = ref("");
const catalogRevision = ref(0);
const creationOpen = ref<ResourceKind | null>(null);
const stateOpen = shallowRef<Extract<MasterData, { kind: "warehouses" }>>();
const canChangeState = computed(mayChangeWarehouseState);
const canCreate = computed(mayCreateWarehouse); const canCreateLocation = computed(mayCreateLocation);
const record = shallowRef<MasterData | null>(null);
const busy = ref(false); const fresh = ref(false); const error = ref(""); const notFound = ref(false);
const loadedAt = ref(""); const online = ref(navigator.onLine);
const attachmentLocked = ref(false);
const scope = computed(() => JSON.stringify(sessionState.subject));
const readable = computed(() => mayRead(resourceKind.value));
const businessFileReadable = computed(() => !!record.value && mayReadBusinessFiles(record.value.kind));
const queryState = computed(() => busy.value ? record.value ? "stale" : "loading"
  : record.value ? fresh.value ? "ready" : "stale" : error.value ? "error" : "empty");
let controller = new AbortController(); let sequence = 0;
function mayRead(kind: ResourceKind) {
  const fields = new Set(sessionState.subject?.grants.filter(grant => grant.permission === `wms.${kind}.read`).flatMap(grant => grant.fields));
  return !!sessionState.subject && (kind === "warehouses" ? warehouseFields : locationFields).every(field => fields.has(field));
}
function cancel() {
  ++sequence; controller.abort(); controller = new AbortController(); busy.value = false; fresh.value = false;
}
function reset() {
  cancel(); record.value = null; loadedAt.value = ""; error.value = ""; notFound.value = false;
}
watch([resourceKind, resourceId], reset, { flush: "sync" });
watch(scope, () => { reset(); resourceId.value = ""; stateOpen.value = undefined; }, { flush: "sync" });
function connection() {
  online.value = navigator.onLine;
  if (!online.value) cancel();
}
onMounted(() => { addEventListener("online", connection); addEventListener("offline", connection); });
onUnmounted(() => { cancel(); removeEventListener("online", connection); removeEventListener("offline", connection); });
async function read() {
  if (attachmentLocked.value || stateOpen.value || !readable.value || !online.value || busy.value || creationOpen.value) return;
  const id = resourceId.value.trim();
  if (!validIdentity(id)) { error.value = "请输入有效的非空 UUID。"; return; }
  cancel(); busy.value = true; error.value = ""; notFound.value = false;
  const ticket = sequence; const signal = controller.signal; const fingerprint = scope.value; const kind = resourceKind.value;
  const current = () => ticket === sequence && fingerprint === scope.value && !signal.aborted;
  try {
    const value = await readMasterData(kind, id, signal);
    if (current()) { record.value = value; fresh.value = true; loadedAt.value = new Date().toISOString(); }
  } catch (failure) {
    if (!current()) return;
    if (failure instanceof ApiError && failure.status === 404) {
      record.value = null; loadedAt.value = ""; notFound.value = true;
    } else {
      if (failure instanceof MasterDataResponseError || failure instanceof ApiError && (failure.status === 401 || failure.status === 403)) {
        record.value = null; loadedAt.value = "";
        ++catalogRevision.value;
      }
      error.value = failure instanceof Error ? failure.message : "主数据读取未确认，请显式重试。";
    }
  } finally { if (ticket === sequence) busy.value = false; }
}
function selectCatalogResource(id: string) {
  if (attachmentLocked.value || stateOpen.value) return;
  reset(); resourceId.value = id; void read();
}
function creationAttempted() { reset(); ++catalogRevision.value; }
function openCreation(kind: ResourceKind) {
  if (attachmentLocked.value || stateOpen.value) return;
  creationAttempted(); creationOpen.value = kind;
}
function readCreatedDefinition(id: string) {
  if (!creationOpen.value) return;
  resourceKind.value = creationOpen.value; creationOpen.value = null; selectCatalogResource(id);
}
function attachLocked(value: boolean) { attachmentLocked.value = value; }
function openState() {
  if (attachmentLocked.value || creationOpen.value || !fresh.value || busy.value || !online.value
    || !canChangeState.value || record.value?.kind !== "warehouses") return;
  stateOpen.value = record.value;
  reset();
}
function readStateWarehouse(id: string) { stateOpen.value = undefined; resourceKind.value = "warehouses"; selectCatalogResource(id); }
</script>

<template>
  <section class="page" aria-labelledby="master-data-heading">
    <header class="page-heading"><div><p class="eyebrow">WMS · 主数据定义</p><h1 id="master-data-heading">仓库与库位详情</h1></div></header>
    <p class="hint">通过实时目录或已知 UUID 读取当前授权的主数据。不代表可分配、位置空闲或物理安全许可；创建及仓库逻辑启停须独立部署装配，层级树和库位启停尚未开放。</p>
    <button v-if="canCreate && !creationOpen && !stateOpen" type="button" :disabled="attachmentLocked" @click="openCreation('warehouses')">新建禁用仓库</button>
    <button v-if="canCreateLocation && !creationOpen && !stateOpen" type="button" :disabled="attachmentLocked" @click="openCreation('locations')">新建禁用库位</button>
    <MasterDataCreation v-if="creationOpen" :kind="creationOpen" @attempted="creationAttempted" @read="readCreatedDefinition" @close="creationOpen = null" />
    <WarehouseStateChange v-if="stateOpen" :warehouse="stateOpen" @attempted="creationAttempted" @read="readStateWarehouse" @close="stateOpen = undefined" />
    <template v-if="!creationOpen">
    <form class="master-data-query" @submit.prevent="read">
      <div class="master-data-kind"><label for="master-data-kind">资源类型</label>
        <select id="master-data-kind" v-model="resourceKind" name="resourceKind" :disabled="attachmentLocked || !!stateOpen"><option value="warehouses">仓库</option><option value="locations">库位</option></select></div>
      <div class="master-data-id"><label for="master-data-id">资源 UUID</label>
        <input id="master-data-id" v-model="resourceId" name="resourceId" autocomplete="off" spellcheck="false" maxlength="36" :disabled="attachmentLocked || !!stateOpen" /></div>
      <button type="submit" :disabled="!readable || !online || busy || attachmentLocked || !!stateOpen">读取当前详情</button>
    </form>
    <p v-if="!readable" role="alert" class="error">缺少当前资源类型的完整读取字段权限。菜单可见不等于资源授权。</p>
    <p v-if="!online" role="status" class="notice">当前离线：仅显示已加载的陈旧定义；恢复后请手工重新读取，不排队或重放。</p>
    <p v-if="error && record" role="alert" class="error">{{ error }}</p>
    <MasterDataCatalog v-if="readable" :key="catalogRevision" :kind="resourceKind" :readable="readable && !attachmentLocked && !stateOpen"
      @select="selectCatalogResource" @denied="reset" />
    <QueryState v-if="readable" label="主数据详情" :state="queryState" :busy="busy" :error-message="error"
      :stale-message="`陈旧定义，仅供参考；当前读取未确认。上次成功读取：${loadedAt || '未确认'}`" @retry="read">
      <template #empty>
        <p v-if="notFound" data-test="master-data-not-found">未找到当前租户内的资源，或主数据入口未装配；这不表示系统没有主数据。</p>
        <p v-else>请选择资源类型并输入已知 UUID，再读取当前详情。</p>
      </template>
      <article v-if="record" data-test="master-data-details" :aria-label="record.kind === 'warehouses' ? '仓库定义' : '库位定义'">
        <h2>{{ record.kind === "warehouses" ? "仓库" : "库位" }}定义</h2>
        <button v-if="record.kind === 'warehouses' && canChangeState" type="button" data-test="prepare-warehouse-state"
          :disabled="!fresh || !online || busy || attachmentLocked" @click="openState">准备{{ record.definition.enabled ? "禁用" : "启用" }}仓库</button>
        <p v-if="fresh" role="status">当前读取已确认 · 上次成功读取：<time :datetime="loadedAt">{{ loadedAt }}</time></p>
        <dl class="master-data-definition">
          <div><dt>资源 UUID</dt><dd>{{ record.definition.id }}</dd></div>
          <div><dt>代码</dt><dd>{{ record.definition.code }}</dd></div>
          <div><dt>名称</dt><dd>{{ record.definition.name }}</dd></div>
          <div><dt>主数据状态</dt><dd>{{ record.definition.enabled ? "主数据启用（不代表空闲或可分配）" : "主数据禁用" }}</dd></div>
          <template v-if="record.kind === 'locations'">
            <div><dt>固定仓库 UUID</dt><dd>{{ record.definition.warehouseId }}</dd></div>
            <div><dt>固定 Pack UUID</dt><dd>{{ record.definition.packId }}</dd></div>
            <div><dt>固定 schema</dt><dd>{{ record.definition.schemaVersion }}</dd></div>
          </template>
          <div><dt>版本</dt><dd>{{ record.definition.version }}</dd></div>
          <div><dt>创建时间（UTC）</dt><dd><time :datetime="record.definition.createdAt">{{ record.definition.createdAt }}</time></dd></div>
        </dl>
      </article>
    </QueryState>
    <EntityAttachments v-if="record && businessFileReadable" :key="`${record.kind}:${record.definition.id}`"
      :kind="record.kind" :entity-id="record.definition.id" :active="fresh && online && !busy" @locked="attachLocked" />
    </template>
  </section>
</template>

<style scoped>
.master-data-query { display: flex; flex-wrap: wrap; align-items: end; gap: var(--wes-space-3); margin-block: var(--wes-space-4); }
.master-data-query > div { display: grid; gap: var(--wes-space-2); min-width: 0; max-width: 100%; }
.master-data-id { flex: 1 1 22rem; }
.master-data-query input, .master-data-query select { width: 100%; min-width: 0; box-sizing: border-box; }
.master-data-definition { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(100%, 22rem), 1fr)); gap: var(--wes-space-4); }
.master-data-definition div { min-width: 0; }
.master-data-definition dt { color: var(--wes-color-muted); }
.master-data-definition dd { margin: var(--wes-space-2) 0 0; overflow-wrap: anywhere; }
</style>
