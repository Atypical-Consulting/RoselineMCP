#!/usr/bin/env python3
"""Scores one run directory produced by run-cell.sh. Usage: analyze.py <run-dir>

Per run it reports the four pre-registered variables, plus turns and tool calls (method in
docs/AGENT-BENCHMARK.md "Capturing turns and tool calls"):
  final_compiles      exit code of `dotnet build Fixture.sln` on the finished tree (meta.txt)
  broken_states       number of DISTINCT snapshot trees (taken after every write-capable tool call)
                      that do not compile; consecutive identical trees count once
  turns_to_green      assistant turns from the first tree that did not compile until the first later
                      tree that does; 0 when no snapshot ever broke; None when it never went green
  tokens              input + cache-creation + cache-read + output, from the result event's usage
"""
import hashlib, json, os, shutil, subprocess, sys, tempfile

run = sys.argv[1]
events = [json.loads(l) for l in open(f"{run}/run.jsonl") if l.strip()]

# turn index (1-based, by distinct assistant message id) of every tool_use id
turn_of, seen, tool_calls = {}, [], {}
for e in events:
    if e.get("type") != "assistant":
        continue
    mid = e["message"]["id"]
    if mid not in seen:
        seen.append(mid)
    for c in e["message"]["content"]:
        if c.get("type") == "tool_use":
            turn_of[c["id"]] = seen.index(mid) + 1
            tool_calls[c["name"]] = tool_calls.get(c["name"], 0) + 1
result = next((e for e in events if e.get("type") == "result"), {})
u = result.get("usage", {})
tokens = sum(u.get(k, 0) for k in ("input_tokens", "cache_creation_input_tokens", "cache_read_input_tokens", "output_tokens"))

def tree_hash(path):
    h = hashlib.sha256()
    for root, _, files in sorted(os.walk(path)):
        for f in sorted(files):
            if f.endswith((".cs", ".csproj", ".sln")):
                p = os.path.join(root, f)
                h.update(p[len(path):].encode()); h.update(open(p, "rb").read())
    return h.hexdigest()

def compiles(tree):
    tmp = tempfile.mkdtemp()
    try:
        shutil.copytree(tree, tmp + "/t")
        r = subprocess.run(["dotnet", "build", "Fixture.sln", "-v", "q"], cwd=tmp + "/t", capture_output=True, text=True)
        return r.returncode == 0
    finally:
        shutil.rmtree(tmp, ignore_errors=True)

snaps = sorted(d for d in os.listdir(f"{run}/snaps") if d[0].isdigit())
states, last = [], None   # (turn, compiles)
for s in snaps:
    tree = f"{run}/snaps/{s}/tree"
    h = tree_hash(tree)
    if h == last:
        continue
    last = h
    payload = json.load(open(f"{run}/snaps/{s}/payload.json"))
    states.append((turn_of.get(payload.get("tool_use_id")), compiles(tree), payload.get("tool_name")))

broken = [i for i, (_, ok, _) in enumerate(states) if not ok]
ttg = 0
if broken:
    first_bad = broken[0]
    ttg = None
    for t, ok, _ in states[first_bad + 1:]:
        if ok:
            ttg = (t - states[first_bad][0]) if t and states[first_bad][0] else "?"
            break
meta = dict(l.strip().split("=", 1) for l in open(f"{run}/meta.txt") if "=" in l)
edit_tools = sorted({t for _, _, t in states if t})
out = {
    "run": os.path.basename(run),
    "final_compiles": meta.get("final_build_exit") == "0",
    "distinct_states": len(states),
    "broken_states": len(broken),
    "turns_to_green": ttg,
    "turns": len(seen),
    "num_turns_reported": result.get("num_turns"),
    "tokens": tokens,
    "cost_usd": result.get("total_cost_usd"),
    "result_subtype": result.get("subtype"),
    "tool_calls": tool_calls,
    "states": [(t, ok, tool) for t, ok, tool in states],
}
print(json.dumps(out))
