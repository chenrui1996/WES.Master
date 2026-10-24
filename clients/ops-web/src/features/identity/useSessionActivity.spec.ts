import { afterEach, expect, it, vi } from "vitest";
import { forgetIdentity, setAccessToken } from "./identity-api";
import { useSessionActivity } from "./useSessionActivity";
afterEach(() => { vi.unstubAllGlobals(); vi.useRealTimers(); forgetIdentity(); });
it("records explicit interactions at most once in thirty seconds and has no polling timer", async () => {
  vi.useFakeTimers(); setAccessToken("fixture", "2099-01-01T00:00:00Z");
  const calls: string[] = [];
  vi.stubGlobal("fetch", vi.fn(async (url: string) => { calls.push(url); return new Response(null, { status: 204 }); }));
  const activity = useSessionActivity();
  await activity.interact(); await activity.interact();
  expect(calls).toEqual(["/api/identity/activity"]);
  await vi.advanceTimersByTimeAsync(30000);
  expect(calls).toHaveLength(1);
  await activity.interact(); expect(calls).toHaveLength(2);
});
