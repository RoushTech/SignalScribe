<script setup lang="ts">
import { computed, onMounted, ref } from "vue";
import { channelTone } from "@/lib/squelchTone";
import { ChannelsApi, type ChannelDto, type ChannelUpsertRequest } from "@/api/ChannelsApi";
import { NetsApi, NetScheduleSource, type NetDto, type NetUpsertRequest } from "@/api/NetsApi";
import { SessionsApi, type SessionDto } from "@/api/SessionsApi";
import { TransmissionsApi, type TransmissionDto } from "@/api/TransmissionsApi";
import { useChannelsStore } from "@/stores/channels";
import { DAY_NAMES, formatFrequency, formatLocal, utcWeeklyToLocal } from "@/lib/time";
import ChannelEditDialog from "@/components/ChannelEditDialog.vue";
import NetEditDialog from "@/components/NetEditDialog.vue";

const props = defineProps<{ id: string }>();
const channelId = computed(() => Number.parseInt(props.id, 10));

const store = useChannelsStore();
const channel = ref<ChannelDto | null>(null);
const transmissions = ref<TransmissionDto[]>([]);
const nets = ref<NetDto[]>([]);
const sessions = ref<SessionDto[]>([]);
const tab = ref("recordings");
const channelDialog = ref(false);
const netDialog = ref(false);
const editingNet = ref<NetDto | null>(null);
const loadingMore = ref(false);

function scheduleText(net: NetDto): string {
  if (net.dayOfWeekUtc == null || !net.startTimeUtc) return "no schedule";
  const local = utcWeeklyToLocal(net.dayOfWeekUtc, net.startTimeUtc);
  const duration = net.durationMinutes ? ` for ${net.durationMinutes} min` : "";
  return `${DAY_NAMES[local.day]}s at ${local.time}${duration}`;
}

async function refresh() {
  channel.value = await ChannelsApi.get(channelId.value);
  transmissions.value = await TransmissionsApi.getRecent(50, channelId.value);
  nets.value = await NetsApi.getForChannel(channelId.value);
  sessions.value = await SessionsApi.getForChannel(channelId.value);
}

async function loadMore() {
  const last = transmissions.value.at(-1);
  if (!last) return;
  loadingMore.value = true;
  try {
    transmissions.value.push(...(await TransmissionsApi.getRecent(50, channelId.value, last.startUtc)));
  } finally {
    loadingMore.value = false;
  }
}

async function saveChannel(request: ChannelUpsertRequest) {
  if (!channel.value) return;
  channel.value = await ChannelsApi.update(channel.value.id, request);
  channelDialog.value = false;
  await store.refresh();
}

function openNetDialog(net: NetDto | null) {
  editingNet.value = net;
  netDialog.value = true;
}

async function saveNet(request: NetUpsertRequest) {
  if (editingNet.value) {
    await NetsApi.update(editingNet.value.id, request);
  } else {
    await NetsApi.create(request);
  }
  netDialog.value = false;
  nets.value = await NetsApi.getForChannel(channelId.value);
}

async function deleteNet(net: NetDto) {
  await NetsApi.remove(net.id);
  nets.value = await NetsApi.getForChannel(channelId.value);
}

onMounted(refresh);
</script>

