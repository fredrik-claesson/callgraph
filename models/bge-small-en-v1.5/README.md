# bge-small-en-v1.5 local bundle

Place local model assets in this folder so `search-method` can run hybrid lexical + semantic reranking without external hosting.

Expected files (at least):

- `model.onnx` or `model_quantized.onnx` (or under `onnx/`)
- `vocab.txt`

You can fetch a compatible bundle with:

```bash
./scripts/bootstrap-bge-small-en-v1.5.sh
```

If files are missing, CallGraph automatically falls back to lexical-only ranking.
