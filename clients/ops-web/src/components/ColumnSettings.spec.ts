import { mount } from "@vue/test-utils";
import { afterEach, expect, it } from "vitest";
import ColumnSettings from "./ColumnSettings.vue";
import { defineComponent, ref } from "vue";
import { forgetIdentity, sessionState } from "../features/identity/identity-api";
import { columnWidthPreset, useColumns } from "./useColumns";

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
    template: `<ColumnSettings :columns="available" v-model="selected" /><button data-test="revoke-fields" @click="revoke">撤权</button><output>{{ visible.map(c => c.key).join(',') }}</output>`,
  });
  const wrapper = mount(host);
  expect(wrapper.get("output").text()).toBe("id,name");
  await wrapper.get('[data-test="column-name"]').trigger("keydown", { key: "ArrowUp" });
  expect(wrapper.findAll('li[data-test^="column-"]').map(item => item.attributes("data-test")))
    .toEqual(["column-name", "column-id"]);
  expect(wrapper.get("output").text()).toBe("id,name");
  await wrapper.get('[data-test="column-name"]').trigger("keydown", { key: "ArrowDown" });
  expect(wrapper.findAll('li[data-test^="column-"]').map(item => item.attributes("data-test")))
    .toEqual(["column-id", "column-name"]);
  await wrapper.get('[data-test="column-name"]').trigger("keydown", { key: "ArrowUp" });
  await wrapper.get("form").trigger("submit");
  expect(wrapper.get("output").text()).toBe("name,id");
  await wrapper.get('input[value="name"]').setValue(false);
  expect(wrapper.get("output").text()).toBe("name,id");
  await wrapper.get('[data-test="cancel-columns"]').trigger("click");
  expect(wrapper.get("output").text()).toBe("name,id");
  await wrapper.get('input[value="name"]').setValue(false);
  await wrapper.get("form").trigger("submit");
  expect(wrapper.get("output").text()).toBe("id");
  await wrapper.get('[data-test="restore-columns"]').trigger("click");
  expect(wrapper.get("output").text()).toBe("id");
  await wrapper.get("form").trigger("submit");
  expect(wrapper.get("output").text()).toBe("id,name");
  await wrapper.get('[data-test="revoke-fields"]').trigger("click");
  expect(wrapper.find('input[value="name"]').exists()).toBe(false);
  expect(wrapper.get("output").text()).toBe("id");
  wrapper.unmount();
});

it("restores only the column layout without clearing filters or business data", async () => {
  const host = defineComponent({
    components: { ColumnSettings },
    setup() {
      return {
        columns: [
          { key: "id", label: "标识", required: true },
          { key: "name", label: "名称" },
          { key: "state", label: "状态" },
        ],
        selected: ref(["id"]),
        widths: ref({ name: 240 }),
        fixedFirstColumn: ref(false),
        filter: ref("WA"),
        row: ref({ id: "R-1", quantity: 3 }),
      };
    },
    template: `<ColumnSettings :columns="columns" v-model="selected" v-model:widths="widths"
        v-model:fixed-first-column="fixedFirstColumn" />
      <input data-test="filter" v-model="filter">
      <output data-test="selected">{{ selected.join(',') }}</output>
      <output data-test="widths">{{ JSON.stringify(widths) }}</output>
      <output data-test="fixed">{{ fixedFirstColumn }}</output>
      <output data-test="business">{{ row.id }}|{{ row.quantity }}</output>`,
  });
  const wrapper = mount(host);
  expect(wrapper.get('[data-test="selected"]').text()).toBe("id");

  await wrapper.get('[data-test="restore-columns"]').trigger("click");

  expect(wrapper.get('[data-test="selected"]').text()).toBe("id");
  expect(wrapper.get('[data-test="filter"]').element).toHaveProperty("value", "WA");
  expect(wrapper.get('[data-test="business"]').text()).toBe("R-1|3");

  await wrapper.get("form").trigger("submit");

  expect(wrapper.get('[data-test="selected"]').text()).toBe("id,name,state");
  expect(wrapper.get('[data-test="widths"]').text()).toBe("{}");
  expect(wrapper.get('[data-test="fixed"]').text()).toBe("true");
  expect(wrapper.get('[data-test="filter"]').element).toHaveProperty("value", "WA");
  expect(wrapper.get('[data-test="business"]').text()).toBe("R-1|3");
  wrapper.unmount();
});

