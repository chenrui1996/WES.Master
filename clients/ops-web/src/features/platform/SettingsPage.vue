<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, watch } from "vue";
import ColumnSettings from "../../components/ColumnSettings.vue";
import QueryState from "../../components/QueryState.vue";
import { useColumns } from "../../components/useColumns";
import { ApiError, api, canRead, isRecord, sessionState } from "../identity/identity-api";
interface SettingRow { id: string; key: string | null; value: string | null; source: string | null; version: string | null; kind: string | null; state: string | null }
interface DictionaryRow { id: string; category: string | null; code: string | null; languageKey: string | null; label: string | null; resolvedLanguage: string | null; missingTranslation: boolean | null; version: string | null }
const rows = ref<SettingRow[]>([]); const entries = ref<DictionaryRow[]>([]);
const error = ref(""); const message = ref(""); const busy = ref(false);
const settingsLoading = ref(false); const settingsQueryError = ref("");
const dictionaryLoading = ref(false); const dictionaryQueryError = ref("");
const selected = ref<SettingRow | null>(null); const value = ref("");
const category = ref("task"); const language = ref("zh-CN"); const offset = ref(0);
const kind = ref(""); const settingOffset = ref(0); const missingTranslation = ref("");
const entry = ref<DictionaryRow | null>(null); const label = ref(""); const deletePending = ref(false);
const code = ref(""); const languageKey = ref("");
let controller = new AbortController(); let readSequence = 0; let settingsSequence = 0;
let successfulSettingsFingerprint: string | null = null;
let successfulDictionaryFingerprint: string | null = null;
const settingsFingerprint = computed(() => JSON.stringify({
  tenantId: sessionState.subject?.tenantId,
  userId: sessionState.subject?.userId,
  grants: sessionState.subject?.grants,
  kind: kind.value,
  offset: settingOffset.value,
}));
const settingsQueryState = computed(() => settingsLoading.value
  ? rows.value.length ? "stale" : "loading"
  : settingsQueryError.value ? rows.value.length ? "stale" : "error"
  : rows.value.length ? "ready" : "empty");
const settingsStaleMessage = computed(() => settingsLoading.value
  ? "正在刷新；页面保留的是最近一次已加载的配置目录。"
  : `刷新失败，仍保留最近一次已加载的配置目录：${settingsQueryError.value}`);
const dictionaryFingerprint = computed(() => JSON.stringify({
  tenantId: sessionState.subject?.tenantId,
  userId: sessionState.subject?.userId,
  grants: sessionState.subject?.grants,
  category: category.value,
  language: language.value,
  offset: offset.value,
  missingTranslation: missingTranslation.value,
}));
const dictionaryQueryState = computed(() => dictionaryLoading.value
  ? entries.value.length ? "stale" : "loading"
  : dictionaryQueryError.value ? entries.value.length ? "stale" : "error"
  : entries.value.length ? "ready" : "empty");
const dictionaryStaleMessage = computed(() => dictionaryLoading.value
  ? "正在刷新；页面保留的是最近一次已加载的字典目录。"
  : `刷新失败，仍保留最近一次已加载的字典目录：${dictionaryQueryError.value}`);
