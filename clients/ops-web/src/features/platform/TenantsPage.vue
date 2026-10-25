<script setup lang="ts">
import { computed, onBeforeUnmount, reactive, ref, watch } from "vue";
import ColumnSettings from "../../components/ColumnSettings.vue";
import QueryState from "../../components/QueryState.vue";
import { useColumns } from "../../components/useColumns";
import { api, ApiError, canRead, forgetIdentity, sessionState } from "../identity/identity-api";

interface TenantRow { tenantId: string; name: string | null; enabled: boolean | null; defaultLanguage: string | null; version: string | null }
interface Page { authorized: boolean; rows: TenantRow[] }
/** Parent must navigate to target-tenant authentication, never carry the old access token forward. */
const emit = defineEmits<{ reauthenticate: [tenantId: string] }>();
const rows = ref<TenantRow[]>([]);
const loading = ref(false);
const busy = ref(false);
const online = ref(navigator.onLine);
const error = ref("");
const queryError = ref("");
const message = ref("");
const offset = ref(0);
const filters = reactive({ tenantId: "", enabled: "" });
const readFields = computed(() => new Set(sessionState.subject?.grants.filter(grant =>
  grant.permission === "platform.tenants.read" && grant.scope === 2).flatMap(grant => grant.fields)));
const editor = ref<TenantRow | null>(null);
const switchTarget = ref<TenantRow | null>(null);
const form = reactive({ name: "", enabled: true, defaultLanguage: "" });
let generation = 0;
let controller: AbortController | undefined;
const lastSuccessfulQuery = ref<string | null>(null);
const hasIdentity = computed(() => sessionState.subject !== null && sessionState.token !== null);
function queryFingerprint() {
  return JSON.stringify({
    tenantId: sessionState.subject?.tenantId,
    userId: sessionState.subject?.userId,
    grants: sessionState.subject?.grants,
    offset: offset.value,
    filters,
  });
}
const currentQuery = computed(queryFingerprint);
const queryState = computed(() => loading.value
  ? rows.value.length ? "stale" : "loading"
  : queryError.value ? rows.value.length ? "stale" : "error"
    : rows.value.length || error.value ? "ready" : "empty");
const queryStale = computed(() => loading.value || queryError.value.length > 0);
const emptyMessage = computed(() => lastSuccessfulQuery.value !== currentQuery.value
  ? "筛选条件已更改，请提交查询以读取对应租户。"
  : filters.tenantId || filters.enabled ? "没有匹配租户，请清除筛选条件。" : "没有可读租户。请联系管理员核对租户资格。");
const managementFields = computed(() => new Set(sessionState.subject?.grants.filter(grant =>
  grant.permission === "platform.tenants.write" && grant.scope === 2).flatMap(grant => grant.fields)));
const canWrite = computed(() => ["Name", "Enabled", "DefaultLanguage"].every(field => managementFields.value.has(field)));
const canSwitch = computed(() => sessionState.subject?.grants.some(grant => grant.permission === "platform.tenants.switch"
  && grant.scope === 2 && grant.fields.includes("Id")) ?? false);
