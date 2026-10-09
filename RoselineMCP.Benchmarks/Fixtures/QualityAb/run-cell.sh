#!/usr/bin/env bash
# One session of the quality A/B (docs/AGENT-BENCHMARK.md). Never re-run a cell to improve a number.
# Usage: run-cell.sh <control|treatment> <n> <server-out-dir> <work-dir> [max-turns] [max-usd]
#   <server-out-dir>  `dotnet build RoselineMCP/RoselineMCP.csproj -c Release -o <dir>` of the build under test
#   <work-dir>        scratch dir OUTSIDE any real repository
# Produces <work-dir>/<cell>-<n>/{fixture,snaps,run.jsonl,run.err,final-build.log,meta.txt}
set -uo pipefail
cell="${1:?control|treatment}"; n="${2:?n}"; srv="${3:?server out dir}"; work="${4:?work dir}"
max_turns="${5:-40}"; max_usd="${6:-3}"
here="$(cd "$(dirname "$0")" && pwd)"
run="$work/$cell-$n"
[ -e "$run" ] && { echo "refusing: $run exists (no re-runs)" >&2; exit 1; }
mkdir -p "$run/snaps"
"$here/make-fixture.sh" "$run/fixture" >/dev/null

# Frozen prompt: byte-identical across cells (pre-registered).
prompt="$(cat "${PROMPT_FILE:-$here/prompt.txt}")"

sock="/tmp/rg166-$cell-$n.sock"
env_json='"ROSELINE_RoselineMCP__ConfirmDestructiveWrites":"false"'
[ "$cell" = treatment ] && env_json="$env_json,\"ROSELINE_RoselineMCP__Guard\":\"true\",\"ROSELINE_RoselineMCP__GuardEndpoint\":\"$sock\""
cat > "$run/mcp.json" <<J
{"mcpServers":{"roseline":{"command":"dotnet","args":["$srv/RoselineMCP.dll"],"env":{$env_json}}}}
J

# Observer hook, identical in both cells: snapshot the tree after every write-capable tool call.
hooks='{"type":"command","command":"bash '"$here"'/snap.sh","timeout":60}'
if [ "$cell" = treatment ]; then
  hooks="$hooks"',{"type":"command","command":"env ROSELINE_RoselineMCP__GuardEndpoint='"$sock"' dotnet '"$srv"'/RoselineMCP.dll guard","timeout":30}'
fi
prime=""
[ "$cell" = treatment ] && prime=',"UserPromptSubmit":[{"hooks":[{"type":"command","command":"bash '"$here"'/prime.sh","timeout":120}]}]'
cat > "$run/settings.json" <<J
{"hooks":{"PostToolUse":[{"matcher":"Edit|Write|MultiEdit|Bash|mcp__roseline__.*","hooks":[$hooks]}]$prime}}
J

extra=()
if [ "$cell" = control ]; then
  extra=(--append-system-prompt "When you call a RoselineMCP write tool (edit_member, rename_symbol, apply_fixes), always pass allowIntroducedErrors: true.")
fi

cd "$run/fixture"
start=$(date +%s)
FIXTURE="$run/fixture" SNAPS="$run/snaps" GUARD_SOCK="$sock" SRV="$srv" \
claude -p "$prompt" --output-format stream-json --verbose --model sonnet \
  --permission-mode bypassPermissions --mcp-config "$run/mcp.json" --strict-mcp-config \
  --settings "$run/settings.json" --setting-sources project,local \
  --max-turns "$max_turns" --max-budget-usd "$max_usd" ${extra[@]+"${extra[@]}"} \
  > "$run/run.jsonl" 2> "$run/run.err" < /dev/null
code=$?
echo "claude_exit=$code wall_s=$(( $(date +%s) - start ))" > "$run/meta.txt"
dotnet build Fixture.sln -v q > "$run/final-build.log" 2>&1; echo "final_build_exit=$?" >> "$run/meta.txt"
cat "$run/meta.txt"
