# Quality and security verification

This directory contains cross-cutting QA assets and evidence for the approved WP-02 contract. It does not define public API DTOs, database migrations, authorization implementation, or the inventory CSV schema; those remain Backend and Lead/Integration decisions.

Run the local static/deployment gate from the repository root:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-quality-security.ps1
```

The gate verifies the frozen WP-01 architecture and source secret scan, rejects tracked secret-like filenames, checks reachable Git history for high-confidence secret patterns without printing secret values, requires exact direct dependency versions and the npm lockfile, renders Compose, rejects host exposure of data services and privileged/host networking, and rejects untagged or `latest` container images. `gitleaks` and `trivy` availability is reported but is not treated as evidence when either tool is absent.

Package vulnerability audits remain separate so their network/cache requirements are visible:

```powershell
docker run --rm --mount type=volume,source=ee-pulse-nuget-cache,target=/root/.nuget/packages --mount type=bind,source=${PWD},target=/source --workdir /source mcr.microsoft.com/dotnet/sdk:10.0.302 dotnet list src/backend/EePulse.sln package --vulnerable --include-transitive
npm --prefix .\src\web audit --audit-level=high
```

Use [wp02-test-matrix.md](wp02-test-matrix.md) when reviewing WP-02. It distinguishes verified evidence from partial coverage and genuine gaps; the matrix itself does not replace the referenced executable evidence.
