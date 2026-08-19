<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from "vue";
import type { HubConnection } from "@microsoft/signalr";
import {
  SettingsApi,
  type AvailableModelsDto,
  type CaptureSettingsDto,
  type WorkerSettingsDto,
} from "@/api/SettingsApi";
import { TransmissionsApi } from "@/api/TransmissionsApi";
import { connectStatusHub, type SdrDeviceInfo } from "@/api/statusHub";

const capture = ref<CaptureSettingsDto | null>(null);
const workers = ref<WorkerSettingsDto | null>(null);
const saved = ref<string | null>(null);
const error = ref<string | null>(null);
const devices = ref<SdrDeviceInfo[]>([]);
const models = ref<AvailableModelsDto>({ whisper: [], summary: [] });
let hub: HubConnection | null = null;

// Device list streams from the capture daemon over the status hub; selection is by serial.
const deviceOptions = computed(() => [
  { title: "First available", value: null as string | null },
  ...devices.value.map((d) => ({ title: `${d.model} — ${d.serial}${d.inUse ? " (active)" : ""}`, value: d.serial })),
  // Keep a configured-but-absent serial selectable so it isn't silently cleared on save.
  ...(capture.value?.deviceSerial && !devices.value.some((d) => d.serial === capture.value?.deviceSerial)
    ? [{ title: `${capture.value.deviceSerial} (not detected)`, value: capture.value.deviceSerial }]
    : []),
]);

// fs/spacing must be a power of two for the channelizer (512 or 256 channels at 12.5 kHz).
const SAMPLE_RATES = [
  { title: "6.4 MSPS — 512 channels (full 2m band, recommended)", value: 6_400_000 },
  { title: "3.2 MSPS — 256 channels (narrow)", value: 3_200_000 },
];

const DEVIATIONS = [
  { title: "±5 kHz — standard amateur NBFM", value: 5000 },
  { title: "±2.5 kHz — narrowband (louder)", value: 2500 },
  { title: "±3.5 kHz — mid", value: 3500 },
];

const CHANNEL_SPACINGS = [
  { title: "12.5 kHz", value: 12_500 },
  { title: "15 kHz", value: 15_000 },
  { title: "25 kHz", value: 25_000 },
];

// Short notes for the models we ship a download script for; anything else lists by filename.
const MODEL_NOTES: Record<string, string> = {
  "ggml-small.en-q5_1.bin": "fastest, decent",
  "ggml-medium.en-q5_0.bin": "more accurate",
  "ggml-large-v3-turbo-q5_0.bin": "best accuracy/speed",
  "ggml-large-v3-q5_0.bin": "most accurate, slowest",
};

function modelItems(files: string[], selected: string | undefined) {
  const items = files.map((f) => ({
    title: MODEL_NOTES[f] ? `${f} — ${MODEL_NOTES[f]}` : f,
    value: f,
  }));
  // A configured model that is not on disk stays selectable so saving cannot silently swap it.
  if (selected && !files.includes(selected)) {
    items.unshift({ title: `${selected} (not downloaded)`, value: selected });
  }
  return items;
}

const whisperModels = computed(() => modelItems(models.value.whisper, workers.value?.whisperModel));
const summaryModels = computed(() => modelItems(models.value.summary, workers.value?.summaryModel));

const THREAD_OPTIONS = computed(() => [
  { title: "Automatic — leaves one core free for capture", value: 0 },
  ...Array.from({ length: 16 }, (_, i) => ({ title: `${i + 1} thread${i ? "s" : ""}`, value: i + 1 })),
]);

function centerMhz(): number {
  return (capture.value?.centerFrequencyHz ?? 0) / 1_000_000;
}

function setCenterMhz(value: string) {
  const parsed = Number.parseFloat(value);
  if (capture.value && !Number.isNaN(parsed)) {
    capture.value.centerFrequencyHz = Math.round(parsed * 1_000_000);
  }
}

async function saveCapture() {
  if (!capture.value) return;
  try {
    capture.value = await SettingsApi.putCapture(capture.value);
    notify("Capture settings saved — pushed to the daemon");
  } catch (e: unknown) {
    error.value = `Save failed: ${e instanceof Error ? e.message : e}`;
  }
}

