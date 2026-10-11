# Bounded history processing and streamed exports

This change retains usage rules, existing records, separate app and website scheduling, the 1.5-second website report interval, and the default 15-second save interval.

## Implementation

- Both compaction workers share a cursor-based runner. Each transaction reads at most 1024 continuation rows, releases the tracking coordinator, and yields. A round has a two-second soft budget. An unresolved root and cursors survive in memory; intervening rows remain in SQLite until a merge is decided. Minimum duration filtering happens after that decision. Progress advances only after commit. Settings, imports, deletions, identity changes, and historical gap repairs invalidate continuations.
- Four ranking queries aggregate clipped integer milliseconds in parameterized SQLite SQL. Category rankings now bound session starts by the query end. Four hourly/daily query handlers stream minimal fields into the same bucket calculator, retaining logical days and time zones.
- An additive migration adds nonunique `(AppId, StartTime)` and `(WebsiteId, StartTime)` indexes. Existing migrations and business fields are unchanged.
- Version 4 export creates a consistent SQLite backup, then streams rows and one validated icon at a time into a temporary JSON file. The live database connection is released before JSON generation and downloading. Generation does not acquire the tracking coordinator. One generation is allowed at a time; competing requests receive HTTP 409 and Retry-After. Completion, cancellation, failures and startup cleanup remove owned temporary files. The browser downloads the response Blob directly; clipboard export converts it to text only when requested.

## Resource limits and tradeoffs

The two-second budget is checked between completed batches, so a single database statement or busy disk can exceed it. SQLite snapshot creation is synchronous, checks cancellation before and after backup, and may briefly delay writers; subsequent JSON generation and slow downloads do not retain the live database snapshot. Export requires disk space for the database copy and generated JSON. The additional indexes consume disk space and add write maintenance. Slow clients retain their generated JSON file until transfer ends; completed downloads are independent of the generation semaphore.

Temporary paths are server generated under `Data/ExportTemporary`; cleanup refuses paths outside that directory and reparse points. Browser clipboard checks use a mock and do not change the desktop clipboard. All test identities and activities are fictional.

## Validation

The 60 backend optimization tests passed, including bounded/full compaction comparisons, cross-page short sessions, long chains, tied timestamps, rollback/restart, deletion/import/settings invalidation, DST transitions, clipping and millisecond rounding, export round-trip, concurrent writes, cancellation, I/O failure, busy responses and path protection. The additive migration preserves records and its identity/start lookup uses the new index in EXPLAIN QUERY PLAN.

The security run used the existing host/security suite in an isolated source copy with the current backend and export regression cases, leaving concurrent native UI work outside that suite. All 49 security cases passed: 47 in the initial run and the two corrected Chrome/Firefox browser cases in a targeted rerun. The rerun fixed test selectors and a CSP-incompatible wait expression, without weakening product CSP. The 7 building-block tests, 8 architecture tests and 7 JavaScript frontend/extension checks also passed. Desktop Release compilation passed with zero warnings and errors in a complete isolated source copy; the frontend production build passed with its existing bundle-size notice. No additional soak or broad test campaign was performed.

## Measurement method

The same console harness runs against commit `50f087a` and the current implementation. Each fresh process seeds a temporary SQLite database with 20 fictional apps and 20 fictional sites, and 10,000, 100,000 or 1,000,000 sessions **per kind** (twice that many total). Each size/version runs three times, sequentially, alternating versions. The workload measures eight statistics queries, a complete export, then complete app and site compaction. Session duration is 8001 ms, starts are spaced by 15 seconds, and merge tolerance remains at its default. This measures independent sessions; long merge chains are covered by correctness tests.

Both benchmark builds use Debug .NET 10.0.2 and identical fake time. Compaction scheduled rounds are compressed until completion while retaining per-round budgets and lock releases. During export and compaction, a separate context attempts a fictional tracking save every 10 ms and measures coordinator wait and save-attempt latency. Because fake time is fixed, the initial attempt inserts a session; subsequent attempts perform the lookup and unchanged SaveChanges rather than advancing activity. This probe stresses coordination more often than the production default, but does not measure sustained changing-session writes or isolate the added indexes’ write cost. The raw `maxSaveMilliseconds` field describes these attempts, not a sustained-write result. Concurrent changed-data writes are covered by functional export tests. Allocation and CPU include the probe; SQL counts include its commands but exclude SQLite backup internals. Save sample counts vary with workload duration. Working-set peaks include runtime, SQLite caches and previous stages; they are not idle background memory or power measurements. Raw sanitized numerical results are stored with the harness.

