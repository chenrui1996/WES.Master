import { describe, expect, it, vi, beforeEach } from "vitest";
import { api, setAccessToken, forgetIdentity, sessionState } from "./identity-api";

describe("identity transport", () => {
  beforeEach(() => { forgetIdentity(); localStorage.clear(); sessionStorage.clear(); });

  it("sends credentials only to same origin with no persistent token cache", async () => {
    const calls: RequestInit[] = [];
    vi.stubGlobal("fetch", vi.fn(async (_url: string, options: RequestInit) => {
      calls.push(options);
      return new Response(JSON.stringify({ userId: "fixture" }), { status: 200 });
    }));
    setAccessToken("opaque-fixture", "2099-01-01T00:00:00Z");
    await api("/api/identity/me");
    expect(new Headers(calls[0]?.headers).get("Authorization")).toBe("Bearer opaque-fixture");
    expect(calls[0]?.credentials).toBe("same-origin");
    expect(localStorage.length).toBe(0);
    expect(sessionStorage.length).toBe(0);
    await expect(api("https://evil.invalid")).rejects.toThrow();
  });

  it("never replays an uncertain mutation or silently clears a failed response", async () => {
    const fetcher = vi.fn(async () => new Response(JSON.stringify({
      code: "VersionConflict", correlationId: "fixture-correlation",
    }), { status: 412 }));
    vi.stubGlobal("fetch", fetcher);
    await expect(api("/api/identity/password", { method: "POST", body: "{}" })).rejects.toThrow("VersionConflict");
    expect(fetcher).toHaveBeenCalledTimes(1);
  });

  it("drops grants and token immediately on authentication loss", async () => {
    setAccessToken("opaque-fixture", "2099-01-01T00:00:00Z");
    vi.stubGlobal("fetch", vi.fn(async () => new Response("{}", { status: 401 })));
    await expect(api("/api/identity/me")).rejects.toThrow();
    expect(sessionState.token).toBeNull();
    expect(sessionState.subject).toBeNull();
  });

  it("keys each explicit nonsecret platform mutation once and preserves a supplied retry key", async () => {
    const calls: Headers[] = [];
    vi.stubGlobal("fetch", vi.fn(async (_url: string, options: RequestInit) => {
      calls.push(new Headers(options.headers));
      return new Response("{}", { status: 200 });
    }));
    await api("/api/platform/users", { method: "POST", body: "{}" });
    await api("/api/platform/users/fixture", { method: "PUT", body: "{}" });
    const key = calls[0]?.get("Idempotency-Key");
    expect(key).toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/);
    expect(calls[1]?.get("Idempotency-Key")).not.toBe(key);
    await api("/api/platform/users", { method: "POST", body: "{}", headers: { "Idempotency-Key": key! } });
    expect(calls[2]?.get("Idempotency-Key")).toBe(key);
  });

  it("never assigns ordinary command receipts to credentials, authentication or eligibility reads", async () => {
    const calls: Headers[] = [];
    vi.stubGlobal("fetch", vi.fn(async (_url: string, options: RequestInit) => {
      calls.push(new Headers(options.headers));
      return new Response("{}", { status: 200 });
    }));
    await api("/api/platform/users/fixture/credential", { method: "POST", body: "{}" });
    await api("/api/identity/password", { method: "POST", body: "{}" });
    await api("/api/platform/tenants/switch-check", { method: "POST", body: "{}" });
    await api("/api/platform/users");
    expect(calls.every(headers => !headers.has("Idempotency-Key"))).toBe(true);
  });

  it("rejects a response whose body finishes after the identity changed", async () => {
    let finish: ReadableStreamDefaultController<Uint8Array> | undefined;
    const body = new ReadableStream<Uint8Array>({ start(controller) { finish = controller; } });
    vi.stubGlobal("fetch", vi.fn(async () => new Response(body)));
    const result = api("/api/platform/settings");
    await Promise.resolve(); await Promise.resolve();
    forgetIdentity();
    finish!.enqueue(new TextEncoder().encode('{"rows":[]}')); finish!.close();
    await expect(result).rejects.toThrow("身份上下文已变更");
  });

  it("invalidates in-flight responses on current grant replacement, even when tenant and token are unchanged", async () => {
    sessionState.subject = { tenantId: "tenant", userId: "user", sessionId: "session", credentialVersion: "v", requiresPasswordChange: false,
      grants: [{ permission: "platform.settings.read", scope: 2, departments: [], fields: ["Id", "Value"] }] };
    let deliver: ((response: Response) => void) | undefined;
    vi.stubGlobal("fetch", vi.fn(() => new Promise<Response>(resolve => { deliver = resolve; })));
    const result = api("/api/platform/settings");
    sessionState.subject.grants[0]!.fields = ["Id"];
    deliver!(new Response('{"rows":[]}'));
    await expect(result).rejects.toThrow("身份上下文已变更");
  });
});
