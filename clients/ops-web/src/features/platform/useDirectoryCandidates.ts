import { ref, shallowRef } from "vue";
import { api, ApiError } from "../identity/identity-api";

interface CandidatePage<T> { authorized: boolean; rows: T[] }
const candidatePageSize = 25;

export function useDirectoryCandidates<T extends { id: string }>(path: string) {
  const rows = shallowRef<T[]>([]);
  const more = ref(false);
  const loading = ref(false);
  let generation = 0;
  let nextOffset = 0;
  let currentCode = "";
  let activeController: AbortController | undefined;
  function reset() {
    generation++; activeController?.abort(); activeController = undefined;
    rows.value = []; more.value = false; loading.value = false; nextOffset = 0; currentCode = "";
  }
  async function read(signal: AbortSignal, append = false, code?: string) {
    if (append && (loading.value || !more.value)) return null;
    if (!append) {
      generation++;
      activeController?.abort();
      currentCode = code?.trim() ?? "";
      nextOffset = 0;
    }
    const current = generation;
    const requestController = new AbortController();
    activeController = requestController;
    const abort = () => requestController.abort();
    if (signal.aborted) abort();
    else signal.addEventListener("abort", abort, { once: true });
    loading.value = true;
    try {
      const query = new URLSearchParams({
        offset: String(append ? nextOffset : 0),
        limit: String(candidatePageSize),
      });
      if (currentCode) query.set("code", currentCode);
      const response = await api<CandidatePage<T>>(
        `${path}?${query}`, { signal: requestController.signal });
      if (current !== generation) return null;
      if (response.authorized !== true || !Array.isArray(response.rows) || response.rows.length > candidatePageSize
        || response.rows.some(row => !row || typeof row.id !== "string"))
        throw new ApiError(403, "DirectoryCandidatesDenied", "未提供");
      rows.value = append ? [...new Map([...rows.value, ...response.rows].map(row => [row.id, row])).values()] : response.rows;
      nextOffset = (append ? nextOffset : 0) + candidatePageSize;
      more.value = response.rows.length === candidatePageSize;
      return response;
    } finally {
      signal.removeEventListener("abort", abort);
      if (current === generation) {
        loading.value = false;
        if (activeController === requestController) activeController = undefined;
      }
    }
  }
  return { rows, more, loading, read, reset };
}
