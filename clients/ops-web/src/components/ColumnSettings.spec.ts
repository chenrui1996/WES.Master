import { mount } from "@vue/test-utils";
import { afterEach, expect, it } from "vitest";
import ColumnSettings from "./ColumnSettings.vue";
import { defineComponent, ref } from "vue";
import { forgetIdentity, sessionState } from "../features/identity/identity-api";
import { useColumns } from "./useColumns";

afterEach(() => { forgetIdentity(); localStorage.clear(); });

it("supports visibility and keyboard ordering without offering revoked fields", async () => {
  const host = defineComponent({
    components: { ColumnSettings },
    setup() {
      const fields = ref(["id", "name"]);
      const columns = useColumns(() => [
        { key: "id", label: "标识", required: true },
        ...fields.value.includes("name") ? [{ key: "name", label: "姓名" }] : [],
      ], { environment: "test", pageId: "test", tableId: "columns" });
      return { ...columns, revoke: () => { fields.value = ["id"]; } };
    },
    template: `<ColumnSettings :columns="available" v-model="selected" /><button @click="revoke">撤权</button><output>{{ visible.map(c => c.key).join(',') }}</output>`,
  });
  const wrapper = mount(host);
  expect(wrapper.get("output").text()).toBe("id,name");
  await wrapper.get('[data-test="column-name"]').trigger("keydown", { key: "ArrowUp" });
  expect(wrapper.get("output").text()).toBe("name,id");
  await wrapper.get('input[value="name"]').setValue(false);
  expect(wrapper.get("output").text()).toBe("id");
  await wrapper.get("button:last-of-type").trigger("click");
  expect(wrapper.find('input[value="name"]').exists()).toBe(false);
  expect(wrapper.get("output").text()).toBe("id");
  wrapper.unmount();
});

it("announces when column preferences could not be loaded or saved", () => {
  const wrapper = mount(ColumnSettings, {
    props: {
      columns: [{ key: "id", label: "标识", required: true }],
      modelValue: ["id"],
      preferenceWarning: "列偏好格式无效，已恢复为当前可读列默认值。",
    },
  });

  expect(wrapper.get('[role="status"]').text()).toContain("列偏好格式无效");
  wrapper.unmount();
});

it("applies and persists bounded per-column widths", async () => {
  sessionState.subject = {
    tenantId: "tenant-a", userId: "user-a", sessionId: "session-a",
    requiresPasswordChange: false, credentialVersion: null, grants: [],
  };
  const host = defineComponent({
    components: { ColumnSettings },
    setup() {
      const columns = useColumns(() => [
        { key: "id", label: "标识", required: true },
        { key: "name", label: "姓名" },
      ], { environment: "test", pageId: "users", tableId: "directory" });
      return { ...columns };
    },
    template: `<ColumnSettings :columns="available" v-model="selected" v-model:widths="widths" />
      <output>{{ widthStyle('name')?.width ?? 'auto' }}</output>`,
  });
  const wrapper = mount(host);
  await wrapper.get('[data-test="width-name"]').setValue("220");
  expect(wrapper.get("output").text()).toBe("220px");
  expect(JSON.parse(localStorage.getItem("wes-ops-web:columns:v1:test:tenant-a:user-a:users:directory") ?? "{}"))
    .toEqual({ version: 1, selected: ["id", "name"], widths: { name: 220 } });
  await wrapper.get('[data-test="width-name"]').setValue("");
  expect(wrapper.get("output").text()).toBe("auto");
  wrapper.unmount();
});
