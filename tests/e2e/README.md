# End-to-end tests

Run `npm run build`, then `npm run test:e2e -- --workers=1` from `src/web`. The current six Playwright cases cover existing inventory flows, the read-only operations dashboard, site filtering, incident detail, keyboard focus, mobile containment, loading/empty/error/stale-refresh states, logout, and the fail-closed production bundle. Development requests are intercepted with test-only fixtures; these results do not establish real backend end-to-end behavior. No fixture is imported by application source.

Set `UI_EVIDENCE` to a writable evidence directory to persist desktop/mobile screenshots. The suite owns two separate servers: Development on port 4174 and built-production preview on port 4175. It does not reuse an already running server. OIDC, command forms, Agent queue recovery, maintenance scenarios, SignalR, audit, broader timeline/metrics, and plant mapping are outside this frontend checkpoint.
