import { computed, onMounted, onUnmounted, ref, shallowRef, watch, type ComputedRef } from "vue";
import { ApiError, sessionState } from "../identity/identity-api";
import { AlarmResponseError } from "./alarms";

/** Request-local query state shared by the two owner console pages; it never stores pending writes. */
export function useNotificationQuery<Row, Detail>(readable: ComputedRef<boolean>,
  readRows: (offset: number, limit: number, signal: AbortSignal) => Promise<Row[]>,
  readDetail: (id: string, signal: AbortSignal) => Promise<Detail>) {
  const rows = shallowRef<Row[]>([]); const detail = shallowRef<Detail | null>(null);
  const busy = ref(false); const fresh = ref(false); const loaded = ref(false);
  const error = ref(""); const offset = ref(0); const limit = ref(25); const online = ref(navigator.onLine);
  const loadedAt = ref("");
  const scope = computed(() => JSON.stringify(sessionState.subject));
  const state = computed(() => busy.value ? rows.value.length ? "stale" : "loading"
    : rows.value.length ? fresh.value ? "ready" : "stale" : error.value ? "error" : "empty");
  let controller = new AbortController(); let sequence = 0;
  function cancel() { ++sequence; controller.abort(); controller = new AbortController(); busy.value = false; fresh.value = false; }
  function clear() { cancel(); rows.value = []; detail.value = null; error.value = ""; loaded.value = false; loadedAt.value = ""; }
  watch(scope, () => { clear(); offset.value = 0; }, { flush: "sync" });
  function connection() { online.value = navigator.onLine; if (!online.value) { cancel(); detail.value = null; } }
  onMounted(() => { addEventListener("online", connection); addEventListener("offline", connection); });
  onUnmounted(() => { cancel(); removeEventListener("online", connection); removeEventListener("offline", connection); });
  async function read(id?: string) {
    if (!readable.value || !online.value || busy.value) return;
    cancel(); detail.value = null; error.value = ""; busy.value = true;
    const ticket = sequence; const signal = controller.signal; const fingerprint = scope.value;
    const current = () => ticket === sequence && fingerprint === scope.value && !signal.aborted;
    try {
      if (id) {
        const value = await readDetail(id, signal);
        if (current()) detail.value = value;
      } else {
        const value = await readRows(offset.value, limit.value, signal);
        if (current()) { rows.value = value; fresh.value = true; loaded.value = true; loadedAt.value = new Date().toISOString(); }
      }
    } catch (failure) {
      if (!current()) return;
      if (failure instanceof AlarmResponseError || failure instanceof ApiError && [401, 403, 404].includes(failure.status)) {
        rows.value = []; loaded.value = false; loadedAt.value = "";
      }
      error.value = failure instanceof Error ? failure.message : "读取未确认，请显式重试。";
    } finally { if (ticket === sequence) busy.value = false; }
  }
  function move(nextOffset: number, nextLimit: number) {
    if (!online.value || !readable.value || busy.value) return;
    clear(); offset.value = nextOffset; limit.value = nextLimit; void read();
  }
  function attempted() { cancel(); detail.value = null; }
  function changed() { clear(); }
  function denied(message: string) { clear(); error.value = message; }
  return { rows, detail, busy, fresh, loaded, error, offset, limit, online, loadedAt, state, read, move, attempted, changed, denied };
}
