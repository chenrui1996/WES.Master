import { computed, readonly, ref, watch } from "vue";
import { sessionState } from "./identity/identity-api";

export type DensityMode = "compact" | "standard" | "comfortable";

const defaultDensity: DensityMode = "compact";
const storageUnavailableWarning = "无法保存显示密度；本次选择仅在当前会话有效。";
const invalidDensityWarning = "已忽略无效的显示密度设置，恢复为紧凑显示。";

export function visualPreferenceKey(environment: string, tenantId: string, userId: string): string {
  return `wes-ops-web:density:${encodeURIComponent(environment)}:${encodeURIComponent(tenantId)}:${encodeURIComponent(userId)}`;
}

function isDensityMode(value: string | null): value is DensityMode {
  return value === "compact" || value === "standard" || value === "comfortable";
}

export function readDensityPreference(
  storage: Pick<Storage, "getItem">,
  key: string,
): { density: DensityMode; warning: string | null } {
  try {
    const value = storage.getItem(key);
    if (value === null) return { density: defaultDensity, warning: null };
    return isDensityMode(value)
      ? { density: value, warning: null }
      : { density: defaultDensity, warning: invalidDensityWarning };
  } catch (error) {
    if (!(error instanceof DOMException)) throw error;
    return { density: defaultDensity, warning: storageUnavailableWarning };
  }
}

export function writeDensityPreference(
  storage: Pick<Storage, "setItem">,
  key: string,
  density: DensityMode,
): string | null {
  if (!isDensityMode(density)) throw new Error("显示密度值无效。");
  try {
    storage.setItem(key, density);
    return null;
  } catch (error) {
    if (!(error instanceof DOMException)) throw error;
    return storageUnavailableWarning;
  }
}

export function useVisualPreferences(
  environment: string,
  storageProvider: () => Pick<Storage, "getItem" | "setItem"> = () => window.localStorage,
) {
  const density = ref<DensityMode>(defaultDensity);
  const storageWarning = ref<string | null>(null);
  const identityKey = computed(() => {
    const subject = sessionState.subject;
    return subject
      ? visualPreferenceKey(environment, subject.tenantId, subject.userId)
      : null;
  });

  watch(identityKey, key => {
    density.value = defaultDensity;
    storageWarning.value = null;
    if (key === null) return;

    try {
      const result = readDensityPreference(storageProvider(), key);
      density.value = result.density;
      storageWarning.value = result.warning;
    } catch (error) {
      if (!(error instanceof DOMException)) throw error;
      storageWarning.value = storageUnavailableWarning;
    }
  }, { immediate: true, flush: "sync" });

  function setDensity(mode: DensityMode): void {
    if (!isDensityMode(mode)) throw new Error("显示密度值无效。");
    density.value = mode;
    storageWarning.value = null;

    const key = identityKey.value;
    if (key === null) return;
    try {
      storageWarning.value = writeDensityPreference(storageProvider(), key, mode);
    } catch (error) {
      if (!(error instanceof DOMException)) throw error;
      storageWarning.value = storageUnavailableWarning;
    }
  }

  return {
    density: readonly(density),
    storageWarning: readonly(storageWarning),
    setDensity,
  };
}