Reproduce with the console project `tests/ScreenTimeTracker.LargeHistoryTests`, passing the desired per-kind session count. Run versions sequentially with no simultaneous builds/tests; use fresh processes for every trial. Do not use personal usage databases.

## Remaining runtime validation

Sustained write latency and isolated index maintenance cost remain unmeasured. Long-term Windows tracking, real tray interactions, real extension integration and battery use have not been measured in this round. Headless Chrome/Firefox checks cover the existing web UI and export behavior with fictional data. Logic tests do not replace those runtime checks.

## Measured results

Each number is the median of three fresh-process trials. Peak memory is sampled working set; allocation is cumulative, not retained memory. Coordinator wait and save-attempt columns are medians of the per-trial maxima. Statistics has no save probe.

| Sessions per kind | Version | Stage | Wall seconds | CPU seconds | Allocated MiB | Peak MiB | SQL commands | Coordinator wait ms | Save attempt ms |
| ---: | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 10,000 | baseline | statistics | 0.444 | 0.031 | 84.6 | 101.6 | 12 | 0.00 | 0.00 |
| 10,000 | baseline | export | 0.252 | 0.047 | 31.9 | 104.9 | 13 | 0.48 | 18.09 |
| 10,000 | baseline | compaction | 1.199 | 0.219 | 222.4 | 131.9 | 20,008 | 626.28 | 3.39 |
| 10,000 | optimized | statistics | 0.223 | 0.031 | 9.1 | 90.6 | 12 | 0.00 | 0.00 |
| 10,000 | optimized | export | 0.375 | 0.062 | 12.2 | 95.6 | 19 | 0.52 | 17.98 |
| 10,000 | optimized | compaction | 0.300 | 0.031 | 24.9 | 102.8 | 74 | 11.16 | 3.42 |
| 100,000 | baseline | statistics | 3.318 | 0.938 | 807.0 | 121.8 | 12 | 0.00 | 0.00 |
| 100,000 | baseline | export | 1.228 | 0.266 | 294.6 | 242.0 | 63 | 0.51 | 24.73 |
| 100,000 | baseline | compaction | 14.476 | 6.562 | 2113.5 | 249.4 | 200,008 | 8016.86 | 6.02 |
| 100,000 | optimized | statistics | 1.929 | 0.609 | 75.4 | 107.3 | 12 | 0.00 | 0.00 |
| 100,000 | optimized | export | 2.525 | 1.719 | 102.8 | 118.6 | 110 | 0.48 | 24.89 |
| 100,000 | optimized | compaction | 2.682 | 1.125 | 232.3 | 124.3 | 669 | 59.38 | 10.88 |
| 1,000,000 | baseline | statistics | 40.149 | 9.672 | 7959.4 | 230.7 | 12 | 0.00 | 0.00 |
| 1,000,000 | baseline | export | 10.206 | 3.266 | 2806.2 | 1424.5 | 414 | 0.57 | 25.60 |
| 1,000,000 | baseline | compaction | 70.624 | 12.734 | 20774.0 | 1545.3 | 2,000,008 | 39981.95 | 3.35 |
| 1,000,000 | optimized | statistics | 19.092 | 3.953 | 738.0 | 105.5 | 12 | 0.00 | 0.00 |
| 1,000,000 | optimized | export | 28.795 | 4.594 | 1002.3 | 122.4 | 962 | 0.63 | 150.45 |
| 1,000,000 | optimized | compaction | 40.026 | 3.891 | 2357.1 | 124.9 | 6,962 | 351.14 | 160.88 |

Compaction reduces SQL command volume and time held by the tracking coordinator. Streamed statistics reduce allocation. Export lowers allocation and peak memory but takes longer in this workload because it creates a database snapshot and temporary JSON rather than serializing an in-memory graph. Saving attempt costs above do not establish sustained writer performance or isolate index maintenance.

The full sanitized samples are in [results.json](../tests/ScreenTimeTracker.LargeHistoryTests/results.json).
