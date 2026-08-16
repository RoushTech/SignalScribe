<script setup lang="ts">
import { ref, watch } from "vue";
import { ChannelType, type ChannelDto, type ChannelUpsertRequest } from "@/api/ChannelsApi";

const props = defineProps<{ modelValue: boolean; channel: ChannelDto | null }>();
const emit = defineEmits<{
  "update:modelValue": [value: boolean];
  save: [request: ChannelUpsertRequest];
}>();

const TYPE_OPTIONS = [
  { title: "Unknown", value: ChannelType.Unknown },
  { title: "Simplex", value: ChannelType.Simplex },
  { title: "Repeater output", value: ChannelType.RepeaterOutput },
];

const form = ref<ChannelUpsertRequest>(empty());

function empty(): ChannelUpsertRequest {
  return {
    frequencyHz: 146520000,
    label: "",
    type: ChannelType.Unknown,
    enabled: true,
    callsign: null,
    description: null,
    ctcssToneHz: null,
    notes: null,
  };
}

watch(
  () => props.modelValue,
  (open) => {
    if (open) {
      form.value = props.channel
        ? {
            frequencyHz: props.channel.frequencyHz,
            label: props.channel.label,
            type: props.channel.type,
            enabled: props.channel.enabled,
            callsign: props.channel.callsign,
            description: props.channel.description,
            ctcssToneHz: props.channel.ctcssToneHz,
            notes: props.channel.notes,
          }
        : empty();
    }
  },
);

// Users think in MHz; the API speaks Hz.
function mhz(): number {
  return form.value.frequencyHz / 1_000_000;
}

function setMhz(value: string) {
  const parsed = Number.parseFloat(value);
  if (!Number.isNaN(parsed)) {
    form.value.frequencyHz = Math.round(parsed * 1_000_000);
  }
}
</script>

<template>
  <v-dialog :model-value="modelValue" max-width="560" @update:model-value="emit('update:modelValue', $event)">
    <v-card :title="channel ? 'Edit channel' : 'New channel'">
      <v-card-text>
        <v-text-field
          :model-value="mhz()"
          label="Frequency (MHz)"
          type="number"
          step="0.0125"
          hint="Detected automatically, adjustable here"
          @update:model-value="setMhz"
        />
        <v-text-field v-model="form.label" label="Label" />
        <v-select v-model="form.type" :items="TYPE_OPTIONS" label="Type" />
        <v-text-field v-model="form.callsign" label="Repeater callsign" />
        <v-text-field v-model="form.description" label="Description" />
        <v-text-field v-model.number="form.ctcssToneHz" label="CTCSS/PL tone (Hz)" type="number" />
        <v-textarea v-model="form.notes" label="Notes" rows="2" />
        <v-switch v-model="form.enabled" label="Enabled" color="primary" />
      </v-card-text>
      <v-card-actions>
        <v-spacer />
        <v-btn @click="emit('update:modelValue', false)">Cancel</v-btn>
        <v-btn color="primary" @click="emit('save', form)">Save</v-btn>
      </v-card-actions>
    </v-card>
  </v-dialog>
</template>
