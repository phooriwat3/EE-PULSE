# API artifacts

The API publishes the v1 OpenAPI document at `/openapi/v1.json`. The Lead-reviewed v1 artifact is checked in as `openapi-v1.json` and is the contract input for frontend client generation.

The 2026-09-27 WP-07 correction checkpoint includes the final-verified dashboard summary, device-status, and incident read/action routes while retaining the existing timezone-preference operations. It contains 38 paths, 47 operations (44 protected), and 75 schemas. The artifact is 262,044 bytes with SHA-256 `8DDFF01A2998CC8540E93A9404EC1E341FFBCE2DF6F6BDE0463165AECADA6A55`. SignalR, audit listing, timeline, metrics, and frontend/UI remain deferred.

The verified Release endpoint generated this artifact twice with byte-identical output. `Wp07OpenApiContractTests` guards the checked-in artifact's runtime semantic equality (apart from the request-derived `servers` URL) and the frozen route/schema, conditional-header, media-type, and constraint metadata.

Regenerate it only from a verified, final API build:

```powershell
curl.exe --noproxy '*' --silent --show-error --fail-with-body --output .\docs\api\openapi-v1.json http://localhost:8080/openapi/v1.json
```

After regeneration, run the integration tests and verify that the checked-in artifact contains the expected inventory operations, response schemas, Bearer security requirements, 401/403 responses, and unauthenticated health operations. Do not hand-edit the generated JSON.

Frontend policy: generate or derive typed API clients from this checked-in v1 artifact, keep generated code in a clearly identified frontend path, and fail CI when regeneration produces an unexplained diff. Compatible additions may extend v1 after Lead review; breaking changes require a new API/schema version.

The approved and frozen WP-03 additive design contract is recorded in `wp03-agent-contract-proposal.md`. It does not alter the frozen WP-02 artifact. During implementation, regenerate `openapi-v1.json` from the verified API rather than hand-editing it.
