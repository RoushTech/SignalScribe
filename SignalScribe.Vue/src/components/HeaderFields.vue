<script setup lang="ts">
import { computed, ref } from "vue";
import type { SegmentDto } from "@/api/TransmissionsApi";

// Everything a digital header gave up, not just the summary line. D-STAR hands over callsigns in
// plain text with no vocoder anywhere near it, and the routing fields (which repeater in, which
// out, group or private) are the part that says how the contact was actually made — so the whole
// field set is shown, never a chosen few. Collapsed by default so a list of transmissions stays a
// list; the summary already sits in the row above.
const props = defineProps<{ segments: SegmentDto[] }>();

const headers = computed(() => props.segments.filter((s) => s.headerFields && s.headerFields.length > 0));
const opened = ref<number[]>([]);

function isOpen(segmentId: number): boolean {
  return opened.value.includes(segmentId);
}

function toggle(segmentId: number) {
  opened.value = isOpen(segmentId) ? opened.value.filter((id) => id !== segmentId) : [...opened.value, segmentId];
}
</script>

<template>
  <div v-if="headers.length" class="header-fields-block">
    <div v-for="(s, i) in headers" :key="s.id">
      <v-btn
        size="x-small"
        variant="text"
        class="px-1"
        :prepend-icon="isOpen(s.id) ? 'mdi-chevron-up' : 'mdi-chevron-down'"
        @click="toggle(s.id)"
      >
        {{ headers.length > 1 ? `Header ${i + 1}` : "Header" }}
      </v-btn>
      <v-expand-transition>
        <!-- Name/value pairs as a grid rather than a v-table: no horizontal scroll to get trapped
             in on a phone, and the columns collapse to the width of the longest field name. -->
        <dl v-show="isOpen(s.id)" class="header-fields text-caption">
          <template v-for="f in s.headerFields!" :key="f.name">
            <dt class="text-medium-emphasis">{{ f.name }}</dt>
            <dd>{{ f.value }}</dd>
          </template>
        </dl>
      </v-expand-transition>
    </div>
  </div>
</template>

<style scoped>
.header-fields-block {
  margin-top: 2px;
}

.header-fields {
  display: grid;
  grid-template-columns: max-content minmax(0, 1fr);
  column-gap: 12px;
  row-gap: 2px;
  margin: 2px 0 6px 4px;
  max-width: 420px;
}

.header-fields dd {
  /* Callsign fields are space-padded and the padding is part of the field, so keep it verbatim. */
  font-family: monospace;
  white-space: pre-wrap;
  overflow-wrap: anywhere;
}
</style>
