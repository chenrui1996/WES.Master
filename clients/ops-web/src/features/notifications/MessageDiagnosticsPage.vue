<script setup lang="ts">
import { computed, ref, useId, watch } from "vue";
import { canRead, sessionState } from "../identity/identity-api";
import { moduleCatalog } from "../module-catalog";
import MessagesPage from "./MessagesPage.vue";
import WmsCreationEvents from "./WmsCreationEvents.vue";
import { availableMessageOwners, type MessageOwner } from "./message-owners";

const id = useId();
const owners = computed(() => availableMessageOwners(code => moduleCatalog.isEnabled(code), canRead));
const owner = ref<MessageOwner | "">(owners.value[0]?.moduleCode ?? "");
const scope = computed(() => JSON.stringify(sessionState.subject));
const replayOpen = ref(false);
watch(owners, () => {
  owner.value = owners.value.some(item => item.moduleCode === owner.value) ? owner.value : owners.value[0]?.moduleCode ?? "";
}, { flush: "sync" });
watch(scope, () => { replayOpen.value = false; }, { flush: "sync" });
watch(owner, () => { replayOpen.value = false; }, { flush: "sync" });
</script>

<template>
  <div>
    <label :for="id">消息 owner</label>
    <select :id="id" v-model="owner" :disabled="replayOpen || !owners.length">
      <option v-for="item in owners" :key="item.moduleCode" :value="item.moduleCode">{{ item.moduleCode === "Wms" ? "WMS 创建事件" : "Platform" }}</option>
    </select>
    <p v-if="replayOpen" class="hint">重放预览或结果恢复期间不能切换 owner；请先在原操作面板明确关闭。</p>
    <p v-if="!owners.length" role="alert">当前模块与消息权限不允许读取任何已接入 owner，请返回会话页或联系管理员。</p>
    <MessagesPage v-if="owner === 'Platform' && owners.some(item => item.moduleCode === owner)"
      :key="`Platform-${scope}`" @replay-open="replayOpen = $event" />
    <WmsCreationEvents v-else-if="owner === 'Wms' && owners.some(item => item.moduleCode === owner)" :key="`Wms-${scope}`" />
  </div>
</template>
