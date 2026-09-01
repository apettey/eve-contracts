# Profit Scanner — optimization log

Eight measured iterations over the public-contract pipeline. Local numbers from
`tests/EveContracts.Bench` (real pipeline code paths, synthetic Forge-scale data:
20k contracts / 50k items / 1.5k types, Release, median of runs). Live numbers
from scanning The Forge (34,385 contracts, 35 pages) against real ESI.

## Where it started

The first live run exposed the baseline's failure mode: all 34,286 item fetches
were held in memory for 6.5 minutes, then the single save died on SQLite's
`too many SQL variables` limit (34k-parameter `IN` clause) — total loss, nothing
on screen the whole time.

## Iterations

| # | Change | Measured effect |
|---|--------|-----------------|
| 1 | **Progressive item pipeline** — fetch/save/price/evaluate in 500-contract chunks, UI notified per chunk | First scannable rows **4.4 s** after scan start (baseline: nothing until the full fetch, which crashed); progress durable across restarts |
| 2 | **Raw SQLite bulk writes** — prepared `INSERT … ON CONFLICT` in one transaction replaces EF change tracking for scan upserts + item inserts | Upsert 888 → 253 ms warm, 3 611 → 384 ms cold; 50k item rows in ~300 ms; the crash from #0 is structurally impossible |
| 3 | **Lean parallel evaluation + static-data cache** — untracked projections in, `Parallel.For` pure evaluation, changed-rows-only writeback; SDE tables cached in memory | Evaluate 559 → 169 ms warm, 2 502 → 376 ms cold; unchanged passes write 0 rows; killed the giant `IN()` queries |
| 4 | **Parallel page fetch** — region pages 2..N at concurrency 8 (page 1 keeps its ETag) | 35-page Forge sweep ~6 s → ~1.5 s |
| 5 | **Scoped price/volume refresh** — per-type ESI history only for included, in-scope (ship/module) items of live contracts; Fuzzwork quote batches 3-wide; prepared price upserts | History calls for a Forge scan: thousands → **495**; a 400 on an untracked type no longer aborts the cycle |
| 6 | **In-memory scanner snapshot** — pre-sorted evaluated rows held in memory, invalidated only when sync lands data | Filter/slider query 31 ms → **0.06 ms** median (p95 0.07); rebuild ~290 ms at data-update frequency |
| 7 | **Startup path** — WAL + `synchronous=NORMAL`, bulk SDE inserts, stale-but-present SDE refreshes in background instead of blocking | UI reads never contend with sync writes; startup with existing data is instant; SDE save no longer tracks 63k entities |
| 8 | **Churn & resilience polish** — snapshot rebuilds throttled to 1/2 s during sync, item concurrency 8→10, fail-fast on per-contract 504s, unparseable ESI bodies retried next cycle, header stops re-querying characters per click | ~20 s of redundant rebuilds per first run removed; persistent-504 contracts can't stall chunk slots or drain the ESI error budget |

## Live-run findings folded back in

- ESI serves `200` with an empty body occasionally → treated as retryable, never cached as "no items".
- Blocks of consecutive contract ids persistently `504` (freshly deleted contracts) → 1 retry, then next cycle.
- `/markets/{region}/history/` returns `400` for types not tracked on market → recorded as zero volume so the staleness window stops retrying.
- A `304` on the contract list must still drain any item backlog left by an interrupted run.

## Bench summary (20k contracts)

| Stage | Baseline | Final |
|---|---|---|
| Scan upsert (warm) | 888 ms | ~200 ms |
| Scan upsert (cold) | 3 611 ms | ~400 ms |
| Evaluate all | 448–559 ms | ~170 ms |
| UI filter query | 31 ms | 0.06 ms |
| Detail panel | 0.5 ms | 0.6 ms |

Reproduce: `dotnet run --project tests/EveContracts.Bench -c Release [-- <contracts> <iterations>]`
