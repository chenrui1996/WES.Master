<script setup lang="ts">
import { ref, watch } from "vue";
import { acceptToken, api, forgetIdentity, isRecord, loadSubject } from "./identity-api";
const props = defineProps<{ initialTenant?: string; returnUrl?: string }>();
const emit = defineEmits<{ signedIn: [target: string] }>();
const tenant = ref(props.initialTenant ?? "");
const account = ref("");
const password = ref("");
const answer = ref("");
const challengeId = ref<string>();
const image = ref<string>();
const expires = ref("");
const busy = ref(false);
const error = ref("");
watch([tenant, account], () => { challengeId.value = undefined; image.value = undefined; answer.value = ""; });
async function signIn() {
  if (busy.value) return;
  busy.value = true; error.value = "";
  try {
    const delivery = acceptToken(await api("/api/identity/login", { method: "POST",
      body: JSON.stringify({ tenantId: tenant.value, account: account.value, password: password.value,
        client: 0, challengeId: challengeId.value, answer: answer.value || undefined, returnUrl: props.returnUrl }) }));
    await loadSubject();
    password.value = ""; answer.value = "";
    emit("signedIn", delivery.requiresPasswordChange ? "/account/sessions" : delivery.returnUrl);
  } catch (failure) {
    forgetIdentity();
    error.value = failure instanceof Error ? failure.message : "登录失败。请检查账号、密码和验证码后重试。";
    challengeId.value = undefined; image.value = undefined; answer.value = "";
  } finally { busy.value = false; }
}
async function challenge() {
  busy.value = true; error.value = "";
  try {
    const value: unknown = await api("/api/identity/challenge", { method: "POST",
      body: JSON.stringify({ tenantId: tenant.value, account: account.value }) });
    if (!isRecord(value) || typeof value.id !== "string" || typeof value.expiresAt !== "string"
      || typeof value.imageDataUrl !== "string" || !/^data:image\/png;base64,[A-Za-z0-9+/=]+$/.test(value.imageDataUrl))
      throw new Error("验证码响应无效，请重新获取。");
    challengeId.value = value.id; image.value = value.imageDataUrl; expires.value = value.expiresAt;
  } catch (failure) { error.value = failure instanceof Error ? failure.message : "验证码不可用，请稍后重试。"; }
  finally { busy.value = false; }
}
async function restore() {
  busy.value = true; error.value = "";
  try {
    acceptToken(await api("/api/identity/refresh", { method: "POST" }));
    await loadSubject(); emit("signedIn", "/account/sessions");
  } catch (failure) { forgetIdentity(); error.value = failure instanceof Error ? failure.message : "会话不可恢复，请重新登录。"; }
  finally { busy.value = false; }
}
</script>
<template>
  <section class="login-panel wes-page" aria-labelledby="login-title">
    <header><p class="eyebrow">WES · 身份验证</p><h1 id="login-title">登录运行管理</h1></header>
    <p>使用所属租户的账号。验证码错误或过期后需要重新获取，密码不会被浏览器应用缓存。</p>
    <p v-if="error" role="alert" class="error">{{ error }}</p>
    <form @submit.prevent="signIn" :aria-busy="busy">
      <label for="tenant">租户标识</label><input id="tenant" v-model="tenant" :disabled="busy" required maxlength="36" autocomplete="organization" placeholder="由管理员提供的租户 UUID">
      <label for="account">账号</label><input id="account" v-model="account" :disabled="busy" required maxlength="256" autocomplete="username">
      <label for="password">密码</label><input id="password" v-model="password" :disabled="busy" type="password" required maxlength="512" autocomplete="current-password">
      <div class="actions"><button type="button" @click="challenge" :disabled="busy || !tenant || !account">获取 / 更换验证码</button></div>
      <template v-if="image">
        <img :src="image" alt="六位验证码图片" width="220" height="64">
        <label for="answer">验证码（{{ new Date(expires).toLocaleTimeString() }} 前一次有效）</label>
        <input id="answer" v-model="answer" maxlength="6" autocomplete="off" autocapitalize="characters">
      </template>
      <div class="actions"><button type="submit" class="primary" :disabled="busy">{{ busy ? "正在处理" : "登录" }}</button>
        <button type="button" @click="restore" :disabled="busy">恢复当前浏览器会话</button></div>
    </form>
    <p class="hint">首次启动没有默认账号。请由管理员通过受控初始化流程创建租户与账号。身份系统未配置时请联系部署管理员。</p>
  </section>
</template>
