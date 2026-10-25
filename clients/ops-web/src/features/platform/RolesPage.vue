<script setup lang="ts">
import { computed, onBeforeUnmount, reactive, ref, watch } from "vue";
import ColumnSettings from "../../components/ColumnSettings.vue";
import { useColumns } from "../../components/useColumns";
import { api, ApiError, canRead, sessionState, type Grant } from "../identity/identity-api";
import { useDirectoryCandidates } from "./useDirectoryCandidates";

interface RoleRow { id: string; code: string | null; enabled: boolean | null; version: string | null; rules: Grant[] | null; affectedSubjects: number | null }
interface DepartmentRow { id: string; name: string | null; enabled: boolean | null }
interface Page<T> { authorized: boolean; rows: T[] }
const rows = ref<RoleRow[]>([]);
const departmentCatalog = useDirectoryCandidates<DepartmentRow>("/api/platform/departments");
const { rows: departments, more: moreDepartments } = departmentCatalog;
const loading = ref(false);
const busy = ref(false);
const online = ref(navigator.onLine);
const error = ref("");
const candidateError = ref("");
const message = ref("");
const offset = ref(0);
const filters = reactive({ code: "", permission: "" });
const readFields = computed(() => new Set(sessionState.subject?.grants.filter(grant =>
  grant.permission === "platform.roles.read" && grant.scope === 2).flatMap(grant => grant.fields)));
const editor = ref<RoleRow | "new" | null>(null);
const form = reactive({ code: "", enabled: true, rules: [] as Grant[] });
const draft = reactive({ permission: "", scope: 0, departments: [] as string[], fields: [] as string[] });
let generation = 0;
let controller: AbortController | undefined;
const hasIdentity = computed(() => sessionState.subject !== null && sessionState.token !== null);
const delegable = computed(() => (sessionState.subject?.grants ?? []).filter(grant =>
  /^[a-z][a-z0-9]*(\.[a-z][a-z0-9]*)+$/.test(grant.permission) && grant.permission.length <= 128
  && [0, 1, 2].includes(grant.scope) && grant.fields.every(field => /^[a-zA-Z][a-zA-Z0-9]{0,63}$/.test(field))));
const permissions = computed(() => [...new Set(delegable.value.map(grant => grant.permission))]);
const sources = computed(() => delegable.value.filter(grant => grant.permission === draft.permission));
const scopes = computed(() => sources.value.some(grant => grant.scope === 2) ? [0, 1, 2]
  : [...new Set(sources.value.map(grant => grant.scope))]);
const scopeSources = computed(() => sources.value.filter(grant => grant.scope === 2 || grant.scope === draft.scope));
const fields = computed(() => [...new Set(scopeSources.value.flatMap(grant => grant.fields))].sort());
const departmentCandidates = computed(() => departments.value.filter(row => row.enabled === true
  && scopeSources.value.some(grant => grant.scope === 2 || grant.scope === 1 && grant.departments.includes(row.id))));
const managementFields = computed(() => new Set(sessionState.subject?.grants.filter(grant =>
  grant.permission === "platform.roles.write" && grant.scope === 2).flatMap(grant => grant.fields)));
