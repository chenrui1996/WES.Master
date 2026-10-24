import { effectScope } from "vue";
import { beforeEach, describe, expect, it } from "vitest";
import { forgetIdentity, sessionState, type Subject } from "./identity/identity-api";
import {
  readDensityPreference,
  useVisualPreferences,
  visualPreferenceKey,
  writeDensityPreference,
} from "./visual-preferences";

const subject = (tenantId: string, userId: string): Subject => ({
  tenantId,
  userId,
  sessionId: "session",
  requiresPasswordChange: false,
  credentialVersion: null,
  grants: [],
});

describe("visual density preferences", () => {
  beforeEach(() => {
    forgetIdentity();
    localStorage.clear();
  });

  it("isolates preferences by environment, tenant, and user", () => {
    const first = visualPreferenceKey("production", "tenant-a", "user-a");
    expect(visualPreferenceKey("test", "tenant-a", "user-a")).not.toBe(first);
    expect(visualPreferenceKey("production", "tenant-b", "user-a")).not.toBe(first);
    expect(visualPreferenceKey("production", "tenant-a", "user-b")).not.toBe(first);
  });

  it("reads only a valid density enum from the matching key", () => {
    const key = visualPreferenceKey("test", "tenant-a", "user-a");
    localStorage.setItem(key, "comfortable");
    expect(readDensityPreference(localStorage, key)).toEqual({
      density: "comfortable",
      warning: null,
    });
  });

  it("falls back to compact and exposes a warning for malformed stored values", () => {
    const key = visualPreferenceKey("test", "tenant-a", "user-a");
    localStorage.setItem(key, "unknown-density-value");
    const result = readDensityPreference(localStorage, key);
    expect(result.density).toBe("compact");
    expect(result.warning).toBeTruthy();
  });

  it("falls back to compact and exposes a warning when storage reads fail", () => {
    const storage = {
      getItem: (_key: string) => {
        throw new DOMException("blocked", "SecurityError");
      },
    };
    const key = visualPreferenceKey("test", "tenant-a", "user-a");
    expect(readDensityPreference(storage, key)).toMatchObject({
      density: "compact",
      warning: expect.any(String),
    });
  });

  it("loads a different value after the authenticated tenant or user changes", () => {
    const keyA = visualPreferenceKey("test", "tenant-a", "user-a");
    const keyB = visualPreferenceKey("test", "tenant-b", "user-b");
    localStorage.setItem(keyA, "comfortable");
    localStorage.setItem(keyB, "standard");
    const scope = effectScope();
    const preferences = scope.run(() => useVisualPreferences("test"))!;

    sessionState.subject = subject("tenant-a", "user-a");
    expect(preferences.density.value).toBe("comfortable");
    sessionState.subject = subject("tenant-b", "user-b");
    expect(preferences.density.value).toBe("standard");
    expect(localStorage.getItem(keyA)).toBe("comfortable");
    expect(localStorage.getItem(keyB)).toBe("standard");
    scope.stop();
  });

  it("keeps the selected density in memory and warns when storage writes fail", () => {
    const storage = {
      getItem: (key: string) => localStorage.getItem(key),
      setItem: (_key: string, _value: string) => {
        throw new DOMException("blocked", "SecurityError");
      },
    };
    const key = visualPreferenceKey("test", "tenant-a", "user-a");
    expect(writeDensityPreference(storage, key, "standard")).toBeTruthy();
    const scope = effectScope();
    const preferences = scope.run(() => useVisualPreferences("test", () => storage))!;

    sessionState.subject = subject("tenant-a", "user-a");
    preferences.setDensity("comfortable");
    expect(preferences.density.value).toBe("comfortable");
    expect(preferences.storageWarning.value).toBeTruthy();
    scope.stop();
  });

  it("keeps the UI usable when obtaining local storage throws", () => {
    const scope = effectScope();
    const preferences = scope.run(() => useVisualPreferences("test", () => {
      throw new DOMException("blocked", "SecurityError");
    }))!;

    sessionState.subject = subject("tenant-a", "user-a");
    expect(preferences.density.value).toBe("compact");
    expect(preferences.storageWarning.value).toBeTruthy();
    preferences.setDensity("comfortable");
    expect(preferences.density.value).toBe("comfortable");
    expect(preferences.storageWarning.value).toBeTruthy();
    scope.stop();
  });
});
