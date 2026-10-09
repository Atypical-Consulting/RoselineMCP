#!/usr/bin/env bash
# Treatment only. Two measured properties of the compile guard force this priming step:
#  1. It is silent on the first write it sees for a solution (the baseline is established from disk
#     AFTER that write, so there is no before-state to diff).
#  2. Its baseline Solution holds file-backed documents whose text Roslyn reads lazily, so a verify
#     that is the first to touch them reads the ALREADY-EDITED file as the "before" state and reports
#     nothing introduced (observed: introduced=0, preexisting=1 on an in-project `return "x";`).
# So: call 1 establishes the baseline; a whitespace-only touch then makes call 2 run a verify, which
# materialises every document's text in the baseline; the touch is undone. The tree is byte-identical
# to the original afterwards. Runs once, on UserPromptSubmit, before the agent has done anything.
cat > /dev/null
f="$FIXTURE/Core/OrderPricing.cs"
call() {
  printf '{"hook_event_name":"PostToolUse","tool_name":"Edit","cwd":"%s","tool_input":{"file_path":"%s"}}' "$FIXTURE" "$f" |
    ROSELINE_RoselineMCP__GuardEndpoint="$GUARD_SOCK" ROSELINE_RoselineMCP__GuardTimeout=90000 \
    dotnet "$SRV/RoselineMCP.dll" guard > /dev/null 2>&1
}
call
cp "$f" "$f.orig"
printf '\n' >> "$f"
call
mv "$f.orig" "$f"
exit 0
