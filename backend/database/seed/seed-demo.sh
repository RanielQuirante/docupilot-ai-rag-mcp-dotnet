#!/usr/bin/env bash
# =============================================================================
# seed-demo.sh — seed the running DocuPilot AI stack with the 4 demo documents
# =============================================================================
# Uploads the four sample .txt files to the running stack in a single multipart
# POST /api/documents/upload (form field "files"), exactly as the Document
# Upload page does. The background Worker then classifies, extracts metadata,
# chunks, and embeds each document automatically.
#
# This is a MANUAL, on-demand seeder -- NOT an automatic startup seeder, so a
# fresh demo stays predictable (show the empty -> populated transition live,
# no duplicate rows on each `docker compose up`). Re-running uploads again.
#
# Usage:
#   ./seed-demo.sh                         # via web/nginx origin :4210 (default)
#   ./seed-demo.sh http://localhost:5010   # straight to the API (bypass nginx)
#
# Requires: bash + curl. (On Windows, prefer seed-demo.ps1.)
# =============================================================================
set -euo pipefail

BASE_URL="${1:-http://localhost:4210}"
SEED_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
UPLOAD_URL="${BASE_URL}/api/documents/upload"

FILES=(
  "sample-contract.txt"
  "sample-invoice.txt"
  "sample-employee-record.txt"
  "sample-compliance-policy.txt"
)

# Verify each file exists and build the repeated -F files=@... args.
CURL_ARGS=()
for f in "${FILES[@]}"; do
  path="${SEED_DIR}/${f}"
  if [[ ! -f "$path" ]]; then
    echo "Seed file not found: $path" >&2
    exit 1
  fi
  # field name MUST be "files" (frozen DA-016 contract: [FromForm(Name="files")])
  CURL_ARGS+=(-F "files=@${path};type=text/plain")
done

echo "Uploading ${#FILES[@]} seed documents to ${UPLOAD_URL} ..."

# -f: fail (non-zero) on HTTP >= 400; -S: show errors; -s: quiet progress.
if ! curl -fsS -X POST "${CURL_ARGS[@]}" "${UPLOAD_URL}"; then
  echo "" >&2
  echo "Upload failed against ${UPLOAD_URL}. Is the stack up?" >&2
  echo "Tip: 'docker compose ps' and confirm http://localhost:4210 loads." >&2
  exit 1
fi

echo ""
echo "Upload accepted. The Worker will now classify -> extract metadata -> chunk -> embed each doc."
echo "Watch them progress to ReadyForSearch in the Library (/library) or Dashboard (/dashboard)."
echo "Note: LLM inference is CPU-only, so processing takes seconds to tens of seconds per doc."
