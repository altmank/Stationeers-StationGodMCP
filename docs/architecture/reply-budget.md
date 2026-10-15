# Reply budgets

A direct reply — the kind an agent reads — that grows past 12 KB of JSON is cut to about 4 KB: the hole is filled
with the first whole entries that fit, and the reply gains `ref`, `cut` and a one-line `next` saying how to get the
rest, so nothing is lost and nothing is silently held back (`src/StationGodMCP.Server/Verbs/ReplyCap.cs:24-31`,
`VerbPipeline.Refs.cs:246-300`). The cut exists because the reply lands in the agent's context: a scan that lists
5,000 things is useless as context even when every entry is useful in principle, while a ref and an offset let the
caller take exactly what it wants. Script and program replies are not cut; a run's returned and printed values have
a 4 KB cut of their own, and the sidecar has a last-resort file at 200 KB.

## The 12 KB cap

Only the direct face is capped: the MCP client's calls, not a run's and not a `--hub` program's
(`VerbPipeline.cs:211`; `tests/StationGodMCP.Tests/Hub/RefsAndCapTests.cs:17-24`).

- **Threshold 12 KB.** A reply of at most 12,288 bytes of JSON goes whole; over that it is cut
  (`ReplyCap.cs:29-31`, `:71-74`). The reply's size is measured as the JSON the MCP client receives.
- **Main list.** The cut keeps as many whole entries of the reply's main list as fit in about 4 KB; the main list is
  the what's declared `reply.list`, else the reply's largest list. The rest of the reply's own lists are cut first,
  largest first, to leave the main list at least half the room (`ReplyCap.cs:42-64`, `:104-130`, `:138-180`).
- **Never zero in silence.** A first entry too large for the room is shown shallow (plain values kept, nested lists as
  their length, other objects as `{...}`), or, if even that does not fit, only its `reference_id` with
  `truncated: true`; `cut.shallow` marks the shallow form (`ReplyCap.cs:104-130`, `:193-210`).
- **What it adds.** `ref`: the reply's reference (`rN`). `cut`: `{shown, total}`, plus `shallow` and a `lists` object
  naming every other list the cut shrank, each `{shown, total}`. `next`, one line:
  `whole: repeat 'r4' with whole: true | page: repeat 'r4' with offset: 8 | narrower: add a filter | as a target, r4 is
  the shown entries` — the offset part only when the what pages by offset (`ReplyCap.cs:83-103`, `:215-219`).
- **Never cut.** A report is never cut; one above 256 KB is refused instead (next bullet). A call that asked for its
  size — `whole: true`, `output_file`, or `list_limits` — is not cut, and an error is never cut or shaped
  (`VerbPipeline.Refs.cs:256-272`; `catalogue/verbs/_shared.json`, *shaping*). A reply with no list to cut is not
  cut; it still gains a ref (`ReplyCap.cs:71-74`; `VerbPipeline.Refs.cs:291-295`).
- **A report too large** answers `report_too_large` with `{targets, bytes}` instead of being cut, above 256 KB
  (`ReplyCap.cs:134`; `VerbPipeline.Refs.cs:256-266`).

## Refs

A reply mints `rN` when it is a report, when it was cut, or when its main list has more remaining or three or more
ids; the ref keeps the call and its whole reply (`VerbPipeline.Refs.cs:246-260`, `:296-300`; `RefStore.cs:59-63`).

- `whole: true` on the same call (or `repeat: "rN"` with `whole: true`) lifts the cut. When the call is not a live
  value, the whole reply is answered from the store with `from_store: true`, so no second game call is made
  (`VerbPipeline.Refs.cs:42-78`, `:116-130`).
- `repeat: "rN"` sends the call again with the options given replacing its own; a report repeated with `confirm:
  true` acts on exactly the targets it listed (`VerbPipeline.Refs.cs:42-112`).
- `id: "r4"` on a write is the ids that reply showed; a ref lasts until the world changes
  (`VerbPipeline.Refs.cs:132-160`; `RefsAndCapTests.cs:182-190`).
- A cut reply asked again in the next three direct calls, by `repeat`, by `whole`, or by the same call, is recorded
  as a re-ask (`VerbPipeline.Refs.cs:319-341`; `src/StationGodMCP.Server/Verbs/CallRecorder.cs`).

## The run's own cut

A `run_script` reply's **returned value and printed output are each cut at 4 KB** and say so, with the rest reachable
as `sg.ref('script-N')` (`src/StationGodMCP.Server/Runs/RunText.cs:10-16`, `:38-60`). The hub tells the worker the
limit in the run's welcome, `run.limits.cut_bytes = 4096` (`Runs/Run.cs:171-178`), which is the one place a what
carries a threshold of its own. A run's reply is otherwise text and is never put through the 12 KB cap
(`RunText.cs:9-16`).

## What else bounds a reply

- **The sidecar's inline limit.** A direct reply larger than `--inline-limit-kb` (default 200 KB) is written to a file
  like `output_file: true` and answered by the pointer with `auto_output_file: true`; `0` turns it off
  (`SidecarOptions.cs:19`; `McpAdapter.cs:433-457`).
- **`output_file`.** Any direct reply can be written to a file on the caller's machine instead, with a pointer
  carrying its counts, a short summary of its small values, and the keys that stayed in the file
  (`src/StationGodMCP.Client/OutputFiles.cs:8-10`, `:121-148`).
- **`list_limits`.** `list_limits {list: n}` lifts the mod's own default cut of a list and is one of the asks that
  stops the 12 KB cut (`catalogue/verbs/_shared.json`, *shaping*; `VerbPipeline.Refs.cs:268`).
- **Paging.** A list answers one page (`limit`, `offset`, `total`, `has_more`); the main list's pages are walked by
  the caller, and `next` points at the next offset of a cut reply (`catalogue/verbs/_shared.json`, *paging*).

## The tool list's own budget

The catalogue has a second, unrelated size budget: the MCP tool list. `generated/verbs/sizes.json` records the token
cost of the whole list (16,537 tokens against today's 89,000, a share of 0.186, target at most 0.25), of each verb's
schema (at most 1,500 tokens per tool), and of a fresh context (the 646-token listing plus the five verbs a task
usually loads, 4,666 tokens, against today's 5,147) (`generated/verbs/sizes.json`; `tests/StationGodMCP.Tests/Verbs/VerbSizes.cs:11-27`,
`:58-96`). It is produced from the same generated tool list and held by
`VerbCatalogueTests.TheSizeTargetsHold` (`tests/StationGodMCP.Tests/Verbs/VerbCatalogueTests.cs:925-940`); it has
nothing to do with a reply's size.

## Where the numbers live

`ReplyCap.Threshold` (12 KB), `ReplyCap.Target` (4 KB) and `ReplyCap.ReportLimit` (256 KB) are the only reply-size
constants, in `src/StationGodMCP.Server/Verbs/ReplyCap.cs:29-31`, `:134`; the run cut is `RunText.CutBytes` (4 KB),
`src/StationGodMCP.Server/Runs/RunText.cs:16`; the sidecar's is `SidecarOptions.DefaultInlineLimitKb` (200),
`src/StationGodMCP.Server/SidecarOptions.cs:19`.