it("rejects widths above the category maximum and persists the exact upper bound", async () => {
  sessionState.subject = {
    tenantId: "tenant-a", userId: "user-a", sessionId: "session-a",
    requiresPasswordChange: false, credentialVersion: null, grants: [],
  };
  const host = defineComponent({
    components: { ColumnSettings },
    setup() {
      const columns = useColumns(() => [
        { key: "id", label: "标识", required: true },
        { key: "name", label: "姓名", widthClass: "identity" },
      ], { environment: "test", pageId: "users", tableId: "directory" });
      return { ...columns };
    },
    template: `<ColumnSettings :columns="available" v-model="selected" v-model:widths="widths" />
      <output data-test="name-width">{{ widthStyle('name')?.width ?? 'auto' }}</output>`,
  });
  const wrapper = mount(host);
  const width = wrapper.get('[data-test="width-name"]');
  await width.setValue("601");
  await width.trigger("change");
  expect((width.element as HTMLInputElement).validationMessage).toContain("160 至 600");
  await wrapper.get('[data-test="apply-columns"]').trigger("submit");
  expect(wrapper.get('[data-test="name-width"]').text()).toBe("200px");
  expect(localStorage.getItem("wes-ops-web:columns:v1:test:tenant-a:user-a:users:directory")).not.toContain('"name":601');

  await width.setValue("159");
  await width.trigger("change");
  expect((width.element as HTMLInputElement).validationMessage).toContain("160 至 600");
  await wrapper.get('[data-test="apply-columns"]').trigger("submit");
  expect(wrapper.get('[data-test="name-width"]').text()).toBe("200px");

  await width.setValue("160");
  await width.trigger("change");
  expect((width.element as HTMLInputElement).validationMessage).toBe("");
  await wrapper.get('[data-test="apply-columns"]').trigger("submit");
  expect(wrapper.get('[data-test="name-width"]').text()).toBe("160px");

  await width.setValue("600");
  await width.trigger("change");
  expect((width.element as HTMLInputElement).validationMessage).toBe("");
  await wrapper.get('[data-test="apply-columns"]').trigger("submit");
  expect(wrapper.get('[data-test="name-width"]').text()).toBe("600px");
  expect(localStorage.getItem("wes-ops-web:columns:v1:test:tenant-a:user-a:users:directory")).toContain('"name":600');
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
    template: `<ColumnSettings :columns="available" v-model="selected" v-model:widths="widths"
        v-model:fixed-first-column="fixedFirstColumn" />
      <output>{{ widthStyle('name')?.width ?? 'auto' }}</output><output data-test="fixed">{{ fixedFirstColumn }}</output>`,
  });
  const wrapper = mount(host);
  expect((wrapper.get('[data-test="fixed-first-column"]').element as HTMLInputElement).checked).toBe(true);
  await wrapper.get('[data-test="width-name"]').setValue("220");
  expect(wrapper.get("output").text()).toBe("200px");
  await wrapper.get('[data-test="fixed-first-column"]').setValue(false);
  expect(wrapper.get('[data-test="fixed"]').text()).toBe("true");
  await wrapper.get('[data-test="fixed-first-column"]').setValue(true);
  await wrapper.get("form").trigger("submit");
  expect(wrapper.get("output").text()).toBe("220px");
  expect(wrapper.get('[data-test="fixed"]').text()).toBe("true");
  expect(JSON.parse(localStorage.getItem("wes-ops-web:columns:v1:test:tenant-a:user-a:users:directory") ?? "{}"))
    .toEqual({ version: 1, selected: ["id", "name"], knownColumns: ["id", "name"],
      widths: { name: 220 }, fixedFirstColumn: true });
  await wrapper.get('[data-test="width-name"]').setValue("");
  expect(wrapper.get("output").text()).toBe("220px");
  await wrapper.get("form").trigger("submit");
  expect(wrapper.get("output").text()).toBe("200px");
  expect(wrapper.get('[data-test="width-name"]').attributes("min")).toBe("160");
  expect(wrapper.get('[data-test="width-name"]').attributes("max")).toBe("600");
  wrapper.unmount();
});

