import { ref, shallowRef } from "vue";
import { api, ApiError } from "../identity/identity-api";

interface CandidatePage<T> { authorized: boolean; rows: T[] }

export function useDirectoryCandidates<T extends { id: string }>(path: string) {
  const rows = shallowRef<T[]>([]);
  const more = ref(false);
  const loading = ref(false);
  let generation = 0;
  let nextOffset = 0;
  function reset() {
    generation++; rows.value = []; more.value = false; loading.value = false; nextOffset = 0;
  }
  async function read(signal: AbortSignal, append = false) {
    if (loading.value || append && !more.value) return null;
    const current = generation;
    loading.value = true;
    try {
      const response = await api<CandidatePage<T>>(`${path}?offset=${append ? nextOffset : 0}&limit=50`, { signal });
      if (current !== generation) return null;
      if (response.authorized !== true || !Array.isArray(response.rows) || response.rows.length > 50
        || response.rows.some(row => !row || typeof row.id !== "string"))
        throw new ApiError(403, "DirectoryCandidatesDenied", "未提供");
      rows.value = append ? [...new Map([...rows.value, ...response.rows].map(row => [row.id, row])).values()] : response.rows;
      nextOffset = (append ? nextOffset : 0) + 50;
      more.value = response.rows.length === 50;
      return response;
    } finally { if (current === generation) loading.value = false; }
  }
  return { rows, more, loading, read, reset };
}
