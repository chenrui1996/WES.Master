import { computed, ref, watch } from "vue";

/** Column metadata only. Never persists rows, authorization or preferences across identities. */
export interface AuthorizedColumn { key: string; label: string; required?: boolean }

/** The caller supplies only fields present in the current server projection. Revocation drops preferences. */
export function useColumns(source: () => AuthorizedColumn[]) {
  const available = computed(source);
  const selected = ref<string[]>([]);
  watch(available, (columns, previous) => {
    const permitted = new Set(columns.map(column => column.key));
    const old = new Set(previous?.map(column => column.key) ?? []);
    selected.value = [
      ...selected.value.filter(key => permitted.has(key)),
      ...columns.filter(column => !old.has(column.key) && !selected.value.includes(column.key)).map(column => column.key),
    ];
    for (const column of columns)
      if (column.required && !selected.value.includes(column.key)) selected.value.unshift(column.key);
  }, { immediate: true });
  const visible = computed(() => selected.value.flatMap(key => {
    const column = available.value.find(item => item.key === key);
    return column ? [column] : [];
  }));
  return { available, selected, visible };
}
