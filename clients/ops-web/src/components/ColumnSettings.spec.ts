import { mount } from "@vue/test-utils";
import { expect, it } from "vitest";
import ColumnSettings from "./ColumnSettings.vue";
import { defineComponent, ref } from "vue";
import { useColumns } from "./useColumns";

it("supports visibility and keyboard ordering without offering revoked fields", async () => {
  const host = defineComponent({
    components: { ColumnSettings },
    setup() {
      const fields = ref(["id", "name"]);
      const columns = useColumns(() => [
        { key: "id", label: "标识", required: true },
        ...fields.value.includes("name") ? [{ key: "name", label: "姓名" }] : [],
      ]);
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
