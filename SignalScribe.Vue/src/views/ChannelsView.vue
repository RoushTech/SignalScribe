<script setup lang="ts">
import { onMounted, ref } from "vue";
import { useRouter } from "vue-router";
import { ChannelsApi, ChannelType, type ChannelDto, type ChannelUpsertRequest } from "@/api/ChannelsApi";
import { useChannelsStore } from "@/stores/channels";
import { formatFrequency, formatLocal } from "@/lib/time";
import ChannelEditDialog from "@/components/ChannelEditDialog.vue";

const router = useRouter();
const store = useChannelsStore();
const dialogOpen = ref(false);
const editing = ref<ChannelDto | null>(null);
const error = ref<string | null>(null);

function typeName(type: ChannelType): string {
  return { [ChannelType.Unknown]: "Unknown", [ChannelType.Simplex]: "Simplex", [ChannelType.RepeaterOutput]: "Repeater" }[type];
}

function openCreate() {
  editing.value = null;
  dialogOpen.value = true;
}

function openEdit(channel: ChannelDto) {
  editing.value = channel;
  dialogOpen.value = true;
}

async function save(request: ChannelUpsertRequest) {
  error.value = null;
  try {
    if (editing.value) {
      await ChannelsApi.update(editing.value.id, request);
    } else {
      await ChannelsApi.create(request);
    }
    dialogOpen.value = false;
    await store.refresh();
  } catch (e: unknown) {
    error.value = `Save failed: ${e instanceof Error ? e.message : e}`;
  }
}

onMounted(() => store.refresh());
</script>

<template>
  <v-container>
    <v-alert v-if="error" type="error" closable class="mb-4" @click:close="error = null">{{ error }}</v-alert>

    <v-card title="Channels">
      <template #append>
        <v-btn color="primary" prepend-icon="mdi-plus" @click="openCreate">Add channel</v-btn>
      </template>
      <v-table hover>
        <thead>
          <tr>
            <th>Frequency</th>
            <th>Label</th>
            <th>Type</th>
            <th>Callsign</th>
            <th>Tone</th>
            <th>Recordings</th>
            <th>Last heard</th>
            <th />
          </tr>
        </thead>
        <tbody>
          <tr
            v-for="c in store.channels"
            :key="c.id"
            style="cursor: pointer"
            @click="router.push(`/channels/${c.id}`)"
          >
            <td>{{ formatFrequency(c.frequencyHz) }}</td>
            <td>
              {{ c.label }}
              <v-tooltip v-if="c.autoDisabledReason" :text="c.autoDisabledReason">
                <template #activator="{ props }">
                  <v-chip v-bind="props" size="x-small" color="warning" class="ml-1">
                    <v-icon start icon="mdi-radio-off" />
                    no voice
                  </v-chip>
                </template>
              </v-tooltip>
              <v-chip v-else-if="!c.enabled" size="x-small" class="ml-1">disabled</v-chip>
            </td>
            <td>{{ typeName(c.type) }}</td>
            <td>{{ c.callsign ?? "—" }}</td>
            <td>{{ c.ctcssToneHz ? `${c.ctcssToneHz} Hz` : "—" }}</td>
            <td>{{ c.transmissionCount }}</td>
            <td>{{ formatLocal(c.lastHeardUtc) }}</td>
            <td>
              <v-btn icon="mdi-pencil" size="small" variant="text" @click.stop="openEdit(c)" />
            </td>
          </tr>
        </tbody>
      </v-table>
    </v-card>

    <ChannelEditDialog v-model="dialogOpen" :channel="editing" @save="save" />
  </v-container>
</template>
