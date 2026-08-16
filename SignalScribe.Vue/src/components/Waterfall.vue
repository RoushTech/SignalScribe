<script setup lang="ts">
import { computed, onMounted, ref } from "vue";
import { decodeBins, type ActiveGate, type SpectrumRow } from "@/api/statusHub";
import { formatFrequency } from "@/lib/time";

const props = defineProps<{ gates?: ActiveGate[] }>();

const canvas = ref<HTMLCanvasElement | null>(null);
const lastRow = ref<SpectrumRow | null>(null);
const HEIGHT = 220;

/** Recording markers positioned across the span, so you can see *where* on the band capture is active. */
const markers = computed(() => {
  const row = lastRow.value;
  if (!row) return [];
  const low = row.centerFrequencyHz - row.spanHz / 2;
  return (props.gates ?? [])
    .map((g) => ({ gate: g, percent: ((g.frequencyHz - low) / row.spanHz) * 100 }))
    .filter((m) => m.percent >= 0 && m.percent <= 100);
});

// Cold → hot color LUT (black → blue → cyan → yellow → white), built once.
const LUT = new Uint8ClampedArray(256 * 3);
for (let v = 0; v < 256; v++) {
  const t = v / 255;
  let r = 0, g = 0, b = 0;
  if (t < 0.33) {
    b = Math.round(255 * (t / 0.33));
  } else if (t < 0.66) {
    const u = (t - 0.33) / 0.33;
    g = Math.round(255 * u);
    b = 255;
  } else {
    const u = (t - 0.66) / 0.34;
    r = Math.round(255 * u);
    g = 255;
    b = Math.round(255 * (1 - u));
  }
  LUT[v * 3] = r;
  LUT[v * 3 + 1] = g;
  LUT[v * 3 + 2] = b;
}

let rowImage: ImageData | null = null;

function pushRow(row: SpectrumRow) {
  lastRow.value = row;
  const el = canvas.value;
  if (!el) return;
  const bins = decodeBins(row);
  if (el.width !== bins.length) {
    el.width = bins.length;
    el.height = HEIGHT;
    rowImage = null;
  }
  const ctx = el.getContext("2d");
  if (!ctx) return;
  if (!rowImage) rowImage = ctx.createImageData(bins.length, 1);
  for (let i = 0; i < bins.length; i++) {
    const v = bins[i];
    rowImage.data[i * 4] = LUT[v * 3];
    rowImage.data[i * 4 + 1] = LUT[v * 3 + 1];
    rowImage.data[i * 4 + 2] = LUT[v * 3 + 2];
    rowImage.data[i * 4 + 3] = 255;
  }
  // Scroll down one row, paint the new row on top.
  ctx.drawImage(el, 0, 0, el.width, HEIGHT - 1, 0, 1, el.width, HEIGHT - 1);
  ctx.putImageData(rowImage, 0, 0);
}

defineExpose({ pushRow });

onMounted(() => {
  const el = canvas.value;
  if (el) {
    el.width = 1024;
    el.height = HEIGHT;
    const ctx = el.getContext("2d");
    if (ctx) {
      ctx.fillStyle = "#000";
      ctx.fillRect(0, 0, el.width, HEIGHT);
    }
  }
});
</script>

<template>
  <div>
    <div style="position: relative">
      <canvas ref="canvas" style="width: 100%; height: 220px; display: block; image-rendering: pixelated" />
      <!-- Live recording markers over the spectrum -->
      <div
        v-for="m in markers"
        :key="m.gate.frequencyHz"
        :style="{
          position: 'absolute',
          left: `${m.percent}%`,
          top: '0',
          height: '100%',
          width: '2px',
          background: m.gate.known ? 'rgba(255,82,82,0.95)' : 'rgba(255,255,255,0.95)',
          pointerEvents: 'none',
        }"
      >
        <div
          class="text-caption"
          :style="{
            position: 'absolute',
            top: '2px',
            left: '4px',
            whiteSpace: 'nowrap',
            color: '#fff',
            textShadow: '0 0 4px #000, 0 0 2px #000',
            fontWeight: 600,
          }"
        >
          {{ m.gate.known ? '●' : '○' }} {{ (m.gate.frequencyHz / 1e6).toFixed(4) }} · {{ m.gate.seconds.toFixed(0) }}s
        </div>
      </div>
    </div>
    <div v-if="lastRow" class="d-flex justify-space-between text-caption text-medium-emphasis mt-1">
      <span>{{ formatFrequency(lastRow.centerFrequencyHz - lastRow.spanHz / 2) }}</span>
      <span>{{ formatFrequency(lastRow.centerFrequencyHz) }}</span>
      <span>{{ formatFrequency(lastRow.centerFrequencyHz + lastRow.spanHz / 2) }}</span>
    </div>
    <div v-else class="text-caption text-medium-emphasis mt-1">Waiting for spectrum from the capture daemon…</div>
  </div>
</template>
