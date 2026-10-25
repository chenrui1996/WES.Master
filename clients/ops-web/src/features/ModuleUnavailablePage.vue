<script setup lang="ts">
import { computed, ref } from "vue";
import { useRoute, useRouter } from "vue-router";
import { isModuleCode, moduleCatalog } from "./module-catalog";

const route = useRoute();
const router = useRouter();
const busy = ref(false);
const requestedModule = computed(() => {
  const value = route.query.module;
  return typeof value === "string" && isModuleCode(value) ? value : null;
});
const returnUrl = computed(() => {
  const value = route.query.returnUrl;
  return typeof value === "string" && value.startsWith("/") && !value.startsWith("//") && !value.includes("\\")
    ? value : "/account/sessions";
});
const title = computed(() => requestedModule.value
  ? `${requestedModule.value} 模块不可用`
  : "模块入口不可用");

async function retry() {
  busy.value = true;
  const loaded = await moduleCatalog.load(true);
  busy.value = false;
  if (loaded && requestedModule.value && moduleCatalog.isEnabled(requestedModule.value))
    await router.replace(returnUrl.value);
}
</script>

<template>
  <section class="wes-page module-unavailable" aria-labelledby="module-unavailable-title">
    <header>
      <p class="eyebrow">部署配置</p>
      <h1 id="module-unavailable-title">{{ title }}</h1>
    </header>
    <p v-if="moduleCatalog.state.status === 'error'" class="error" role="alert">
      {{ moduleCatalog.state.error }}
    </p>
    <p v-else class="notice" role="status">
      该模块入口由部署配置控制。关闭入口不改变服务端 API 的认证与授权规则。
    </p>
    <div class="actions">
      <button type="button" :disabled="busy" @click="retry">
        {{ busy ? "正在检查…" : "重新检查模块配置" }}
      </button>
      <button type="button" @click="router.replace('/account/sessions')">返回会话与安全</button>
    </div>
  </section>
</template>