const canWrite = computed(() => ["Rules", "Enabled"].every(field => managementFields.value.has(field)));
const canCreate = computed(() => ["Code", "Rules", "Enabled"].every(field => managementFields.value.has(field)));
const { available, selected, visible, preferenceWarning } = useColumns(() => rows.value.length ? [
  { key: "id", label: "角色标识", required: true },
  ...rows.value.some(row => row.code !== null) ? [{ key: "code", label: "角色编码" }] : [],
  ...rows.value.some(row => row.enabled !== null) ? [{ key: "enabled", label: "状态" }] : [],
  ...rows.value.some(row => row.rules !== null) ? [{ key: "rules", label: "权限规则" }] : [],
  ...readFields.value.has("AffectedSubjects") && rows.value.some(row => row.affectedSubjects != null)
    ? [{ key: "affectedSubjects", label: "关联用户数（查询快照）" }] : [],
] : [], { pageId: "platform-roles", tableId: "directory" });
function scopeName(scope: number) { return scope === 0 ? "本人" : scope === 1 ? "指定部门" : scope === 2 ? "当前租户全部" : "无效范围"; }
function resetDraft() { Object.assign(draft, { permission: "", scope: 0, departments: [], fields: [] }); }
watch(() => draft.permission, () => {
  draft.fields = []; draft.departments = []; draft.scope = scopes.value[0] ?? 0;
});
watch(() => draft.scope, () => { draft.fields = []; draft.departments = []; });
function closeEditor() {
  editor.value = null; Object.assign(form, { code: "", enabled: true, rules: [] }); resetDraft();
}
function clear() {
  generation++; controller?.abort(); rows.value = []; departmentCatalog.reset();
  closeEditor(); loading.value = false; busy.value = false;
}
function fail(reason: unknown) {
  if (reason instanceof ApiError && [403, 412, 428].includes(reason.status)) clear();
  error.value = reason instanceof Error ? reason.message : "角色操作失败，请重新读取后检查权限。";
  if (reason instanceof ApiError && [412, 428].includes(reason.status)) error.value = `版本已失效，请重新读取后显式提交。${error.value}`;
}
async function load() {
  if (!hasIdentity.value || !canRead("platform.roles.read")) return;
  clear(); error.value = ""; candidateError.value = ""; message.value = "";
  const current = generation;
  controller = new AbortController();
  const signal = controller.signal;
  loading.value = true;
  const candidateLoad = (async () => {
    if (!canRead("platform.departments.read")) return;
    try {
      const response = await departmentCatalog.read(signal);
      if (current !== generation) return;
      if (response?.authorized === true && Array.isArray(response.rows)) departments.value = response.rows;
      else candidateError.value = "无权读取部门候选。指定部门规则不能提交。";
    } catch (reason) {
      if (current === generation) candidateError.value = `部门候选读取失败，请重新读取。${reason instanceof Error ? reason.message : ""}`;
    }
  })();
  try {
    const query = new URLSearchParams({ offset: String(offset.value), limit: "50" });
    if (filters.code && readFields.value.has("Code")) query.set("code", filters.code.trim());
    if (filters.permission && readFields.value.has("Rules")) query.set("permission", filters.permission.trim());
    const response = await api<Page<RoleRow>>(`/api/platform/roles?${query}`, { signal });
    if (current !== generation) return;
    if (response.authorized !== true || !Array.isArray(response.rows)) throw new ApiError(403, "DirectoryDenied", "未提供");
    rows.value = response.rows;
  } catch (reason) { if (current === generation) fail(reason); }
  finally { await candidateLoad; if (current === generation) loading.value = false; }
}
async function moreCandidates() {
  if (busy.value || loading.value || !controller) return;
  const current = generation; candidateError.value = "";
  try { await departmentCatalog.read(controller.signal, true); }
  catch (reason) {
    if (current === generation) candidateError.value = `后续部门候选读取失败，请重试。${reason instanceof Error ? reason.message : ""}`;
  }
}
function covered(rule: Grant) {
  return delegable.value.some(source => source.permission === rule.permission
    && rule.fields.every(field => source.fields.includes(field))
    && (source.scope === 2 || source.scope === 0 && rule.scope === 0
      || source.scope === 1 && rule.scope === 1 && rule.departments.every(id => source.departments.includes(id))));
}
function open(row?: RoleRow) {
  if (busy.value) return;
  closeEditor();
  editor.value = row ? { ...row } : "new";
  Object.assign(form, { code: row?.code ?? "", enabled: row?.enabled ?? true,
    rules: row?.rules?.map(rule => ({ ...rule, departments: [...rule.departments], fields: [...rule.fields] })) ?? [] });
  error.value = ""; message.value = "";
}
function addRule() {
  const rule: Grant = { permission: draft.permission, scope: Number(draft.scope),
    departments: Number(draft.scope) === 1 ? [...draft.departments] : [], fields: [...draft.fields] };
  if (!permissions.value.includes(rule.permission) || !scopes.value.includes(rule.scope) || !rule.fields.length
    || rule.scope === 1 && (!rule.departments.length || rule.departments.some(id => !departmentCandidates.value.some(row => row.id === id)))
    || !covered(rule)) {
    error.value = "规则无效：请选择可委派权限、范围、至少一个字段；指定部门须选择启用且可委派的部门。"; return;
  }
  form.rules.push(rule); resetDraft(); error.value = "";
}
async function submit() {
  if (busy.value) return;
  if (!online.value) { error.value = "当前离线，未发送写入。恢复连接后请重新读取。"; return; }
  const row = editor.value;
  if (!row || row === "new" && (!form.code.trim() || !canCreate.value)
    || row !== "new" && (!row.version || row.rules === null || !canWrite.value)) {
    error.value = "需要当前版本、角色编码及角色写入权限。"; return;
  }
  if (draft.permission) { error.value = "尚有未加入的权限规则，请加入规则或清除草稿。"; return; }
  if (form.rules.some(rule => !covered(rule) || ![0, 1, 2].includes(rule.scope)
    || rule.scope !== 1 && rule.departments.length > 0 || rule.scope === 1 && rule.departments.length === 0)) {
    error.value = "规则超出当前可委派权限，未发送。请重新读取并检查每条规则。"; return;
  }
  const current = generation;
  const identity = sessionState.subject;
  busy.value = true; error.value = ""; message.value = "";
  try {
    const body = row === "new" ? { code: form.code.trim(), rules: form.rules }
      : { expectedVersion: row.version, enabled: form.enabled, rules: form.rules };
    const result = await api<{ decision: number }>(row === "new" ? "/api/platform/roles" : `/api/platform/roles/${encodeURIComponent(row.id)}`,
      { method: row === "new" ? "POST" : "PUT", body: JSON.stringify(body), signal: controller?.signal });
    if (current !== generation) return;
    if (result.decision !== 0) throw new Error("角色写入未被接受，请重新读取后检查规则。");
    await load();
    if (identity === sessionState.subject) message.value = "角色已保存并重新读取。";
  } catch (reason) { if (current === generation) fail(reason); }
  finally { if (current === generation) busy.value = false; }
}
function page(delta: number) { if (busy.value || loading.value) return; offset.value = Math.max(0, offset.value + delta); void load(); }
function connectivity() {
  online.value = navigator.onLine;
  if (!online.value) { clear(); error.value = "当前离线，已清除规则草稿。恢复连接后请重新读取。"; }
}
window.addEventListener("online", connectivity); window.addEventListener("offline", connectivity);
watch([() => sessionState.subject, () => sessionState.token], () => {
  clear(); offset.value = 0; error.value = ""; message.value = ""; candidateError.value = "";
  filters.code = ""; filters.permission = "";
  if (hasIdentity.value) void load();
}, { immediate: true, deep: true, flush: "sync" });
onBeforeUnmount(() => { clear(); window.removeEventListener("online", connectivity); window.removeEventListener("offline", connectivity); });
</script>

