<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from "vue";
import type { HubConnection } from "@microsoft/signalr";
import { connectStatusHub, type ActiveGate, type ServiceStatusUpdate } from "@/api/statusHub";
import Waterfall from "@/components/Waterfall.vue";
import { TransmissionsApi, type TransmissionDto } from "@/api/TransmissionsApi";
import { SearchApi, type SearchHitDto } from "@/api/SearchApi";
import { JOB_TYPE_NAMES, ProcessingApi, type FailedJobDto, type ProcessingStatsDto } from "@/api/ProcessingApi";
import { formatLocal, formatFrequency } from "@/lib/time";
import { useRouter } from "vue-router";

const statuses = ref<Map<string, ServiceStatusUpdate>>(new Map());
const transmissions = ref<TransmissionDto[]>([]);
const searchQuery = ref("");
const searchHits = ref<SearchHitDto[]>([]);
const stats = ref<ProcessingStatsDto | null>(null);
const failedJobs = ref<FailedJobDto[]>([]);
const showFailed = ref(false);
const waterfall = ref<InstanceType<typeof Waterfall> | null>(null);
const gates = ref<ActiveGate[]>([]);
const router = useRouter();

/** Merge a live hub push: replace the row in place (transcript arriving) or prepend a new one. */
function mergeTransmission(incoming: TransmissionDto) {
  const at = transmissions.value.findIndex((t) => t.id === incoming.id);
  if (at >= 0) {
    transmissions.value[at] = incoming;
    transmissions.value = [...transmissions.value];
  } else {
    transmissions.value = [incoming, ...transmissions.value].slice(0, 40);
  }
}

function statusColor(status: string): string {
  switch (status) {
    case "transcribed": return "success";
    case "processing": return "info";
    case "double": return "warning";
    default: return "grey";
  }
}

async function reprocess(id: number) {
  await TransmissionsApi.reprocess(id);
  await refreshStats();
}

// What the workers are chewing on right now (streamed on the same socket).
const analyzing = computed(() => {
  const job = statuses.value.get("workers")?.details?.currentJob;
  return job && job !== "none" ? job : null;
});
let hub: HubConnection | null = null;
let statsTimer: ReturnType<typeof setInterval> | null = null;

async function refreshStats() {
  stats.value = await ProcessingApi.getStats();
  if (stats.value.failed > 0) {
    failedJobs.value = await ProcessingApi.getFailed();
  } else {
    failedJobs.value = [];
    showFailed.value = false;
  }
}

async function retryJob(job: FailedJobDto) {
  await ProcessingApi.retry(job.id);
  await refreshStats();
}

const EXPECTED_SERVICES = ["capture", "workers"];

function stateColor(state: string | undefined): string {
  switch (state) {
    case "running": return "success";
    case "idle": return "info";
    case "degraded": return "warning";
    default: return "error";
  }
}

async function runSearch() {
  searchHits.value = searchQuery.value ? await SearchApi.search(searchQuery.value) : [];
}

onMounted(async () => {
  transmissions.value = await TransmissionsApi.getRecent(25);
  await refreshStats();
  statsTimer = setInterval(refreshStats, 10_000);
  hub = await connectStatusHub(
    (update) => {
      statuses.value.set(update.service, update);
      statuses.value = new Map(statuses.value); // trigger reactivity
      if (update.service === "capture") {
        gates.value = update.gates ?? [];
      }
    },
    (row) => waterfall.value?.pushRow(row),
    (t) => mergeTransmission(t as TransmissionDto),
  );
});

onUnmounted(() => {
  hub?.stop();
  if (statsTimer) clearInterval(statsTimer);
});
</script>