it("uses the field-category width baselines from the dense-page contract", () => {
  expect(columnWidthPreset({ key: "name", label: "名称", widthClass: "identity" }))
    .toEqual({ defaultWidth: 200, minWidth: 160, maxWidth: 600 });
  expect(columnWidthPreset({ key: "state", label: "状态", widthClass: "status" }))
    .toEqual({ defaultWidth: 120, minWidth: 100, maxWidth: 600 });
  expect(columnWidthPreset({ key: "count", label: "数量", widthClass: "numeric" }))
    .toEqual({ defaultWidth: 120, minWidth: 100, maxWidth: 600 });
  expect(columnWidthPreset({ key: "createdAt", label: "时间", widthClass: "time" }))
    .toEqual({ defaultWidth: 200, minWidth: 180, maxWidth: 600 });
  expect(columnWidthPreset({ key: "description", label: "说明", widthClass: "description" }))
    .toEqual({ defaultWidth: 240, minWidth: 180, maxWidth: 600 });
});

it("offers fixed zones for optional columns while keeping the primary identifier controlled separately", async () => {
  const host = defineComponent({
    components: { ColumnSettings },
    setup() {
      const columns = useColumns(() => [
        { key: "id", label: "标识", required: true },
        { key: "name", label: "姓名" },
      ], { environment: "test", pageId: "users", tableId: "directory" });
      return { ...columns, fixedPositions: ref<Record<string, "left" | "right">>({}) };
    },
    template: `<ColumnSettings :columns="available" v-model="selected" v-model:widths="widths"
        v-model:fixed-first-column="fixedFirstColumn" v-model:fixed-positions="fixedPositions" />
      <output data-test="position">{{ fixedPositions.name ?? 'center' }}</output>`,
  });
  const wrapper = mount(host);

  await wrapper.get('[data-test="fixed-position-name"]').setValue("left");
  await wrapper.get('[data-test="cancel-columns"]').trigger("click");
  expect(wrapper.get('[data-test="position"]').text()).toBe("center");
  await wrapper.get('[data-test="fixed-position-name"]').setValue("right");
  await wrapper.get("form").trigger("submit");
  expect(wrapper.get('[data-test="position"]').text()).toBe("right");
  expect(wrapper.find('[data-test="fixed-position-id"]').exists()).toBe(false);
  wrapper.unmount();
});

it("requires confirmation before abandoning an unsaved personal view", async () => {
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
      return { ...columns, activeView: columns.columnViews.activeViewId };
    },
    template: `<ColumnSettings :columns="available" v-model="selected" v-model:widths="widths"
        v-model:fixed-first-column="fixedFirstColumn" :column-views="columnViews" />
      <output data-test="active-view">{{ activeView }}</output>`,
  });
  const wrapper = mount(host);
  await wrapper.get('[data-test="save-view-as"]').trigger("click");
  await wrapper.get("#column-view-name-create").setValue("Operations");
  await wrapper.get('[data-test="confirm-view"]').trigger("click");
  const id = wrapper.get('[data-test="active-view"]').text();
  expect(id).not.toBe("");
  await wrapper.get('[data-test="column-name"] input').setValue(false);
  await wrapper.get("details.column-settings form").trigger("submit");
  expect(wrapper.get('[data-test="active-view"]').text()).toBe(id);
  expect(wrapper.get('[data-test="save-view"]').text()).toContain("保存视图");
  await wrapper.get('[aria-label="当前个人视图"]').setValue("");
  expect(wrapper.get('[aria-label="确认个人视图操作"]').text()).toContain("放弃");
  await wrapper.get('[data-test="cancel-view"]').trigger("click");
  expect(wrapper.get('[data-test="active-view"]').text()).toBe(id);
  await wrapper.get('[aria-label="当前个人视图"]').setValue("");
  await wrapper.get('[data-test="confirm-view"]').trigger("click");
  expect(wrapper.get('[data-test="active-view"]').text()).toBe("");
  wrapper.unmount();
});