<template>
  <section class="wes-page" aria-labelledby="roles-title" :aria-busy="loading || busy">
    <header><h1 id="roles-title">角色与权限</h1><p class="hint">仅可委派当前持有的范围与字段。</p></header>
    <p v-if="!hasIdentity || !canRead('platform.roles.read')" role="status">无权读取角色目录，请使用有权账号登录。</p>
    <template v-else>
      <p v-if="!online" class="notice" role="status">当前离线，恢复连接后请重新读取。</p>
      <p v-if="error" class="error" role="alert">{{ error }}</p>
      <p v-if="candidateError" class="notice" role="alert">{{ candidateError }}</p>
      <p v-if="message" role="status">{{ message }}</p>
      <div class="toolbar"><button @click="load">重新读取</button><button v-if="canCreate" data-test="new-role" @click="open()">新建角色</button></div>
      <form data-test="role-filters" class="toolbar" @submit.prevent="offset = 0; load()">
        <label v-if="readFields.has('Code')">角色编码（精确）<input v-model="filters.code" name="filterRoleCode" maxlength="128"></label>
        <label v-if="readFields.has('Rules')">权限键（精确）<input v-model="filters.permission" name="filterRolePermission" maxlength="128"></label>
        <button :disabled="busy || loading">查询角色</button>
      </form>
      <button v-if="moreDepartments" type="button" data-test="more-departments" :disabled="loading || busy || departmentCatalog.loading.value" @click="moreCandidates">加载更多部门候选</button>
      <p v-if="loading" role="status">正在读取角色与部门候选。</p>
      <template v-if="rows.length">
        <ColumnSettings :columns="available" v-model="selected" :preference-warning="preferenceWarning" />
        <div class="table-scroll" tabindex="0" aria-label="角色目录">
          <table><caption class="hint">当前页角色</caption><thead><tr><th v-for="column in visible" :key="column.key" scope="col">{{ column.label }}</th><th scope="col">操作</th></tr></thead>
            <tbody><tr v-for="row in rows" :key="row.id">
              <td v-for="column in visible" :key="column.key">
                <template v-if="column.key === 'id'">{{ row.id }}</template>
                <template v-else-if="column.key === 'code'">{{ row.code ?? '未授权' }}</template>
                <template v-else-if="column.key === 'enabled'">{{ row.enabled === null ? '未授权' : row.enabled ? '启用' : '停用' }}</template>
                <template v-else-if="column.key === 'affectedSubjects'">{{ row.affectedSubjects ?? '未授权' }}</template>
                <template v-else-if="column.key === 'rules'">
                  <span v-if="row.rules === null">未授权</span><span v-else-if="!row.rules.length">无权限规则</span>
                  <details v-else><summary>{{ row.rules.length }} 条规则</summary><ul><li v-for="(rule, index) in row.rules" :key="index">
                    {{ rule.permission }} · {{ scopeName(rule.scope) }} · 字段：{{ rule.fields.join('、') || '无' }}
                    <span v-if="rule.scope === 1"> · 部门：{{ rule.departments.map(id => departments.find(row => row.id === id)?.name ?? id).join('、') || '无' }}</span>
                  </li></ul></details>
                </template>
              </td>
              <td><button v-if="canWrite && row.version && row.enabled !== null && row.rules !== null" data-test="edit-role" @click="open(row)">编辑权限</button><span v-else>只读</span></td>
            </tr></tbody>
          </table>
        </div>
      </template>
      <p v-else-if="!loading && !error" role="status">尚无可读角色，可新建无默认权限的角色。</p>
      <nav class="actions" aria-label="角色分页"><button v-if="offset > 0" @click="page(-50)">上一页</button><span>第 {{ offset / 50 + 1 }} 页，每页最多 50 条</span><button v-if="rows.length === 50" @click="page(50)">下一页</button></nav>
      <form v-if="editor" data-test="role-form" class="editor" @submit.prevent="submit">
        <fieldset :disabled="busy"><legend>{{ editor === 'new' ? '新建角色' : '角色权限' }}</legend>
          <label v-if="editor === 'new'">角色编码<input v-model="form.code" name="roleCode" required maxlength="64" /></label>
          <p v-else>角色：{{ form.code || editor.id }}</p>
          <section v-if="editor !== 'new'" data-test="role-impact" aria-label="角色变更影响预览">
            <p>当前选定版本：{{ editor.version }}；关联用户数：{{ readFields.has('AffectedSubjects') ? editor.affectedSubjects ?? '未授权' : '未授权' }}（查询快照，可能随并发变化）。</p>
            <p>保存后本服务在后续请求中复验当前权限；不承诺其他系统瞬时撤权。提交时重新验证版本和可委派范围。</p>
          </section>
          <label v-if="editor !== 'new'" class="check"><input v-model="form.enabled" type="checkbox" />启用角色</label>
          <h2>权限规则</h2>
          <p v-if="!form.rules.length" class="hint">当前没有权限规则。保存空规则不会授予任何权限。</p>
          <ol><li v-for="(rule, index) in form.rules" :key="index">{{ rule.permission }} · {{ scopeName(rule.scope) }} · {{ rule.fields.join('、') }}
            <span v-if="rule.scope === 1"> · {{ rule.departments.join('、') }}</span>
            <button type="button" :aria-label="`移除第${index + 1}条规则`" @click="form.rules.splice(index, 1)">移除规则</button>
          </li></ol>
          <fieldset><legend>新增权限规则</legend>
            <label>权限<select v-model="draft.permission" name="permission"><option value="">选择可委派权限</option><option v-for="permission in permissions" :key="permission" :value="permission">{{ permission }}</option></select></label>
            <label v-if="draft.permission">数据范围<select v-model.number="draft.scope" name="scope"><option v-for="scope in scopes" :key="scope" :value="scope">{{ scopeName(scope) }}</option></select></label>
            <fieldset v-if="draft.permission && draft.scope === 1"><legend>启用部门（当前已加载页）</legend>
              <label v-for="department in departmentCandidates" :key="department.id" class="check"><input v-model="draft.departments" name="grantDepartment" type="checkbox" :value="department.id" />{{ department.name ?? department.id }}</label>
              <p v-if="!departmentCandidates.length">没有已加载的可委派启用部门，请重新读取或选择其他范围。</p>
            </fieldset>
            <fieldset v-if="draft.permission"><legend>可委派字段</legend><label v-for="field in fields" :key="field" class="check"><input v-model="draft.fields" type="checkbox" name="grantField" :value="field" />{{ field }}</label></fieldset>
            <div class="actions"><button type="button" data-test="add-rule" @click="addRule">加入规则</button><button type="button" @click="resetDraft">清除规则草稿</button></div>
          </fieldset>
          <p class="notice">保存会替换该角色的全部规则，并立即影响关联用户。请核对后确认。</p>
          <div class="actions"><button type="button" @click="closeEditor">取消</button><button type="submit" class="primary">{{ busy ? '正在提交' : '确认保存角色' }}</button></div>
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
summary { cursor: pointer; min-height: 44px; }
li { overflow-wrap: anywhere; margin-block: .5rem; }
</style>