const fields = computed(() => new Set(sessionState.subject?.grants.filter(grant => grant.permission === "platform.settings.read").flatMap(grant => grant.fields) ?? []));
const { available, selected: selectedColumns, visible, widths: settingWidths, widthStyle: settingWidthStyle,
  fixedFirstColumn: settingsFixedFirstColumn, preferenceWarning } = useColumns(() => [
  { key: "id", label: "标识", field: "Id", required: true, widthClass: "identity" as const },
  { key: "key", label: "稳定键", field: "Key", widthClass: "identity" as const },
  { key: "value", label: "当前值", field: "Value", widthClass: "description" as const },
  { key: "source", label: "来源", field: "Source", widthClass: "identity" as const },
  { key: "version", label: "版本", field: "Version", widthClass: "identity" as const },
].filter(column => fields.value.has(column.field)), { pageId: "platform-settings", tableId: "settings" });
const dictionaryFields = computed(() => new Set(sessionState.subject?.grants.filter(grant => grant.permission === "platform.dictionary.read").flatMap(grant => grant.fields) ?? []));
const dictColumns = useColumns(() => [
  { key: "id", label: "标识", field: "Id", required: true, widthClass: "identity" as const },
  { key: "code", label: "稳定业务码", field: "Code", widthClass: "identity" as const },
  { key: "languageKey", label: "语言键", field: "LanguageKey", widthClass: "identity" as const },
  { key: "label", label: "显示文本", field: "Label", widthClass: "description" as const },
  { key: "resolvedLanguage", label: "实际回退语言", field: "Label", widthClass: "identity" as const },
  { key: "version", label: "版本", field: "Version", widthClass: "identity" as const },
].filter(column => dictionaryFields.value.has(column.field)), { pageId: "platform-settings", tableId: "dictionary" });
function problem(failure: unknown) { error.value = failure instanceof Error ? failure.message : "请求未确认，请重新读取后重试。"; }
function parseRows<T>(data: unknown, guard: (row: unknown) => row is T): T[] {
  if (!isRecord(data) || data.authorized !== true || !Array.isArray(data.rows) || !data.rows.every(guard))
    throw new Error("列表响应无效，已丢弃数据，请重新读取。");
  return data.rows;
}
function nullableText(row: Record<string, unknown>, keys: string[]) { return keys.every(key => row[key] === null || typeof row[key] === "string"); }
function isSetting(row: unknown): row is SettingRow {
  return isRecord(row) && typeof row.id === "string" && nullableText(row, ["key", "value", "source", "version", "kind", "state"]);
}
function isEntry(row: unknown): row is DictionaryRow {
  return isRecord(row) && typeof row.id === "string" && nullableText(row, ["category", "code", "languageKey", "label", "resolvedLanguage", "version"])
    && (row.missingTranslation === null || typeof row.missingTranslation === "boolean");
}
async function readSettings() {
  if (!canRead("platform.settings.read")) return;
  const sequence = ++settingsSequence; const signal = controller.signal;
  const fingerprint = settingsFingerprint.value;
  if (successfulSettingsFingerprint !== fingerprint) {
    rows.value = []; selected.value = null; successfulSettingsFingerprint = null;
  }
  settingsQueryError.value = ""; settingsLoading.value = true; busy.value = true;
  try {
    const query = new URLSearchParams({ offset: String(settingOffset.value), limit: "50" });
    if (kind.value && fields.value.has("Key")) query.set("kind", kind.value);
    const projected = parseRows(await api(`/api/platform/settings?${query}`, { signal }), isSetting);
    if (sequence === settingsSequence && fingerprint === settingsFingerprint.value) {
      rows.value = projected; successfulSettingsFingerprint = fingerprint;
    }
  }
  catch (failure) {
    if (!signal.aborted && sequence === settingsSequence) {
      settingsQueryError.value = failure instanceof Error ? failure.message : "请求未确认，请重新读取后重试。";
      if (failure instanceof ApiError && [401, 403].includes(failure.status)) {
        rows.value = []; selected.value = null; successfulSettingsFingerprint = null;
      } else if (successfulSettingsFingerprint !== fingerprint) rows.value = [];
    }
  }
  finally {
    if (sequence === settingsSequence) {
      settingsLoading.value = false; busy.value = false;
    }
  }
}
async function readDictionary() {
  const sequence = ++readSequence; const fingerprint = dictionaryFingerprint.value;
  if (successfulDictionaryFingerprint !== fingerprint) {
    entries.value = []; successfulDictionaryFingerprint = null;
  }
  entry.value = null; deletePending.value = false; dictionaryQueryError.value = "";
  dictionaryLoading.value = true; busy.value = true;
  try {
    const query = new URLSearchParams({
      category: category.value, language: language.value, offset: String(offset.value), limit: "50",
    });
    if (missingTranslation.value && dictionaryFields.value.has("Label")) query.set("missingTranslation", missingTranslation.value);
    const data = parseRows(await api(`/api/platform/dictionary?${query}`, { signal: controller.signal }), isEntry);
    if (sequence === readSequence && fingerprint === dictionaryFingerprint.value) {
      entries.value = data; successfulDictionaryFingerprint = fingerprint;
    }
  } catch (failure) {
    if (!controller.signal.aborted && sequence === readSequence) {
      dictionaryQueryError.value = failure instanceof Error ? failure.message : "请求未确认，请重新读取后重试。";
      if (failure instanceof ApiError && [401, 403].includes(failure.status)) {
        entries.value = []; entry.value = null; successfulDictionaryFingerprint = null;
      } else if (successfulDictionaryFingerprint !== fingerprint) entries.value = [];
    }
  }
  finally {
    if (sequence === readSequence) {
      dictionaryLoading.value = false; busy.value = false;
    }
  }
}
async function saveSetting() {
  if (!selected.value?.version) return;
  busy.value = true; error.value = ""; message.value = "";
  try {
    await api(`/api/platform/settings/${encodeURIComponent(selected.value.id)}`, { method: "PUT",
      body: JSON.stringify({ expectedVersion: selected.value.version, value: value.value }) });
    message.value = "配置已提交；受影响服务的实际读取与现场许可仍按各自门禁判断。"; await readSettings();
  } catch (failure) { problem(failure); rows.value = []; selected.value = null; }
  finally { busy.value = false; }
}
async function createDictionary() {
  busy.value = true; error.value = "";
  try {
    await api("/api/platform/dictionary", { method: "POST", body: JSON.stringify({ category: category.value, code: code.value, languageKey: languageKey.value }) });
    code.value = ""; languageKey.value = ""; message.value = "稳定业务码已创建。"; await readDictionary();
  } catch (failure) { problem(failure); }
  finally { busy.value = false; }
}
async function saveTranslation() {
  if (!entry.value?.version) return;
  busy.value = true; error.value = "";
  try {
    await api(`/api/platform/dictionary/${encodeURIComponent(entry.value.id)}/translation`, { method: "PUT",
      body: JSON.stringify({ expectedVersion: entry.value.version, language: language.value, label: label.value }) });
    label.value = ""; message.value = "语言文本已更新，业务码保持不变。"; await readDictionary();
  } catch (failure) { problem(failure); entries.value = []; entry.value = null; }
  finally { busy.value = false; }
}
async function removeDictionary() {
  if (!entry.value?.version || !deletePending.value) return;
  busy.value = true; error.value = "";
  try {
    await api(`/api/platform/dictionary/${encodeURIComponent(entry.value.id)}?${new URLSearchParams({ expectedVersion: entry.value.version })}`, { method: "DELETE" });
    message.value = "未被引用的字典项已删除。"; await readDictionary();
  } catch (failure) { problem(failure); deletePending.value = false; entry.value = null; entries.value = []; }
  finally { busy.value = false; }
}
function settingCell(row: SettingRow, key: string) {
  switch (key) { case "id": return row.id; case "key": return row.key; case "value": return row.value ?? (row.state === "Unverified" ? "Unverified（无物理证据）" : "未授权 / 未配置"); case "source": return row.source; case "version": return row.version; default: return "未授权"; }
}
function dictionaryCell(row: DictionaryRow, key: string) {
  switch (key) { case "id": return row.id; case "code": return row.code; case "languageKey": return row.languageKey; case "label": return row.label; case "resolvedLanguage": return row.resolvedLanguage ?? (row.missingTranslation ? "缺失：显示诊断键" : "未授权"); case "version": return row.version; default: return "未授权"; }
}
watch([category, language, missingTranslation], () => {
  readSequence++; offset.value = 0; entries.value = []; successfulDictionaryFingerprint = null;
  entry.value = null; label.value = ""; deletePending.value = false;
  dictionaryLoading.value = false; dictionaryQueryError.value = "";
});
watch(kind, () => {
  settingsSequence++; settingOffset.value = 0; rows.value = []; selected.value = null; value.value = "";
  successfulSettingsFingerprint = null; settingsLoading.value = false; settingsQueryError.value = ""; busy.value = false;
});
watch([() => sessionState.subject, () => sessionState.token], () => {
  controller.abort(); controller = new AbortController(); readSequence++; settingsSequence++;
  rows.value = []; entries.value = []; selected.value = null; entry.value = null; deletePending.value = false;
  successfulSettingsFingerprint = null; successfulDictionaryFingerprint = null;
  settingsLoading.value = false; settingsQueryError.value = "";
  dictionaryLoading.value = false; dictionaryQueryError.value = "";
  value.value = ""; label.value = ""; code.value = ""; languageKey.value = ""; message.value = ""; busy.value = false;
  kind.value = ""; missingTranslation.value = ""; offset.value = 0; settingOffset.value = 0;
  error.value = "身份或权限已变更，旧列表与编辑已清除，请重新读取。";
}, { deep: true, flush: "sync" });
onMounted(readSettings); onUnmounted(() => { controller.abort(); readSequence++; settingsSequence++; });
</script>
<template>
  <section class="wes-page"><header><p class="eyebrow">平台管理</p><h1>配置、字典与语言</h1>
    <p>仅展示已审定的非秘密配置。P 值非法时拒绝提交；H 未核验时保持 Unverified，软件录入不能代替现场签字。</p></header>
    <p v-if="error" role="alert" class="error">{{ error }}</p><p v-if="message" role="status">{{ message }}</p>
    <section v-if="canRead('platform.settings.read')" aria-labelledby="catalog-title">
      <h2 id="catalog-title">强类型配置目录</h2><button :disabled="busy" @click="readSettings">重新读取配置</button>
      <form data-test="setting-filters" class="toolbar" @submit.prevent="settingOffset = 0; readSettings()">
        <label v-if="fields.has('Key')">参数类别<select v-model="kind" name="filterSettingKind"><option value="">全部</option><option value="P">P（软件参数）</option><option value="H">H（现场核验）</option></select></label>
        <button :disabled="busy">查询配置</button>
      </form>
      <ColumnSettings :columns="available" v-model="selectedColumns" v-model:widths="settingWidths"
        v-model:fixed-first-column="settingsFixedFirstColumn" :preference-warning="preferenceWarning" />
      <QueryState label="强类型配置目录" :state="settingsQueryState" :busy="settingsLoading"
        :error-message="settingsQueryError" :stale-message="settingsStaleMessage" @retry="readSettings">
        <template #empty><p>没有可展示的配置。未装配目录时请使用受控初始化，不会回填伪造默认行。</p></template>
        <div class="table-scroll" :aria-busy="settingsLoading"><table :class="{ 'table-fixed-first-column': settingsFixedFirstColumn }"><caption>配置值、来源与当前版本</caption>
          <thead><tr><th v-for="column in visible" :key="column.key" scope="col" :style="settingWidthStyle(column.key)">{{ column.label }}</th><th scope="col">操作</th></tr></thead>
          <tbody><tr v-for="row in rows" :key="row.id"><td v-for="column in visible" :key="column.key">{{ settingCell(row, column.key) ?? "未授权" }}</td>
            <td><button v-if="row.kind !== 'H' && row.version && row.value !== null && canRead('platform.settings.write')" data-test="edit-setting"
              :disabled="settingsLoading || settingsQueryState === 'stale'"
              @click="selected = row; value = row.value">编辑此版本</button><span v-else>只读 / 未核验</span></td></tr></tbody>
        </table></div>
      </QueryState>
      <nav class="actions" aria-label="配置分页"><button :disabled="busy || settingOffset === 0" @click="settingOffset -= 50; readSettings()">上一页</button><span>第 {{ settingOffset / 50 + 1 }} 页，每页最多 50 条</span><button :disabled="busy || rows.length < 50" @click="settingOffset += 50; readSettings()">下一页</button></nav>
      <form v-if="selected" data-test="setting-form" class="editor" @submit.prevent="saveSetting">
        <h2>更改 {{ selected.key }}</h2><p>当前版本：{{ selected.version }}；来源：{{ selected.source }}；原值：{{ selected.value }}</p>
        <label for="setting-value">新的正整数值</label><input id="setting-value" v-model="value" required inputmode="numeric" pattern="[0-9]+" maxlength="10">
        <div class="actions"><button type="submit" :disabled="busy">确认提交此版本</button><button type="button" @click="selected = null">取消</button></div>
      </form>
    </section>
    <section v-if="canRead('platform.dictionary.read')" aria-labelledby="dictionary-title">
      <h2 id="dictionary-title">字典与语言回退</h2>
      <form data-test="dictionary-filters" class="toolbar" @submit.prevent="offset = 0; readDictionary()">
        <label for="dictionary-category">业务分类<input id="dictionary-category" v-model="category" required maxlength="64"></label>
        <label for="dictionary-language">请求语言<input id="dictionary-language" v-model="language" required maxlength="35" placeholder="zh-CN"></label>
        <label v-if="dictionaryFields.has('Label')">翻译缺失<select v-model="missingTranslation" name="filterMissingTranslation"><option value="">全部</option><option value="true">请求和默认语言均缺失</option><option value="false">有请求或默认语言文本</option></select></label>
        <button :disabled="busy">查询分类</button>
      </form><p>显示顺序：请求语言 → 当前租户默认语言 → [诊断语言键]。回退不改变业务状态码。</p>
      <ColumnSettings :columns="dictColumns.available.value" v-model="dictColumns.selected.value"
        v-model:widths="dictColumns.widths.value" v-model:fixed-first-column="dictColumns.fixedFirstColumn.value"
        :preference-warning="dictColumns.preferenceWarning.value" />
      <QueryState label="字典与语言目录" :state="dictionaryQueryState" :busy="dictionaryLoading"
        :error-message="dictionaryQueryError" :stale-message="dictionaryStaleMessage" @retry="readDictionary">
        <template #empty><p>当前分类没有可展示的字典项。</p></template>
        <div class="table-scroll" :aria-busy="dictionaryLoading"><table :class="{ 'table-fixed-first-column': dictColumns.fixedFirstColumn.value }"><caption>当前分类的稳定业务码</caption>
          <thead><tr><th v-for="column in dictColumns.visible.value" :key="column.key" scope="col" :style="dictColumns.widthStyle(column.key)">{{ column.label }}</th><th scope="col">操作</th></tr></thead>
          <tbody><tr v-for="row in entries" :key="row.id"><td v-for="column in dictColumns.visible.value" :key="column.key">{{ dictionaryCell(row, column.key) ?? "未授权" }}</td>
            <td><button v-if="row.version && canRead('platform.dictionary.write')" :disabled="dictionaryLoading || dictionaryQueryState === 'stale'"
              @click="entry = row; label = row.label ?? ''; deletePending = false">选择字典项</button></td></tr></tbody>
        </table></div>
        <div class="actions"><button :disabled="busy || offset === 0" @click="offset -= 50; readDictionary()">上一页</button><span>从 {{ offset + 1 }} 条起；每页最多 50 条</span>
          <button :disabled="busy || entries.length < 50" @click="offset += 50; readDictionary()">下一页</button></div>
      </QueryState>
      <form v-if="canRead('platform.dictionary.write')" class="editor" @submit.prevent="createDictionary">
        <fieldset><legend>在当前分类创建稳定码（不创建翻译）</legend><label for="dictionary-code">业务码</label><input id="dictionary-code" v-model="code" required maxlength="64">
          <label for="dictionary-key">语言键</label><input id="dictionary-key" v-model="languageKey" required maxlength="128">
          <div class="actions"><button :disabled="busy">创建稳定码</button></div></fieldset>
      </form>
      <form v-if="entry" class="editor" @submit.prevent="saveTranslation"><h2>修改 {{ entry.code }} 的 {{ language }} 显示文本</h2><p>版本：{{ entry.version }}。分类与稳定码不可改名。</p>
        <label for="translation-label">纯文本显示值</label><input id="translation-label" v-model="label" required maxlength="512">
        <div class="actions"><button :disabled="busy">提交翻译</button><button type="button" @click="deletePending = true">申请删除稳定码</button><button type="button" @click="entry = null">取消选择</button></div>
        <section v-if="deletePending" aria-label="删除稳定码确认"><p class="notice">确认删除 {{ category }} / {{ entry.code }}？所有语言文本一并移除；存在业务引用时服务端拒绝，不提供强制解除引用。</p>
          <button type="button" :disabled="busy" @click="removeDictionary">确认删除此版本</button><button type="button" @click="deletePending = false">保留字典项</button></section>
      </form>
    </section>
  </section>
</template>