<template>
  <v-container>
    <v-row>
      <v-col v-for="service in EXPECTED_SERVICES" :key="service" cols="12" md="6">
        <v-card>
          <v-card-item>
            <template #prepend>
              <v-icon
                :color="stateColor(statuses.get(service)?.state)"
                :icon="service === 'capture' ? 'mdi-radio-tower' : 'mdi-cog'"
              />
            </template>
            <v-card-title class="text-capitalize">{{ service }}</v-card-title>
            <v-card-subtitle>
              {{ statuses.get(service)?.state ?? "not connected" }}
              <span v-if="statuses.get(service)"> — {{ formatLocal(statuses.get(service)!.timestampUtc) }}</span>
            </v-card-subtitle>
          </v-card-item>
          <v-card-text v-if="statuses.get(service)?.details">
            <v-chip
              v-for="(value, key) in statuses.get(service)!.details"
              :key="key"
              size="small"
              class="mr-1 mb-1"
              variant="outlined"
            >
              {{ key }}: {{ value }}
            </v-chip>
          </v-card-text>
        </v-card>
      </v-col>
    </v-row>

    <v-card class="mt-4" title="Band waterfall">
      <v-card-text>
        <Waterfall ref="waterfall" :gates="gates" />

        <!-- Live activity. Icon carries the meaning (colour alone is unreliable under f.lux /
             night-shift, where amber, red and yellow converge). -->
        <div class="d-flex flex-wrap align-center mt-3" style="gap: 6px">
          <template v-if="gates.length">
            <v-chip
              v-for="g in gates"
              :key="g.frequencyHz"
              size="small"
              variant="outlined"
              :prepend-icon="g.known ? 'mdi-record-circle' : 'mdi-record-circle-outline'"
              :class="g.known ? 'text-red-lighten-1' : 'text-grey-lighten-1'"
            >
              {{ (g.frequencyHz / 1e6).toFixed(4) }} · {{ g.seconds.toFixed(0) }}s · {{ g.peakDbfs.toFixed(0) }} dB
            </v-chip>
          </template>
          <v-chip v-else size="small" variant="outlined" prepend-icon="mdi-ear-hearing" class="text-grey">
            idle
          </v-chip>

          <v-chip v-if="analyzing" size="small" variant="outlined" prepend-icon="mdi-cog" class="text-blue-lighten-2">
            {{ analyzing }}
          </v-chip>
          <v-chip v-if="stats && stats.pending > 0" size="small" variant="outlined" prepend-icon="mdi-tray-full" class="text-grey-lighten-1">
            {{ stats.pending }}
          </v-chip>

          <span class="text-caption text-medium-emphasis ml-2">
            <v-icon size="x-small" icon="mdi-record-circle" /> known ·
            <v-icon size="x-small" icon="mdi-record-circle-outline" /> new ·
            <v-icon size="x-small" icon="mdi-cog" /> analyzing ·
            <v-icon size="x-small" icon="mdi-tray-full" /> queued
          </span>
        </div>
      </v-card-text>
    </v-card>

    <v-card v-if="stats" class="mt-4" title="Processing queue">
      <v-card-text>
        <v-chip class="mr-2" color="info" variant="tonal">pending: {{ stats.pending }}</v-chip>
        <v-chip class="mr-2" color="primary" variant="tonal">running: {{ stats.leased }}</v-chip>
        <v-chip class="mr-2" color="success" variant="tonal">completed: {{ stats.completed }}</v-chip>
        <v-chip
          class="mr-2"
          :color="stats.failed ? 'error' : 'success'"
          variant="tonal"
          :append-icon="stats.failed ? (showFailed ? 'mdi-chevron-up' : 'mdi-chevron-down') : undefined"
          @click="stats.failed && (showFailed = !showFailed)"
        >
          failed: {{ stats.failed }}
        </v-chip>
        <v-chip v-for="tc in stats.pendingByType" :key="tc.type" class="mr-2" size="small" variant="outlined">
          {{ JOB_TYPE_NAMES[tc.type] ?? tc.type }}: {{ tc.count }}
        </v-chip>
        <div v-if="stats.oldestPendingUtc" class="text-caption text-medium-emphasis mt-2">
          Oldest pending job: {{ formatLocal(stats.oldestPendingUtc) }}
        </div>

        <v-expand-transition>
          <v-list v-if="showFailed && failedJobs.length" density="compact" lines="two">
            <v-list-item
              v-for="job in failedJobs"
              :key="job.id"
              :title="`${JOB_TYPE_NAMES[job.type] ?? job.type} #${job.id} — ${job.attempts} attempts`"
              :subtitle="job.error ?? 'no error recorded'"
            >
              <template #append>
                <v-btn size="small" variant="tonal" prepend-icon="mdi-restart" @click="retryJob(job)">Retry</v-btn>
              </template>
            </v-list-item>
          </v-list>
        </v-expand-transition>
      </v-card-text>
    </v-card>

    <v-text-field
      v-model="searchQuery"
      class="mt-4"
      label="Search transcripts"
      prepend-inner-icon="mdi-magnify"
      clearable
      @keyup.enter="runSearch"
    />

    <v-list v-if="searchHits.length" lines="two">
      <v-list-item
        v-for="hit in searchHits"
        :key="hit.segmentId"
        :title="`${formatFrequency(hit.frequencyHz)} — ${formatLocal(hit.startUtc)}`"
      >
        <!-- eslint-disable-next-line vue/no-v-html — snippet markup comes from our own FTS5 snippet() -->
        <v-list-item-subtitle v-html="hit.snippet" />
      </v-list-item>
    </v-list>

    <v-card class="mt-4" title="Recent transmissions">
      <v-table>
        <thead>
          <tr>
            <th>Channel</th>
            <th>Start</th>
            <th>Audio</th>
            <th>State</th>
            <th>Transcript</th>
            <th />
          </tr>
        </thead>
        <tbody>
          <!-- In-flight recordings appear here before they are posted -->
          <tr v-for="g in gates" :key="`gate-${g.frequencyHz}`">
            <td>{{ (g.frequencyHz / 1e6).toFixed(4) }} MHz</td>
            <td style="white-space: nowrap">now</td>
            <td>—</td>
            <td>
              <v-chip size="x-small" variant="outlined" prepend-icon="mdi-record-circle" class="text-red-lighten-1">
                recording {{ g.seconds.toFixed(0) }}s
              </v-chip>
            </td>
            <td>—</td>
            <td />
          </tr>
          <tr v-for="t in transmissions" :key="t.id">
            <td>
              <a style="cursor: pointer; text-decoration: underline" @click="router.push(`/channels/${t.channelId}`)">
                {{ t.channelLabel }}
              </a>
            </td>
            <td style="white-space: nowrap">{{ formatLocal(t.startUtc) }}</td>
            <td>
              <audio :src="TransmissionsApi.audioUrl(t.id)" controls preload="none" style="height: 30px; width: 220px" />
            </td>
            <td>
              <v-chip size="x-small" variant="outlined" :class="`text-${statusColor(t.status)}`">{{ t.status }}</v-chip>
            </td>
            <td>
              <template v-if="!t.isDouble">
                <v-chip
                  v-for="cs in [...new Set(t.segments.map((s) => s.callsign).filter(Boolean))]"
                  :key="cs!"
                  color="primary"
                  size="x-small"
                  variant="tonal"
                  class="mr-1"
                >
                  {{ cs }}
                </v-chip>
                <span>{{ t.segments.map((s) => s.transcript).filter(Boolean).join(" ") || "—" }}</span>
              </template>
            </td>
            <td>
              <v-btn
                icon="mdi-refresh"
                size="x-small"
                variant="text"
                title="Re-transcribe this clip"
                @click="reprocess(t.id)"
              />
            </td>
          </tr>
        </tbody>
      </v-table>
    </v-card>
  </v-container>
</template>
