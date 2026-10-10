# Verification record

## Automated checks

| Check | Result |
| --- | --- |
| Security/API/image/import/statistics/browser regression suite | 48 passed |
| Extension sender, credential isolation, HTTP errors and Firefox MV2 compatibility | 5 passed |
| Chart HTML escaping and color validation | 2 passed |
| Existing BuildingBlocks unit suite | 7 passed |
| Existing architecture suite | 8 passed |
| Frontend production build and TypeScript check | passed |
| Chromium MV3 and Firefox MV2 production builds | passed |
| Extension TypeScript check | passed |
| Windows self-contained publication with .NET 10.0.12 | passed |

The 48-test security suite also passed in its self-contained .NET 10.0.12 build, matching the product publication runtime.

The security suite checks anonymous reads/exports/mutations, one-use and expired login/pairing credentials, session Origin/CSRF, extension scope/revocation/rate limits, bounded reporting, input limits, Windows junction containment, image validation, transactional import rollback and cleanup, legacy import/icon migration, 100,000 interval comparisons, app/website hours across midnight, website ranking/timeline, timezone/DST conversion, idle/suspend accounting, and icon upload/reference deletion.

Chrome and Firefox interface tests use a loopback Kestrel host, the actual production frontend, the security middleware and actual ScreenTime endpoints backed by a temporary SQLite database. Desktop settings are mocked. They verify fragment removal, session cookies, hostile names shown as input text, and pairing/revocation UI. Extension tests mock browser APIs and do not establish installed-extension integration.

## Reproduce

From `src/frontend`, run `pnpm install --frozen-lockfile`, `pnpm build`, and `pnpm test`.

From `src/web-extension`, run `pnpm install --frozen-lockfile`, `pnpm compile`, `pnpm test`, `pnpm build`, and `pnpm build:firefox`.

From the repository root:

```text
dotnet run --project tests/ScreenTimeTracker.SecurityTests
dotnet run --project tests/BuildingBlocks/ScreenTimeTracker.BuildingBlocks.UnitTests
dotnet run --project tests/ScreenTimeTracker.ArchitectureTests
dotnet publish src/Hosts/ScreenTimeTracker.Desktop -c Release -r win-x64 --self-contained true -p:RuntimeFrameworkVersion=10.0.12 -o artifacts/security-win-x64
```

UI tests require installed Chrome, built frontend assets and the Playwright Firefox test browser. After building the test project, use its generated `playwright.ps1 install firefox` script. `PLAYWRIGHT_BROWSERS_PATH` can select an isolated cache; this workspace uses `artifacts/test-browsers` when present. No personal browser profile is used.

For a runtime-matched safety check, publish the security test project with `-r win-x64 --self-contained true -p:RuntimeFrameworkVersion=10.0.12` to `artifacts/security-tests`, then run its executable.

For offline dependency comparison, download the public GitHub Advisory Database archive and run:

```text
python security/audit-dependencies.py /path/to/advisory-database.tar.gz
```

The report retains the unpatched Android development dependency finding; this command returns nonzero because the finding is still present. It scans public advisory ranges locally, including npm lock graphs, NuGet restore graphs and the application dependency manifest. It is not a penetration test or a native binary vulnerability proof.

## Manual checks still pending

- Native Photino window and tray login on Windows, including reopening after session expiration.
- Extension installation/upgrade and actual pairing, active-tab reporting, favicon refresh and revocation in Chromium and Firefox.
- Real idle, sleep/resume and timer drift under Windows OS events, using fictional browser activity and an isolated database.

No actual use-history database was modified during automated verification.