async function reprocessAll(onlyMissing: boolean) {
  const count = await TransmissionsApi.reprocessBulk(undefined, onlyMissing);
  notify(`Queued ${count} clip(s) for re-transcription`);
}

async function saveWorkers() {
  if (!workers.value) return;
  try {
    workers.value = await SettingsApi.putWorkers(workers.value);
    notify("Worker settings saved — pushed to the workers");
  } catch (e: unknown) {
    error.value = `Save failed: ${e instanceof Error ? e.message : e}`;
  }
}

function notify(message: string) {
  saved.value = message;
  setTimeout(() => (saved.value = null), 4000);
}

onMounted(async () => {
  [capture.value, workers.value, models.value] = await Promise.all([
    SettingsApi.getCapture(),
    SettingsApi.getWorkers(),
    SettingsApi.getModels(),
  ]);
  hub = await connectStatusHub((update) => {
    if (update.service === "capture" && update.devices) {
      devices.value = update.devices;
    }
  });
});

onUnmounted(() => {
  hub?.stop();
});
</script>

<template>
  <v-container>
    <v-alert v-if="error" type="error" closable class="mb-4" @click:close="error = null">{{ error }}</v-alert>
    <v-snackbar :model-value="!!saved" color="success" timeout="4000">{{ saved }}</v-snackbar>

    <v-card v-if="capture" title="Capture / RSP1" class="mb-6">
      <v-card-text>
        <v-alert type="info" variant="tonal" density="compact" class="mb-4">
          Frequency, sample rate, and channel spacing rebuild the capture pipeline (open recordings
          are closed and posted). Gain, AGC, and squelch apply live.
        </v-alert>
        <v-row dense>
          <v-col cols="12" md="6">
            <v-select
              v-model="capture.deviceSerial"
              :items="deviceOptions"
              label="Device"
              :hint="devices.length ? undefined : 'No devices reported by the capture daemon yet'"
              persistent-hint
            />
          </v-col>
        </v-row>
        <v-row dense>
          <v-col cols="12" md="4">
            <v-text-field
              :model-value="centerMhz()"
              label="Center frequency (MHz)"
              type="number"
              step="0.1"
              hint="Park between channels — the DC spike lands on no one"
              persistent-hint
              @update:model-value="setCenterMhz"
            />
          </v-col>
          <v-col cols="12" md="4">
            <v-select v-model="capture.sampleRateHz" :items="SAMPLE_RATES" label="Sample rate" />
          </v-col>
          <v-col cols="12" md="4">
            <v-select v-model="capture.channelSpacingHz" :items="CHANNEL_SPACINGS" label="Channel spacing" />
          </v-col>
        </v-row>
        <v-row dense>
          <v-col cols="12" md="4">
            <v-slider
              v-model="capture.gainReductionDb"
              label="Gain reduction (dB)"
              :min="20"
              :max="59"
              :step="1"
              thumb-label
              :disabled="capture.agcEnabled"
            />
          </v-col>
          <v-col cols="6" md="2">
            <v-select
              v-model="capture.lnaState"
              :items="[0, 1, 2, 3]"
              label="LNA state"
              :disabled="capture.agcEnabled"
            />
          </v-col>
          <v-col cols="6" md="2">
            <v-switch v-model="capture.agcEnabled" label="AGC" color="primary" />
          </v-col>
        </v-row>
        <v-row dense>
          <v-col cols="4">
            <v-text-field v-model.number="capture.squelchOpenDb" label="Squelch open (dB over floor)" type="number" />
          </v-col>
          <v-col cols="4">
            <v-text-field v-model.number="capture.squelchCloseDb" label="Squelch close (dB over floor)" type="number" />
          </v-col>
          <v-col cols="4">
            <v-text-field v-model.number="capture.squelchHangMs" label="Hang time (ms)" type="number" />
          </v-col>
        </v-row>
        <v-row dense>
          <v-col cols="6" md="3">
            <v-text-field
              :model-value="capture.monitorLowHz / 1e6"
              label="Monitor from (MHz)"
              type="number"
              step="0.1"
              hint="Outside this range nothing is gated or recorded"
              persistent-hint
              @update:model-value="(v: string) => capture && (capture.monitorLowHz = Math.round(Number.parseFloat(v) * 1e6))"
            />
          </v-col>
          <v-col cols="6" md="3">
            <v-text-field
              :model-value="capture.monitorHighHz / 1e6"
              label="Monitor to (MHz)"
              type="number"
              step="0.1"
              @update:model-value="(v: string) => capture && (capture.monitorHighHz = Math.round(Number.parseFloat(v) * 1e6))"
            />
          </v-col>
        </v-row>
        <v-row dense>
          <v-col cols="12" md="5">
            <v-select
              v-model="capture.deviationHz"
              :items="DEVIATIONS"
              label="Audio level reference (FM deviation)"
              hint="Deviation mapped to full-scale audio. Lower = louder; a soft limiter prevents clipping either way."
              persistent-hint
            />
          </v-col>
        </v-row>
      </v-card-text>
      <v-card-actions>
        <v-spacer />
        <v-btn color="primary" variant="flat" @click="saveCapture">Save capture settings</v-btn>
      </v-card-actions>
    </v-card>

    <v-card v-if="workers" title="Workers / Processing">
      <v-card-text>
        <v-row dense>
          <v-col cols="12" md="6">
            <v-select v-model="workers.whisperModel" :items="whisperModels" label="Whisper model" />
          </v-col>
          <v-col cols="12" md="6">
            <v-select v-model="workers.summaryModel" :items="summaryModels" label="Summary model" />
          </v-col>
        </v-row>
        <v-textarea
          v-model="workers.transcriptionPrompt"
          label="Transcription prompt"
          hint="Ham vocabulary, local repeater names, and known callsigns — seeds Whisper toward the right tokens"
          persistent-hint
          rows="3"
        />
        <v-row dense class="mt-2">
          <v-col cols="6" md="3">
            <v-select
              v-model.number="workers.transcriptionThreads"
              :items="THREAD_OPTIONS"
              label="Transcription CPU threads"
              hint="Whisper grabs every core by default, which starves the realtime capture daemon"
              persistent-hint
            />
          </v-col>
          <v-col cols="12" md="4">
            <v-select
              v-model.number="workers.summaryThreads"
              :items="THREAD_OPTIONS"
              label="Summary LLM CPU threads"
              hint="Applies to the local model that writes net summaries"
              persistent-hint
            />
          </v-col>
          <v-col cols="12" md="4">
            <v-text-field
              v-model.number="workers.maxJobsPerClaim"
              label="Jobs leased per poll"
              type="number"
              min="1"
              max="16"
              hint="Queue batching only — leased jobs still run one at a time, so this does not affect CPU"
              persistent-hint
            />
          </v-col>
          <v-col cols="6" md="3">
            <v-switch v-model="workers.paused" label="Pause processing" color="warning" />
          </v-col>
          <v-col cols="6" md="3">
            <v-text-field
              v-model.number="workers.discardRetentionHours"
              label="Keep discarded clips (hours)"
              type="number"
              min="1"
              hint="Rejected clips are reviewable for this long, then purged hourly"
              persistent-hint
            />
          </v-col>
          <v-col cols="6" md="3">
            <v-text-field
              v-model.number="workers.transcriptionGatherSeconds"
              label="Gather clips before transcribing (s)"
              type="number"
              min="0"
              max="120"
              hint="Whisper costs the same per run whether it holds 1s or 30s of audio, so waiting to fill a window turns several runs into one. 0 transcribes each clip as it arrives."
              persistent-hint
            />
          </v-col>
          <v-col cols="6" md="3">
            <v-text-field
              v-model.number="workers.noSpeechRetentionHours"
              label="Keep no-speech clips (hours)"
              type="number"
              min="1"
              hint="Kept recordings that settled as empty age out after this; digital voice and data clips never do"
              persistent-hint
            />
          </v-col>
        </v-row>
      </v-card-text>
      <v-card-actions>
        <v-btn variant="tonal" prepend-icon="mdi-refresh" @click="reprocessAll(true)">
          Transcribe missing
        </v-btn>
        <v-btn variant="tonal" prepend-icon="mdi-refresh-auto" @click="reprocessAll(false)">
          Re-transcribe all
        </v-btn>
        <v-spacer />
        <v-btn color="primary" variant="flat" @click="saveWorkers">Save worker settings</v-btn>
      </v-card-actions>
    </v-card>
  </v-container>
</template>
