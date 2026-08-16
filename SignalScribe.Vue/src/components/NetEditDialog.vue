<script setup lang="ts">
import { ref, watch } from "vue";
import type { NetDto, NetUpsertRequest } from "@/api/NetsApi";
import { DAY_NAMES, localWeeklyToUtc, utcWeeklyToLocal } from "@/lib/time";

const props = defineProps<{ modelValue: boolean; channelId: number; net: NetDto | null }>();
const emit = defineEmits<{
  "update:modelValue": [value: boolean];
  save: [request: NetUpsertRequest];
}>();

const DAY_OPTIONS = DAY_NAMES.map((title, value) => ({ title, value }));

const name = ref<string | null>(null);
const description = ref<string | null>(null);
const dayLocal = ref<number | null>(null);
const timeLocal = ref<string | null>(null);
const durationMinutes = ref<number | null>(60);

watch(
  () => props.modelValue,
  (open) => {
    if (!open) return;
    name.value = props.net?.name ?? null;
    description.value = props.net?.description ?? null;
    durationMinutes.value = props.net?.durationMinutes ?? 60;
    // Schedule is stored UTC; entry/display is local (browser converts).
    if (props.net?.dayOfWeekUtc != null && props.net?.startTimeUtc) {
      const local = utcWeeklyToLocal(props.net.dayOfWeekUtc, props.net.startTimeUtc);
      dayLocal.value = local.day;
      timeLocal.value = local.time;
    } else {
      dayLocal.value = null;
      timeLocal.value = null;
    }
  },
);

function save() {
  const utc = dayLocal.value != null && timeLocal.value ? localWeeklyToUtc(dayLocal.value, timeLocal.value) : null;
  emit("save", {
    channelId: props.channelId,
    name: name.value,
    description: description.value,
    dayOfWeekUtc: utc?.day ?? null,
    startTimeUtc: utc?.time ?? null,
    durationMinutes: durationMinutes.value,
  });
}
</script>

<template>
  <v-dialog :model-value="modelValue" max-width="560" @update:model-value="emit('update:modelValue', $event)">
    <v-card :title="net ? 'Edit net' : 'New net'">
      <v-card-text>
        <v-text-field v-model="name" label="Net name" />
        <v-text-field v-model="description" label="Description" />
        <v-row dense>
          <v-col cols="5">
            <v-select v-model="dayLocal" :items="DAY_OPTIONS" label="Day (your local time)" />
          </v-col>
          <v-col cols="4">
            <v-text-field v-model="timeLocal" label="Start time (local)" type="time" />
          </v-col>
          <v-col cols="3">
            <v-text-field v-model.number="durationMinutes" label="Duration (min)" type="number" />
          </v-col>
        </v-row>
        <div class="text-caption text-medium-emphasis">
          Sessions on this channel inside the window are classified as occurrences of this net.
        </div>
      </v-card-text>
      <v-card-actions>
        <v-spacer />
        <v-btn @click="emit('update:modelValue', false)">Cancel</v-btn>
        <v-btn color="primary" @click="save">Save</v-btn>
      </v-card-actions>
    </v-card>
  </v-dialog>
</template>
