# End-to-end tests

Run `npm run build`, then `npm run test:e2e -- --workers=1` from `src/web`. The current six Playwright cases cover existing inventory flows, the read-only operations dashboard, site filtering, incident detail, keyboard focus, mobile containment, loading/empty/error/stale-refresh states, logout, and the fail-closed production bundle. Development requests are intercepted with test-only fixtures; these results do not establish real backend end-to-end behavior. No fixture is imported by application source.

Set `UI_EVIDENCE` to a writable evidence directory to persist desktop/mobile screenshots. The mocked suite owns two separate servers: Development on port 4174 and built-production preview on port 4175. It does not reuse an already running server.

`real-backend/operations.spec.ts` is excluded from that mocked configuration. Run it through the explicit `OperationsBrowserAcceptanceTests` fixture, following [the frontend README](../../src/web/README.md#isolated-real-backend-acceptance). Its isolated configuration requires the fixture environment and runs without interception against Vite → Release API → disposable PostgreSQL. It records actual response/persistence evidence and desktop/mobile screenshots. A loopback test-only controller performs bounded out-of-band changes to its own database; it does not mock API responses. The fixture owns cleanup and ephemeral credentials/keys.

OIDC, command forms, Agent queue recovery, maintenance scenarios, SignalR, audit, broader timeline/metrics, and plant mapping remain outside this checkpoint.
