import { mount, flushPromises } from "@vue/test-utils";
import { afterEach, describe, expect, it, vi } from "vitest";
import LoginPage from "./LoginPage.vue";
import { forgetIdentity, sessionState } from "./identity-api";

afterEach(() => { vi.unstubAllGlobals(); forgetIdentity(); });
describe("login page", () => {
  it("starts target-tenant reauthentication with an empty account and password", () => {
    const wrapper = mount(LoginPage, { props: { initialTenant: "target-tenant" } });
    expect((wrapper.get("#tenant").element as HTMLInputElement).value).toBe("target-tenant");
    expect((wrapper.get("#account").element as HTMLInputElement).value).toBe("");
    expect((wrapper.get("#password").element as HTMLInputElement).value).toBe("");
    wrapper.unmount();
  });
  it("discards partial identity when the current-subject read fails after token delivery", async () => {
    vi.stubGlobal("fetch", vi.fn(async (url: string) => new Response(JSON.stringify(url.endsWith("/login") ? {
      accessToken: "partial-fixture", expiresAt: "2099-01-01T00:00:00Z",
      requiresPasswordChange: false, returnUrl: "/account/sessions",
    } : { code: "Unavailable", correlationId: "subject-fixture" }), { status: url.endsWith("/login") ? 200 : 503 })));
    const wrapper = mount(LoginPage);
    await wrapper.get("#tenant").setValue("c95ead5c-af74-477e-81ea-df682973bd02");
    await wrapper.get("#account").setValue("fixture");
    await wrapper.get("#password").setValue("unchanged password fixture");
    await wrapper.get("form").trigger("submit");
    await flushPromises();
    expect(sessionState.token).toBeNull();
    expect(wrapper.emitted("signedIn")).toBeUndefined();
    expect(wrapper.get('[role="alert"]').text()).toContain("subject-fixture");
    wrapper.unmount();
  });
  it("preserves account and password after safe generic failure and displays support correlation", async () => {
    vi.stubGlobal("fetch", vi.fn(async () => new Response(JSON.stringify({
      code: "AuthenticationRequired", correlationId: "support-fixture",
    }), { status: 401 })));
    const wrapper = mount(LoginPage);
    await wrapper.get("#tenant").setValue("c95ead5c-af74-477e-81ea-df682973bd02");
    await wrapper.get("#account").setValue("fixture");
    await wrapper.get("#password").setValue(" exact password ");
    await wrapper.get("form").trigger("submit");
    await flushPromises();
    expect(wrapper.get('[role="alert"]').text()).toContain("support-fixture");
    expect((wrapper.get("#password").element as HTMLInputElement).value).toBe(" exact password ");
    expect(sessionState.token).toBeNull();
    wrapper.unmount();
  });

  it("sends the untouched password and keeps access token only in memory", async () => {
    let body: unknown;
    vi.stubGlobal("fetch", vi.fn(async (url: string, options: RequestInit) => {
      if (url.endsWith("/login")) {
        body = JSON.parse(String(options.body));
        return new Response(JSON.stringify({
          accessToken: "opaque-fixture", expiresAt: "2099-01-01T00:00:00Z",
          requiresPasswordChange: false, returnUrl: "/account/sessions",
        }), { status: 200 });
      }
      return new Response(JSON.stringify({
        tenantId: "tenant", userId: "user", sessionId: "session",
        credentialVersion: "version", requiresPasswordChange: false, grants: [],
      }), { status: 200 });
    }));
    const wrapper = mount(LoginPage);
    await wrapper.get("#tenant").setValue("c95ead5c-af74-477e-81ea-df682973bd02");
    await wrapper.get("#account").setValue("fixture");
    await wrapper.get("#password").setValue(" exact password ");
    await wrapper.get("form").trigger("submit");
    await flushPromises();
    expect(body).toMatchObject({ account: "fixture", password: " exact password ", client: 0 });
    expect(sessionState.token).toBe("opaque-fixture");
    expect(wrapper.emitted("signedIn")?.[0]).toEqual(["/account/sessions"]);
    expect(localStorage.length).toBe(0);
    expect(sessionStorage.length).toBe(0);
    wrapper.unmount();
  });

  it("reveals and hides the password without changing its value or submission", async () => {
    let body: unknown;
    vi.stubGlobal("fetch", vi.fn(async (url: string, options: RequestInit) => {
      if (url.endsWith("/login")) {
        body = JSON.parse(String(options.body));
        return new Response(JSON.stringify({
          accessToken: "visible-fixture", expiresAt: "2099-01-01T00:00:00Z",
          requiresPasswordChange: false, returnUrl: "/account/sessions",
        }), { status: 200 });
      }
      return new Response(JSON.stringify({
        tenantId: "tenant", userId: "user", sessionId: "session",
        credentialVersion: "version", requiresPasswordChange: false, grants: [],
      }), { status: 200 });
    }));
    const wrapper = mount(LoginPage);
    await wrapper.get("#tenant").setValue("c95ead5c-af74-477e-81ea-df682973bd02");
    await wrapper.get("#account").setValue("fixture");
    await wrapper.get("#password").setValue(" exact password ");
    const password = wrapper.get("#password");
    const toggle = wrapper.get(".password-toggle");
    expect(password.attributes("type")).toBe("password");
    expect(toggle.attributes("aria-label")).toBe("显示密码");
    await toggle.trigger("click");
    expect(password.attributes("type")).toBe("text");
    expect(password.element).toHaveProperty("value", " exact password ");
    expect(toggle.attributes("aria-pressed")).toBe("true");
    await toggle.trigger("click");
    expect(password.attributes("type")).toBe("password");
    await wrapper.get("form").trigger("submit");
    await flushPromises();
    expect(body).toMatchObject({ password: " exact password " });
    wrapper.unmount();
  });
});