const { available, selected, visible, preferenceWarning } = useColumns(() => rows.value.length ? [
  { key: "tenantId", label: "租户标识", required: true },
  ...rows.value.some(row => row.name !== null) ? [{ key: "name", label: "租户名称" }] : [],
  ...rows.value.some(row => row.enabled !== null) ? [{ key: "enabled", label: "状态" }] : [],
  ...rows.value.some(row => row.defaultLanguage !== null) ? [{ key: "defaultLanguage", label: "默认语言" }] : [],
] : [], { pageId: "platform-tenants", tableId: "directory" });
function closeEditor() {
  editor.value = null; Object.assign(form, { name: "", enabled: true, defaultLanguage: "" });
}
function clear(preserveRows = false) {
  generation++; controller?.abort();
  if (!preserveRows) {
    rows.value = []; lastSuccessfulQuery.value = null;
  }
  closeEditor(); switchTarget.value = null;
  loading.value = false; busy.value = false; queryError.value = "";
}
function fail(reason: unknown) {
  if (reason instanceof ApiError && [403, 412, 428].includes(reason.status)) clear();
  error.value = reason instanceof Error ? reason.message : "租户操作失败，请重新读取后检查权限。";
  if (reason instanceof ApiError && [412, 428].includes(reason.status)) error.value = `版本已失效，请重新读取后显式提交。${error.value}`;
}
async function load() {
  if (!hasIdentity.value || !canRead("platform.tenants.read")) return;
  const queryKey = currentQuery.value;
  const preserveRows = rows.value.length > 0 && lastSuccessfulQuery.value === queryKey;
  clear(preserveRows); error.value = ""; queryError.value = ""; message.value = "";
  const current = generation;
  controller = new AbortController(); loading.value = true;
  try {
    const query = new URLSearchParams({ offset: String(offset.value), limit: "50" });
    if (filters.tenantId) query.set("tenantId", filters.tenantId);
    if (filters.enabled && readFields.value.has("Enabled")) query.set("enabled", filters.enabled);
    const response = await api<Page>(`/api/platform/tenants?${query}`, { signal: controller.signal });
    if (current !== generation) return;
    if (response.authorized !== true || !Array.isArray(response.rows)) throw new ApiError(403, "DirectoryDenied", "未提供");
    rows.value = response.rows;
    lastSuccessfulQuery.value = queryKey;
  } catch (reason) {
    if (current === generation) {
      fail(reason);
      queryError.value = error.value;
    }
  }
  finally { if (current === generation) loading.value = false; }
}
function open(row: TenantRow) {
  if (busy.value || queryStale.value || row.tenantId !== sessionState.subject?.tenantId) return;
  switchTarget.value = null; editor.value = { ...row };
  Object.assign(form, { name: row.name ?? "", enabled: row.enabled === true, defaultLanguage: row.defaultLanguage ?? "" });
  error.value = ""; message.value = "";
}
async function submit() {
  if (busy.value) return;
  if (!online.value) { error.value = "当前离线，未发送写入。恢复连接后请重新读取。"; return; }
  const row = editor.value;
  if (!canWrite.value || !row?.version || row.tenantId !== sessionState.subject?.tenantId
    || !form.name.trim() || !/^[a-zA-Z]{2,8}(-[a-zA-Z0-9]{1,8})*$/.test(form.defaultLanguage)) {
    error.value = "请填写租户名称和有效语言标签，并确认当前版本及写入权限。"; return;
  }
  const current = generation;
  const identity = sessionState.subject;
  busy.value = true; error.value = "";
  try {
    const result = await api<{ decision: number }>("/api/platform/tenants/current", { method: "PUT",
      body: JSON.stringify({ expectedVersion: row.version, name: form.name.trim(), enabled: form.enabled, defaultLanguage: form.defaultLanguage }),
      signal: controller?.signal });
    if (current !== generation) return;
    if (result.decision !== 0) throw new Error("租户写入未被接受，请重新读取后检查输入。");
    if (!form.enabled) { forgetIdentity(); message.value = "租户已停用。已清除身份，请重新登录。"; return; }
    await load(); if (identity === sessionState.subject) message.value = "租户已保存并重新读取。";
  } catch (reason) { if (current === generation) fail(reason); }
  finally { if (current === generation) busy.value = false; }
}
function chooseSwitch(row: TenantRow) {
  if (busy.value || queryStale.value) return;
  closeEditor(); switchTarget.value = { ...row }; error.value = ""; message.value = "";
}
async function confirmSwitch() {
  if (busy.value) return;
  const target = switchTarget.value;
  if (!target || !canSwitch.value || target.tenantId === sessionState.subject?.tenantId || target.enabled !== true) {
    error.value = "目标租户不可切换，请重新读取。"; return;
  }
  if (!online.value) { error.value = "当前离线，未发送切换检查。"; return; }
  const current = generation;
  busy.value = true; error.value = "";
  try {
    const response = await api<{ authorized: boolean; tenantId: string | null }>("/api/platform/tenants/switch-check",
      { method: "POST", body: JSON.stringify({ targetTenantId: target.tenantId }), signal: controller?.signal });
    if (current !== generation) return;
    if (response.authorized !== true || response.tenantId !== target.tenantId)
      throw new ApiError(403, "TenantSwitchDenied", "未提供");
    // Eligibility is not authentication. Forgetting the identity also invalidates all outstanding API generations.
    const tenantId = target.tenantId;
    clear(); forgetIdentity();
    message.value = "切换资格已确认。旧身份和编辑状态已清除，请为目标租户重新登录。";
    emit("reauthenticate", tenantId);
  } catch (reason) { if (current === generation) fail(reason); }
  finally { if (current === generation) busy.value = false; }
}
function page(delta: number) { if (busy.value || loading.value) return; offset.value = Math.max(0, offset.value + delta); void load(); }
function connectivity() {
  online.value = navigator.onLine;
  if (!online.value) {
    clear();
    error.value = "当前离线，已清除租户编辑和切换选择。恢复连接后请重新读取。";
    queryError.value = error.value;
  }
}
window.addEventListener("online", connectivity); window.addEventListener("offline", connectivity);
watch(filters, () => {
  if (lastSuccessfulQuery.value !== currentQuery.value) {
    rows.value = [];
    lastSuccessfulQuery.value = null;
    queryError.value = "";
    error.value = "";
  }
}, { flush: "sync" });
watch([() => sessionState.subject, () => sessionState.token], () => {
  clear(); offset.value = 0; error.value = ""; message.value = "";
  filters.tenantId = ""; filters.enabled = "";
  if (hasIdentity.value) void load();
}, { immediate: true, deep: true, flush: "sync" });
onBeforeUnmount(() => { clear(); window.removeEventListener("online", connectivity); window.removeEventListener("offline", connectivity); });
</script>

