<script setup>
import { onMounted, ref } from "vue";

const props = defineProps({ api: { type: Object, required: true } });
const state = ref({ status: "pending" });

/** 读取 context，在存储目录中写入并读回 vue.json。Reads the context, then writes and reads back vue.json in storage. */
async function roundTrip(api) {
  const { extension } = api.context.get();
  const uri = `${extension.storageUri}/vue.json`;
  const written = JSON.stringify({ extensionId: extension.id, nonce: crypto.randomUUID() });
  await api.fs.writeFile(uri, written);
  const read = await api.fs.readTextFile(uri);
  if (read !== written) throw new Error(`Storage round-trip mismatch: ${read}`);
}

onMounted(() => {
  void roundTrip(props.api).then(
    () => {
      state.value = { status: "ok" };
    },
    (error) => {
      state.value = { status: "error", message: String(error) };
      throw error;
    },
  );
});
</script>

<template>
  <p v-if="state.status === 'ok'" id="result">vue ok</p>
  <p v-else-if="state.status === 'error'" id="error">{{ state.message }}</p>
  <p v-else id="status">Loading</p>
</template>
