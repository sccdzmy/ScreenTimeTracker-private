# Background tracking optimizations

## Behavior

The tracking module retains the 1.5-second extension heartbeat and default 15-second session save intervals. The HTTP contract, database schema and history format are unchanged. Existing idle, suspend, timezone and logical-day rules remain in effect.

- A singleton immutable settings snapshot loads after migration. Parallel reads share one refresh; versioned invalidation prevents an in-flight stale read from replacing a newer snapshot. Successful settings saves and committed imports invalidate it. Failed saves and rolled-back imports keep the prior snapshot.
- Application and website identity caches use independent 512-entry LRU stores. They retain IDs and application refresh metadata, never tracked EF entities. Successful identity changes invalidate the caches; applications recheck changed executable paths and expired metadata. Insertions require the matching cache generation.
- Continuous website heartbeats update the in-memory session without resolving a database context. Duplicate application events take the same fast path when metadata remains valid. Changed settings are read from the snapshot on the next worker cycle.
- Website save cycles skip an unchanged identity/start/end tuple. Only a successful save acknowledges that tuple. Application and idle-app sessions still grow and save at the original interval even without window events.
- A module-wide asynchronous coordinator serializes collection, saving, optimization, idle/suspend repair and relevant mutations. Session state is published after database success. Bulk deletes are transactional; deleting current application history starts its next active interval at the deletion time, preventing deleted history from being recreated by the next save.
- Collection failures retain one pending event/report and retry after one second; saver failures retry at the next configured interval. Queue limits continue to apply during retry.
- Entering idle publishes only the idle notification; genuine activity recovery publishes the active notification and samples the foreground window using the injected clock.

## Foreground overload handling

The non-blocking foreground buffer holds at most 256 queued transitions plus one fixed-size overflow summary. A worker can additionally retain one pending retry. Only adjacent equal window identities within the same lifecycle generation are coalesced, preserving the first timestamp.

Overflow retains the first missing timestamp and latest event. The known application ends at the first missing timestamp; missing active time becomes the existing Unknown application, and tracking resumes from the latest event. Idle/suspend generations reject stale pre-boundary events. If a gap spans lifecycle boundaries, recorded active portions are reattributed to Unknown while idle rows and absent sleep intervals remain unchanged. The first observed event in the latest generation bounds the resumed active portion. No window titles, executable paths or hosts are included in overload diagnostics.

The buffer bounds memory rather than guaranteeing every transition during overload. Actual application attribution inside a missing interval is deliberately unavailable. There is no replay of unknown transitions after recovery.

## Reproducible comparison

The same console harness runs against baseline commit `0ef35f2` and the optimized module. Each trial runs in a fresh process, alternates baseline/optimized order and uses self-contained .NET / ASP.NET Core 10.0.12. Three trials per version simulate 2,400 reports for one fictional website, 240 session saves and 840 settings polls against an in-memory SQLite database. Database initialization and snapshot initialization precede the measured workload.

This is a compressed simulated hour, not an hour of wall-clock observation. It calls the real processing/persistence code but excludes HTTP transport, queues, real OS collectors, the UI, installed extensions and power behavior. The console uses reflection only to invoke the same processing seam on both revisions. Results are in `../tests/ScreenTimeTracker.PerformanceTests/results.json`.

| Metric, median of three trials | Baseline | Optimized |
| --- | ---: | ---: |
| SQL commands | 3,721 | 482 |
| Settings reads during workload | 840 | 0 |
| Website identity reads | 2,400 | 1 |
| Managed allocation during workload | 271.08 MiB | 24.11 MiB |
| Process CPU time during workload | 1437.50 ms | 187.50 ms |
| Wall-clock execution time | 2611.13 ms | 715.48 ms |
| Process peak working set, including initialization | 98.64 MiB | 98.14 MiB |

SQL commands fell by approximately 87%; managed allocation fell by approximately 91%. CPU and wall-clock timings are short-workload observations subject to JIT, scheduling and concurrent machine activity. They do not predict the percentage CPU shown in Task Manager. Process working-set peaks are almost unchanged; runtime initialization dominates this small benchmark. No reduction in long-running desktop memory or battery consumption has been established.

Build the performance console with `dotnet publish tests/ScreenTimeTracker.PerformanceTests -c Release -r win-x64 --self-contained true -p:RuntimeFrameworkVersion=10.0.12 -o artifacts/performance-optimized-bin`, then invoke it with `--single-run` for a fresh-process trial. Use the same harness source with the baseline module in an isolated checkout to reproduce the comparison. Native collectors must not be started by the benchmark.

## Verification

- 32 optimization tests: settings coalescing and stale-refresh invalidation; committed/failed settings and imports; LRU eviction and generations; same-site one-hour simulation; unchanged saves and failure retries; duplicate apps, metadata expiry and policy changes; deletion; simultaneous saving/switching; uncommitted-state visibility; midnight; actual idle-monitor notifications; simulated idle/suspend handlers; 10,000-event overload and recovery; gaps spanning idle or sleep.
- 48 existing security tests, including the Chrome and Firefox UI scenarios, passed against the optimized module with the established host in an isolated validation copy.
- 7 BuildingBlocks tests, 8 architecture tests, 2 frontend text-safety tests and 5 extension message tests passed.
- The existing host and the current host source both compile against the optimized module in independent build directories.
- Optimization tests and security tests run on bundled .NET 10.0.12; the existing architecture and BuildingBlocks runners use the installed .NET 10.0.2 runtime. Production code compilation retains strict analyzer checks.

The native tray, installed browser-extension timing, actual Windows suspend/resume and long-running memory/power behavior require manual verification using fictional activity. These are not substituted by the simulated collectors. External database edits require restart. This change does not alter the scheduler architecture or implement batching of accumulated session optimization work.
