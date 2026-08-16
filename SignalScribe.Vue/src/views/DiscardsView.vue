<script setup lang="ts">
import { onMounted, ref } from "vue";
import { DiscardsApi, type DiscardDto, type DiscardStatsDto } from "@/api/DiscardsApi";
import { discardDetail, discardLabel } from "@/lib/discardReason";
import { toneLabel, toneName } from "@/lib/squelchTone";
import { SettingsApi, type WorkerSettingsDto } from "@/api/SettingsApi";
import { formatLocal } from "@/lib/time";

const discards = ref<DiscardDto[]>([]);
const stats = ref<DiscardStatsDto | null>(null);
const settings = ref<WorkerSettingsDto | null>(null);
const busy = ref(false);

async function refresh() {
  [discards.value, stats.value, settings.value] = await Promise.all([
    DiscardsApi.getRecent(),
    DiscardsApi.getStats(),
    SettingsApi.getWorkers(),
  ]);
}

async function purge() {
  busy.value = true;
  try {
    await DiscardsApi.purgeAll();
    await refresh();
  } finally {
    busy.value = false;
  }
}

onMounted(refresh);
</script>

<template>
  <v-container>
    <v-card>
      <v-card-item>
        <v-card-title>Discarded clips</v-card-title>
        <v-card-subtitle>
          Recordings the capture gate rejected. Kept
          {{ settings?.discardRetentionHours ?? 24 }}h for review, then purged automatically.
        </v-card-subtitle>
        <template #append>
          <v-btn variant="tonal" prepend-icon="mdi-refresh" class="mr-2" @click="refresh">Refresh</v-btn>
          <v-btn color="error" variant="tonal" prepend-icon="mdi-delete-sweep" :loading="busy" @click="purge">
            Purge now
          </v-btn>
        </template>
      </v-card-item>

      <v-card-text v-if="stats">
        <v-chip class="mr-2" size="small" variant="tonal">{{ stats.total }} total</v-chip>
        <v-tooltip v-for="r in stats.byReason" :key="r.reason" :text="discardDetail(r.reason)" location="bottom">
          <template #activator="{ props }">
            <v-chip v-bind="props" class="mr-2" size="small" variant="outlined">
              {{ discardLabel(r.reason) }}: {{ r.count }}
            </v-chip>
          </template>
        </v-tooltip>
        <span v-if="stats.oldestUtc" class="text-caption text-medium-emphasis ml-2">
          oldest: {{ formatLocal(stats.oldestUtc) }}
        </span>
      </v-card-text>

      <v-table>
        <thead>
          <tr>
            <th>Frequency</th>
            <th>When</th>
            <th>Length</th>
            <th>Audio</th>
            <th>Why it was dropped</th>
            <th>Tone</th>
            <th>Measurements</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="d in discards" :key="d.id">
            <td style="white-space: nowrap">{{ (d.frequencyHz / 1e6).toFixed(4) }} MHz</td>
            <td style="white-space: nowrap">{{ formatLocal(d.startUtc) }}</td>
            <td>{{ (d.durationMs / 1000).toFixed(1) }}s</td>
            <td>
              <audio :src="DiscardsApi.audioUrl(d.id)" controls preload="none" style="height: 30px; width: 200px" />
            </td>
            <td>
              <v-tooltip :text="discardDetail(d.reason)" location="bottom" max-width="360">
                <template #activator="{ props }">
                  <v-chip v-bind="props" size="small" variant="tonal" style="cursor: help">
                    {{ discardLabel(d.reason) }}
                  </v-chip>
                </template>
              </v-tooltip>
            </td>
            <td>
              <v-tooltip
                v-if="toneName(d.ctcssHz, d.dcsCode)"
                :text="`${toneName(d.ctcssHz, d.dcsCode)}, read from under this clip. Tells you whose system it was, even though we threw the audio away.`"
                location="bottom"
                max-width="340"
              >
                <template #activator="{ props }">
                  <v-chip v-bind="props" size="x-small" variant="outlined" style="cursor: help">
                    <v-icon start size="x-small" icon="mdi-tune" />
                    {{ toneLabel(d.ctcssHz, d.dcsCode) }}
                  </v-chip>
                </template>
              </v-tooltip>
              <span v-else class="text-medium-emphasis">—</span>
            </td>
            <td class="text-caption text-medium-emphasis" style="white-space: nowrap">
              voiced {{ d.voicedMs }}ms · speech {{ (d.speechBandRatio * 100).toFixed(0) }}% ·
              syllables {{ d.syllableRateHz.toFixed(1) }}Hz · peak {{ d.peakDbfs.toFixed(0) }}dB
            </td>
          </tr>
          <tr v-if="!discards.length">
            <td colspan="7" class="text-medium-emphasis">Nothing discarded recently.</td>
          </tr>
        </tbody>
      </v-table>
    </v-card>
  </v-container>
</template>
