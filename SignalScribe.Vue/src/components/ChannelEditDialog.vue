<script setup lang="ts">
import { computed, ref, watch } from "vue";
import { ChannelType, type ChannelDto, type ChannelUpsertRequest } from "@/api/ChannelsApi";
import { DetectedMode, modeLabel } from "@/lib/detectedMode";
import { CTCSS_TONES, DCS_CODES, formatDcs, formatNoiseFloor } from "@/lib/squelchTone";

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

// Null means "whatever capture measures", which is the right default — the classifier is usually
// right and an operator override exists for the cases where it is not.
const MODE_OPTIONS = [
  { title: "Auto-detect", value: null },
  ...[
    DetectedMode.AnalogFm,
    DetectedMode.Dmr,
    DetectedMode.DStar,
    DetectedMode.Ysf,
    DetectedMode.P25Phase1,
    DetectedMode.Nxdn,
    DetectedMode.M17,
    DetectedMode.Afsk1200,
    DetectedMode.Fsk9600,
    DetectedMode.Pocsag,
    DetectedMode.Flex,
  ].map((m) => ({ title: modeLabel(m), value: m })),
];

// CTCSS and DCS are alternative squelch systems — a channel runs one or the other, never both, so
// the UI offers a choice rather than two fields that could contradict each other.
type ToneMode = "none" | "ctcss" | "dcs";

const TONE_MODES = [
  { title: "None", value: "none" as ToneMode },
  { title: "CTCSS", value: "ctcss" as ToneMode },
  { title: "DCS", value: "dcs" as ToneMode },
];

const form = ref<ChannelUpsertRequest>(empty());
const toneMode = ref<ToneMode>("none");

// A measured tone can land off the standard table; keep whatever the channel already carries in
// the list so opening the dialog never silently drops it.
const ctcssItems = computed(() => {
  const set = new Set(CTCSS_TONES);
  if (form.value.ctcssToneHz) set.add(form.value.ctcssToneHz);
  return [...set].sort((a, b) => a - b).map((t) => ({ title: `${t.toFixed(1)} Hz`, value: t }));
});

const dcsItems = computed(() => {
  const set = new Set(DCS_CODES);
  if (form.value.dcsCode) set.add(form.value.dcsCode);
  return [...set].sort((a, b) => a - b).map((c) => ({ title: formatDcs(c), value: c }));
});

/** What capture has learned, independent of what the form is about to pin. */
const learnedFloor = computed(() => props.channel?.noiseFloorDbfs ?? null);

function empty(): ChannelUpsertRequest {
  return {
    frequencyHz: 146520000,
    label: "",
    type: ChannelType.Unknown,
    enabled: true,
    callsign: null,
    description: null,
    ctcssToneHz: null,
    dcsCode: null,
    notes: null,
    modulation: null,
    adaptiveSquelch: true,
    noiseFloorDbfs: null,
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
            dcsCode: props.channel.dcsCode,
            notes: props.channel.notes,
            modulation: props.channel.modulation,
            adaptiveSquelch: props.channel.adaptiveSquelch,
            noiseFloorDbfs: props.channel.noiseFloorDbfs,
          }
        : empty();
      toneMode.value = form.value.ctcssToneHz ? "ctcss" : form.value.dcsCode ? "dcs" : "none";
    }
  },
);

// Switching systems clears the other one, so the request can never carry both.
function setToneMode(mode: ToneMode | null) {
  if (!mode) return;
  toneMode.value = mode;
  if (mode !== "ctcss") form.value.ctcssToneHz = null;
  if (mode !== "dcs") form.value.dcsCode = null;
}

// Turning adaptive off pins the floor at whatever the daemon has learned so far: the operator means
// "stop moving from here", not "start from nothing".
function setAdaptive(on: boolean | null) {
  form.value.adaptiveSquelch = on === true;
  form.value.noiseFloorDbfs = form.value.adaptiveSquelch ? null : (form.value.noiseFloorDbfs ?? learnedFloor.value);
}

/**
 * Last word on the two rules the server also enforces: never both tone systems, and no pinned floor
 * while adaptive is on. A cleared number field leaves a non-number behind, so normalise that too.
 */
function submit() {
  const request: ChannelUpsertRequest = { ...form.value };
  if (toneMode.value !== "ctcss") request.ctcssToneHz = null;
  if (toneMode.value !== "dcs") request.dcsCode = null;
  if (request.adaptiveSquelch || !Number.isFinite(request.noiseFloorDbfs)) request.noiseFloorDbfs = null;
  emit("save", request);
}

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
        <v-select
          v-model="form.modulation"
          :items="MODE_OPTIONS"
          label="Modulation"
          hint="Leave on auto-detect unless capture is getting it wrong."
          persistent-hint
        />
        <v-text-field v-model="form.callsign" label="Repeater callsign" />
        <v-text-field v-model="form.description" label="Description" />

        <div class="text-subtitle-2 mt-2">Squelch tone</div>
        <div class="text-caption text-medium-emphasis mb-2">
          CTCSS and DCS are alternative systems — a channel runs one or the other.
        </div>
        <v-btn-toggle
          :model-value="toneMode"
          mandatory
          density="comfortable"
          variant="outlined"
          divided
          class="mb-4"
          @update:model-value="setToneMode"
        >
          <v-btn v-for="m in TONE_MODES" :key="m.value" :value="m.value">{{ m.title }}</v-btn>
        </v-btn-toggle>
        <v-select
          v-if="toneMode === 'ctcss'"
          v-model="form.ctcssToneHz"
          :items="ctcssItems"
          label="CTCSS/PL tone"
          clearable
        />
        <v-select
          v-if="toneMode === 'dcs'"
          v-model="form.dcsCode"
          :items="dcsItems"
          label="DCS code"
          hint="Octal, as operators write it."
          persistent-hint
          clearable
        />

        <div class="text-subtitle-2 mt-4">Squelch</div>
        <v-switch
          :model-value="form.adaptiveSquelch"
          label="Adaptive squelch"
          color="primary"
          density="comfortable"
          hide-details
          @update:model-value="setAdaptive"
        />
        <!-- Adaptive: the floor is the daemon's to move, so show it rather than offer it. -->
        <v-text-field
          v-if="form.adaptiveSquelch"
          :model-value="formatNoiseFloor(learnedFloor)"
          label="Noise floor (learned)"
          readonly
          persistent-hint
          hint="Capture keeps re-learning this. Switch adaptive off to pin it where it is."
        />
        <v-text-field
          v-else
          v-model.number="form.noiseFloorDbfs"
          label="Noise floor (dBFS)"
          type="number"
          step="0.1"
          persistent-hint
          :hint="`Pinned. Capture last learned ${formatNoiseFloor(learnedFloor)}.`"
        />

        <v-textarea v-model="form.notes" label="Notes" rows="2" class="mt-4" />
        <v-switch v-model="form.enabled" label="Enabled" color="primary" />
      </v-card-text>
      <v-card-actions>
        <v-spacer />
        <v-btn @click="emit('update:modelValue', false)">Cancel</v-btn>
        <v-btn color="primary" @click="submit">Save</v-btn>
      </v-card-actions>
    </v-card>
  </v-dialog>
</template>
