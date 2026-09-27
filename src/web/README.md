# EE Pulse web client

Run `npm ci`, then `npm run dev` from this directory. The existing Vite proxy forwards API requests to `http://localhost:8080`; run the backend separately with its documented Development configuration. Sign in using the Development access screen and select **Operations overview**. Synthetic access is development-only. The production bundle always denies access in this checkpoint: implementing OIDC is a later task, not a configuration switch that enables this UI today.

The operations checkpoint provides API-backed, manually refreshed snapshots: site selection, probe counts, open incidents, recently down probes, offline agents, and incident detail. It is not realtime monitoring. Text accompanies status colors. Loading and snapshot failures are distinct from empty results; retained data is labelled stale after refresh failure and hidden after authorization failure. Pending requests are cancelled on unmount/detail close, query keys include the session, and logout clears the query cache. Times use the browser timezone. The inventory workspace remains available.

Run `npm run generate:contracts` after an approved OpenAPI update. `npm run check:contracts` compares the entire generated file, including its artifact SHA-256. Local references and composition siblings are preserved; unsupported structural keywords fail generation rather than silently producing narrower types. These are TypeScript structural models, not runtime validators: formats without a declared type remain unknown, oneOf exclusivity and length/UUID/timestamp/cross-field constraints are not enforced by TypeScript. JSON int64 values use JavaScript numbers; unsafe integer literals are rejected by the generator and the dashboard refuses to display unsafe integer counts as exact values. Existing inventory types remain separately maintained.

Verify with `npm run check:contracts`, `npm run test:contracts`, `npm run typecheck`, `npm run lint`, `npm run build`, `npm run test`, and `npm run test:e2e -- --workers=1`. The default browser suite starts a Development server and built-production preview on separate ports and uses isolated mocked API responses. The real-backend suite below is opt-in and separate; it does not intercept API responses. Production browser tests require the build first.

## Isolated real-backend acceptance

Prerequisites: pinned .NET SDK, Node/npm dependencies (`npm ci`), installed Playwright Chromium, Docker Linux, and `postgres:18.4-alpine`. On Windows, admit verification only with at least 3 GiB available physical RAM and 10 GiB commit headroom, an active pagefile, sufficient disk, and no active repository runner. Preserve pre-existing containers. Obtain supported execution permission if the sandbox denies Docker/process access; never work around restrictions.

From repository root in the existing terminal:

```powershell
dotnet build tests/EePulse.IntegrationTests/EePulse.IntegrationTests.csproj -c Release -m:1 -nr:false -p:RunAnalyzers=true
# Run the frontend build from src/web before the separate production/mock tests.
$env:UI_EVIDENCE = Join-Path $env:TEMP ('ee-pulse-real-ui-' + [guid]::NewGuid().ToString('N'))
$env:DOCKER_HOST = 'npipe://./pipe/dockerDesktopLinuxEngine'
./tests/EePulse.IntegrationTests/bin/Release/net10.0/EePulse.IntegrationTests.exe -explicit only -class EePulse.IntegrationTests.OperationsBrowserAcceptanceTests -maxThreads 1 -reporter json -trx "$env:UI_EVIDENCE/acceptance.trx" -ctrf "$env:UI_EVIDENCE/acceptance.ctrf.json"
```

On Linux, omit the Windows `DOCKER_HOST` setting and use the built executable for that platform. This explicit test is not silently run by the normal backend suite. Do not set `EE_PULSE_TEST_POSTGRES`: acceptance requires its own disposable container. The fixture creates its evidence directory, random PostgreSQL password and cursor key, migrates a dedicated database through the existing EF migrations, seeds through domain/EF models, and launches the actual Release API in Development plus Vite on isolated loopback ports. Normal `npm run dev` remains unchanged on its documented proxy target.

The seed is fixed: North/South/empty sites, North press and South press Down incidents, North controller Up, and one offline Agent. IDs and UTC seed time are recorded in `fixture.json`. A separate token-protected loopback test controller renames only the fixture's North device or denies/restores connections to only its database. Browser requests still pass through Vite and the real API; the controller never supplies application responses. Assertions cover actual returned/displayed site filters, detail, empty results, all five supported read roles, anonymous/Guest denial at the API boundary, dependency 503/stale/retry, controlled refresh, logout/role change, and inventory reads. Guest is not an invented selectable UI role; every supported role is a reader.

PID files, stdout/stderr, actual child exits, browser JSON, integration-wrapper TRX/CTRF, screenshots, and persistence assertions are written under `UI_EVIDENCE`. The fixture disposes its PostgreSQL, test controller, API and browser/server process tree; API termination at teardown is explicitly recorded as cleanup, not a successful process-exit gate. Cancellation cleanup has an independent deadline. Never dispose containers based merely on a Testcontainers label: preserve resources predating the run. Inspect remaining processes/containers after execution, including failure. This verifies Development integration, not production OIDC, live network probing, or deployment readiness.

This is a read-only UI checkpoint. Command forms, production OIDC integration, complete cursor browsing, realtime updates, audit listing, metrics, and a plant-location map are not delivered here. The summary lists are capped by the published API and are labelled accordingly. Site choices currently load the first 200 sites. No synthetic machine layout or chart is presented as production data. Orange is a brand accent; incident severity uses separate labelled status colors. The company logo asset should be supplied before adding the official mark.
