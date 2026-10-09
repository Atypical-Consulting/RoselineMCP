#!/usr/bin/env bash
# PostToolUse observer: copy the fixture tree into $SNAPS/<seq>/ and log the hook payload.
# Silent, always exit 0 — it must not influence the agent.
payload="$(cat)"
[ -n "${FIXTURE:-}" ] && [ -n "${SNAPS:-}" ] || exit 0
seq=$(printf '%03d' $(( $(ls "$SNAPS" | grep -c '^[0-9]') + 1 )))
mkdir -p "$SNAPS/$seq"
rsync -a --exclude bin --exclude obj --exclude .git "$FIXTURE/" "$SNAPS/$seq/tree/"
printf '%s\n' "$payload" > "$SNAPS/$seq/payload.json"
exit 0
