<script setup lang="ts">
import type { ModeChipSpec } from "@/lib/detectedMode";

// One tag for "what modulation is this" — the same chip on transmissions, channels and discards, so
// FM / APRS / D-STAR / DMR read identically wherever they turn up. Build the spec with
// transmissionModeChip() or channelModeChip(); null means there is nothing worth saying.
defineProps<{ chip: ModeChipSpec | null }>();
</script>

<template>
  <v-tooltip v-if="chip" :text="chip.detail" location="bottom" max-width="360">
    <template #activator="{ props }">
      <v-chip v-bind="props" size="x-small" :variant="chip.variant" style="cursor: help" :color="chip.color">
        <v-icon start size="x-small" :icon="chip.icon" />
        {{ chip.label }}
      </v-chip>
    </template>
  </v-tooltip>
</template>
