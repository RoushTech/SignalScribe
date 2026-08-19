<script setup lang="ts">
import { ref, watch } from "vue";
import type { NetDto, NetUpsertRequest } from "@/api/NetsApi";
import { DAY_NAMES, localDailyToUtc, localWeeklyToUtc, utcDailyToLocal, utcWeeklyToLocal } from "@/lib/time";

const props = defineProps<{ modelValue: boolean; channelId: number; net: NetDto | null }>();
const emit = defineEmits<{
  "update:modelValue": [value: boolean];
  save: [request: NetUpsertRequest];
}>();

const DAILY = "daily" as const;

// A net with no day runs every day, so "Daily" belongs in the same picker as the weekdays rather
// than in a separate mode toggle — it is one answer to "how often?", not a different question.
const REPEAT_OPTIONS = [
  { title: "Daily", value: DAILY as number | typeof DAILY },
  ...DAY_NAMES.map((day, value) => ({ title: `${day}s`, value: value as number | typeof DAILY })),
];

const name = ref<string | null>(null);
const description = ref<string | null>(null);
const repeatLocal = ref<number | typeof DAILY | null>(null);
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
    if (!props.net?.startTimeUtc) {
      repeatLocal.value = null;
      timeLocal.value = null;
    } else if (props.net.dayOfWeekUtc == null) {
      repeatLocal.value = DAILY;
      timeLocal.value = utcDailyToLocal(props.net.startTimeUtc);
    } else {
      const local = utcWeeklyToLocal(props.net.dayOfWeekUtc, props.net.startTimeUtc);
      repeatLocal.value = local.day;
      timeLocal.value = local.time;
    }
  },
);

function save() {
  let dayOfWeekUtc: number | null = null;
  let startTimeUtc: string | null = null;
  if (timeLocal.value && repeatLocal.value === DAILY) {
    startTimeUtc = localDailyToUtc(timeLocal.value);
  } else if (timeLocal.value && typeof repeatLocal.value === "number") {
    const utc = localWeeklyToUtc(repeatLocal.value, timeLocal.value);
    dayOfWeekUtc = utc.day;
    startTimeUtc = utc.time;
  }

  emit("save", {
    channelId: props.channelId,
    name: name.value,
    description: description.value,
    dayOfWeekUtc,
    startTimeUtc,
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
            <v-select v-model="repeatLocal" :items="REPEAT_OPTIONS" label="Repeats (your local time)" />
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