<template>
  <section class="wes-page" aria-labelledby="tenants-title" :aria-busy="loading || busy">
    <header><h1 id="tenants-title">租户管理</h1><p class="hint">切换租户需要重新认证。</p></header>
    <p v-if="message" role="status">{{ message }}</p>
    <p v-if="!hasIdentity || !canRead('platform.tenants.read')" role="status">无权读取租户目录，请登录后重新读取。</p>
    <template v-else>
      <p v-if="!online" class="notice" role="status">当前离线，恢复连接后请重新读取。</p>
      <p v-if="error && !queryError" class="error" role="alert">{{ error }}</p>
      <div class="toolbar"><button @click="load">重新读取</button></div>
      <form data-test="tenant-filters" class="toolbar" @submit.prevent="offset = 0; load()">
        <label>租户 UUID<input v-model="filters.tenantId" name="filterTenantId" maxlength="36"></label>
        <label v-if="readFields.has('Enabled')">状态<select v-model="filters.enabled" name="filterTenantEnabled"><option value="">全部</option><option value="true">启用</option><option value="false">停用</option></select></label>
        <button :disabled="busy || loading">查询可访问租户</button>
      </form>
      <QueryState label="租户目录" :state="queryState" :busy="loading" :error-message="queryError"
        :stale-message="loading ? '正在刷新；页面保留的是最近一次已加载的租户目录。' : `刷新失败，仍保留最近一次已加载的租户目录：${queryError}`"
        @retry="load">
        <template #empty><p>{{ emptyMessage }}</p></template>
        <template v-if="rows.length">
          <ColumnSettings :columns="available" v-model="selected" :preference-warning="preferenceWarning" />
          <div class="table-scroll" tabindex="0" aria-label="租户目录">
          <table><caption class="hint">当前页可读租户</caption><thead><tr><th v-for="column in visible" :key="column.key" scope="col">{{ column.label }}</th><th scope="col">操作</th></tr></thead>
            <tbody><tr v-for="row in rows" :key="row.tenantId">
              <td v-for="column in visible" :key="column.key">
                <template v-if="column.key === 'tenantId'">{{ row.tenantId }}<span v-if="row.tenantId === sessionState.subject?.tenantId">（当前）</span></template>
                <template v-else-if="column.key === 'name'">{{ row.name ?? '未授权' }}</template>
                <template v-else-if="column.key === 'enabled'">{{ row.enabled === null ? '未授权' : row.enabled ? '启用' : '停用' }}</template>
                <template v-else-if="column.key === 'defaultLanguage'">{{ row.defaultLanguage ?? '未授权' }}</template>
              </td>
              <td>
                <button v-if="canWrite && row.tenantId === sessionState.subject?.tenantId && row.version && row.name !== null && row.enabled !== null && row.defaultLanguage !== null" data-test="edit-tenant" :disabled="busy || queryStale" @click="open(row)">编辑租户</button>
                <button v-if="canSwitch && row.tenantId !== sessionState.subject?.tenantId && row.enabled === true" data-test="switch-tenant" :disabled="busy || queryStale" @click="chooseSwitch(row)">切换租户</button>
              </td>
            </tr></tbody>
          </table>
        </div>
        </template>
      </QueryState>
      <nav class="actions" aria-label="租户分页"><button v-if="offset > 0" @click="page(-50)">上一页</button><span>第 {{ offset / 50 + 1 }} 页，每页最多 50 条</span><button v-if="rows.length === 50" @click="page(50)">下一页</button></nav>
      <form v-if="editor" data-test="tenant-form" class="editor" @submit.prevent="submit">
        <fieldset :disabled="busy"><legend>当前租户资料</legend>
          <label>租户名称<input v-model="form.name" name="tenantName" required maxlength="128" /></label>
          <label>默认语言<input v-model="form.defaultLanguage" name="defaultLanguage" required maxlength="35" placeholder="zh-CN" /></label>
          <label class="check"><input v-model="form.enabled" type="checkbox" />启用租户</label>
          <p class="notice">停用将阻止该租户访问，并清除当前登录状态。请核对后保存。</p>
          <div class="actions"><button type="button" @click="closeEditor">取消</button><button type="submit" class="primary">{{ busy ? '正在提交' : '确认保存租户' }}</button></div>
        </fieldset>
      </form>
      <form v-if="switchTarget" data-test="switch-form" class="editor" @submit.prevent="confirmSwitch">
        <fieldset :disabled="busy"><legend>租户切换确认</legend>
          <p>目标租户：{{ switchTarget.name ?? switchTarget.tenantId }}。成功检查后会清除旧身份、目录数据和未保存编辑，再为目标租户重新登录。不会继续使用旧令牌。</p>
          <div class="actions"><button type="button" @click="switchTarget = null">取消</button><button type="submit" data-test="confirm-switch" class="primary">{{ busy ? '正在检查' : '确认检查并重新登录' }}</button></div>
        </fieldset>
      </form>
    </template>
  </section>
</template>

<style scoped>
section, .editor { min-width: 0; }
.check { display: flex; min-height: 44px; align-items: center; gap: .5rem; margin: 0; }
.check input { width: 20px; min-width: 20px; min-height: 20px; }
button { white-space: nowrap; }
</style>
