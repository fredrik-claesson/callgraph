#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"

find "$ROOT_DIR/_claude" "$ROOT_DIR/_codex" "$ROOT_DIR/_cursor" \
  -name '.DS_Store' \
  -type f \
  -delete 2>/dev/null || true

echo "Removed .DS_Store files from distributable folders."
