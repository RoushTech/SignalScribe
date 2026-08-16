<script setup lang="ts">
import { onMounted, ref } from "vue";
import { DiscardsApi, type DiscardDto, type DiscardStatsDto } from "@/api/DiscardsApi";
import { SettingsApi, type WorkerSettingsDto } from "@/api/SettingsApi";
import { formatLocal } from "@/lib/time";

const discards = ref<DiscardDto[]>([]);
const stats = ref<DiscardStatsDto | null>(null);
const settings = ref<WorkerSettingsDto | null>(null);
const busy = ref(false);

/** Plain-language read of the measurements that drove the rejection. */
function why(d: DiscardDto): string {
  if (d.sustainedTone) return "steady tone — carrier or heterodyne, not speech";
  if (d.reason.includes("ms of signal")) return "too brief — a click or key-up, no sustained signal";
  if (d.speechBandRatio < 0.3) return "energy outside the speech band — data, noise or a spur";
  if (d.syllableRateHz > 12) return "too fast to be syllables — likely data (APRS/packet)";
  if (d.voicedMs < 800) return "not enough voiced audio to call it speech";
  return "failed the voice test";
}

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
          Recordings the capture gate rejected — kept
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
        <v-chip v-for="r in stats.byReason" :key="r.reason" class="mr-2" size="small" variant="outlined">
          {{ r.reason }}: {{ r.count }}
        </v-chip>
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
            <td>{{ why(d) }}</td>
            <td class="text-caption text-medium-emphasis" style="white-space: nowrap">
              voiced {{ d.voicedMs }}ms · speech {{ (d.speechBandRatio * 100).toFixed(0) }}% ·
              syllables {{ d.syllableRateHz.toFixed(1) }}Hz · peak {{ d.peakDbfs.toFixed(0) }}dB
            </td>
          </tr>
          <tr v-if="!discards.length">
            <td colspan="6" class="text-medium-emphasis">Nothing discarded recently.</td>
          </tr>
        </tbody>
      </v-table>
    </v-card>
  </v-container>
</template>
