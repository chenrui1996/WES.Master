import { ref } from "vue";
import { api, sessionState } from "./identity-api";
export function useSessionActivity() {
  const error = ref("");
  let lastAttempt = -Infinity;
  let pending = false;
  async function interact() {
    if (!sessionState.token || pending || Date.now() - lastAttempt < 30000) return;
    pending = true; lastAttempt = Date.now(); error.value = "";
    try { await api("/api/identity/activity", { method: "POST" }); }
    catch (failure) { error.value = failure instanceof Error ? failure.message : "活动未确认，请重新读取会话；不会自动重放操作。"; }
    finally { pending = false; }
  }
  return { error, interact };
}