<template>
  <v-container v-if="channel">
    <v-card class="mb-4">
      <v-card-item>
        <v-card-title>
          {{ formatFrequency(channel.frequencyHz) }} — {{ channel.label }}
          <v-chip v-if="channel.callsign" class="ml-2" size="small">{{ channel.callsign }}</v-chip>
        </v-card-title>
        <v-card-subtitle>
          {{ channel.description ?? "No description" }}
          <span v-if="channelTone(channel)">
            ·
            <v-tooltip :text="channelTone(channel)!.detail" location="bottom" max-width="340">
              <template #activator="{ props }">
                <span v-bind="props" style="cursor: help; text-decoration: underline dotted">
                  {{ channelTone(channel)!.measured ? "heard" : "tone" }} {{ channelTone(channel)!.label }}
                </span>
              </template>
            </v-tooltip>
          </span>
          <span v-if="channel.measuredCtcssToneHz"> (measured {{ channel.measuredCtcssToneHz }} Hz)</span>
        </v-card-subtitle>
        <template #append>
          <v-btn prepend-icon="mdi-pencil" variant="tonal" @click="channelDialog = true">Edit</v-btn>
        </template>
      </v-card-item>
    </v-card>

    <v-tabs v-model="tab">
      <v-tab value="recordings">Recordings ({{ channel.transmissionCount }})</v-tab>
      <v-tab value="sessions">Sessions ({{ sessions.length }})</v-tab>
      <v-tab value="nets">Nets ({{ nets.length }})</v-tab>
    </v-tabs>

    <v-window v-model="tab">
      <v-window-item value="recordings">
        <v-list lines="two">
          <v-list-item v-for="t in transmissions" :key="t.id">
            <v-list-item-title>
              {{ formatLocal(t.startUtc) }}
              <v-chip v-if="t.isDouble" color="warning" size="x-small" class="ml-1">double</v-chip>
              <v-chip
                v-for="cs in [...new Set(t.segments.map((s) => s.callsign).filter(Boolean))]"
                :key="cs!"
                color="primary"
                size="x-small"
                variant="tonal"
                class="ml-1"
              >
                {{ cs }}
              </v-chip>
            </v-list-item-title>
            <v-list-item-subtitle>
              {{ t.segments.map((s) => s.transcript).filter(Boolean).join(" ") || "not transcribed yet" }}
            </v-list-item-subtitle>
            <template #append>
              <audio :src="TransmissionsApi.audioUrl(t.id)" controls preload="none" style="height: 32px" />
            </template>
          </v-list-item>
        </v-list>
        <v-btn v-if="transmissions.length >= 50" block variant="tonal" :loading="loadingMore" @click="loadMore">
          Load more
        </v-btn>
      </v-window-item>

      <v-window-item value="sessions">
        <v-list lines="three">
          <v-list-item v-for="s in sessions" :key="s.id">
            <v-list-item-title>
              {{ formatLocal(s.startUtc) }} — {{ s.transmissionCount }} transmissions
              <v-chip v-if="s.isNet" color="primary" size="x-small" class="ml-1">{{ s.netName ?? "net" }}</v-chip>
            </v-list-item-title>
            <v-list-item-subtitle style="-webkit-line-clamp: 4">
              {{ s.summary ?? "No summary yet" }}
            </v-list-item-subtitle>
          </v-list-item>
        </v-list>
      </v-window-item>

      <v-window-item value="nets">
        <v-card flat>
          <v-card-text>
            <v-btn color="primary" prepend-icon="mdi-plus" class="mb-4" @click="openNetDialog(null)">
              Declare net window
            </v-btn>
            <v-list lines="two">
              <v-list-item v-for="net in nets" :key="net.id" :title="net.name ?? 'Unnamed net'">
                <v-list-item-subtitle>
                  {{ scheduleText(net) }} · {{ net.sessionCount }} occurrences
                  <v-chip v-if="net.source === NetScheduleSource.Mined" size="x-small" class="ml-1">mined</v-chip>
                </v-list-item-subtitle>
                <template #append>
                  <v-btn icon="mdi-pencil" size="small" variant="text" @click="openNetDialog(net)" />
                  <v-btn icon="mdi-delete" size="small" variant="text" @click="deleteNet(net)" />
                </template>
              </v-list-item>
            </v-list>
          </v-card-text>
        </v-card>
      </v-window-item>
    </v-window>

    <ChannelEditDialog v-model="channelDialog" :channel="channel" @save="saveChannel" />
    <NetEditDialog v-model="netDialog" :channel-id="channelId" :net="editingNet" @save="saveNet" />
  </v-container>
</template>
