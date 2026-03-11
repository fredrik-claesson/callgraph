#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
MODEL_DIR="$ROOT_DIR/models/bge-small-en-v1.5"
ONNX_DIR="$MODEL_DIR/onnx"

mkdir -p "$ONNX_DIR"

# ONNX weights from Xenova conversion of bge-small-en-v1.5.
curl -fL "https://huggingface.co/Xenova/bge-small-en-v1.5/resolve/main/onnx/model_quantized.onnx" \
  -o "$ONNX_DIR/model_quantized.onnx"

# Original BAAI vocabulary for WordPiece tokenization.
curl -fL "https://huggingface.co/BAAI/bge-small-en-v1.5/resolve/main/vocab.txt" \
  -o "$MODEL_DIR/vocab.txt"

echo "Downloaded bge-small-en-v1.5 model assets to: $MODEL_DIR"
