# API artifacts

The API publishes the v1 OpenAPI document at `/openapi/v1.json`. The Lead-reviewed WP-02 snapshot is checked in as `openapi-v1.json` and is the contract input for frontend client generation.

Regenerate it only from a verified, final API build:

```powershell
curl.exe --noproxy '*' --silent --show-error --fail-with-body --output .\docs\api\openapi-v1.json http://localhost:8080/openapi/v1.json
```

After regeneration, run the integration tests and verify that the checked-in artifact contains the expected inventory operations, response schemas, Bearer security requirements, 401/403 responses, and unauthenticated health operations. Do not hand-edit the generated JSON.

Frontend policy: generate or derive typed API clients from this checked-in v1 artifact, keep generated code in a clearly identified frontend path, and fail CI when regeneration produces an unexplained diff. Compatible additions may extend v1 after Lead review; breaking changes require a new API/schema version.

The approved and frozen WP-03 additive design contract is recorded in `wp03-agent-contract-proposal.md`. It does not alter the frozen WP-02 artifact. During implementation, regenerate `openapi-v1.json` from the verified API rather than hand-editing it.
