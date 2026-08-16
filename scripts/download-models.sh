#!/usr/bin/env bash
# Downloads inference models into the models directory (or the docker `models` volume).
# Run once while online — everything after this is fully offline.
set -euo pipefail

MODELS_DIR="${1:-models}"
mkdir -p "$MODELS_DIR"

fetch() {
    local url="$1" out="$2"
    if [[ -f "$MODELS_DIR/$out" ]]; then
        echo "✓ $out (already present)"
    else
        echo "→ $out"
        curl -fL --progress-bar -o "$MODELS_DIR/$out.part" "$url"
        mv "$MODELS_DIR/$out.part" "$MODELS_DIR/$out"
    fi
}

# Whisper (transcription) — small.en default; medium.en optional for higher accuracy.
fetch "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.en-q5_1.bin" "ggml-small.en-q5_1.bin"
fetch "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-medium.en-q5_0.bin" "ggml-medium.en-q5_0.bin"
# large-v3-turbo: near large-v3 accuracy at a fraction of the CPU cost — the sweet spot here.
fetch "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-large-v3-turbo-q5_0.bin" "ggml-large-v3-turbo-q5_0.bin"

# Qwen 2.5 7B Instruct Q4_K_M (net summaries) — ~4.7 GB. Swap for the 3B on low-power boxes.
fetch "https://huggingface.co/bartowski/Qwen2.5-7B-Instruct-GGUF/resolve/main/Qwen2.5-7B-Instruct-Q4_K_M.gguf" "Qwen2.5-7B-Instruct-Q4_K_M.gguf"
# fetch "https://huggingface.co/bartowski/Qwen2.5-3B-Instruct-GGUF/resolve/main/Qwen2.5-3B-Instruct-Q4_K_M.gguf" "Qwen2.5-3B-Instruct-Q4_K_M.gguf"

# TODO(milestone 4): ECAPA-TDNN speaker-embedding model. The SpeechBrain release needs a one-time
# ONNX export (torch.onnx.export of speechbrain/spkrec-ecapa-voxceleb) — script that here so the
# result lands at "$MODELS_DIR/ecapa-tdnn.onnx" for ONNX Runtime.

echo "Done. Models in $MODELS_DIR:"
ls -lh "$MODELS_DIR"
