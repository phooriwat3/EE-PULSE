[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot

function Assert-Condition {
    param(
        [Parameter(Mandatory)][bool]$Condition,
        [Parameter(Mandatory)][string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Test-ExactVersion {
    param([Parameter(Mandatory)][string]$Version)

    return $Version -match '^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$'
}

Push-Location $repositoryRoot
try {
    & (Join-Path $PSScriptRoot 'verify-wp01-foundation.ps1')

    $trackedFiles = @(& git ls-files)
    Assert-Condition ($LASTEXITCODE -eq 0) 'Unable to enumerate tracked files with Git.'

    $forbiddenTrackedFiles = @($trackedFiles | Where-Object {
        $name = [System.IO.Path]::GetFileName($_)
        ($name -match '^\.env(?:\..+)?$' -and $name -ne '.env.example') -or
        ([System.IO.Path]::GetExtension($_) -in @('.pem', '.key', '.pfx', '.p12', '.jks')) -or
        $name -match '^id_(?:rsa|dsa|ecdsa|ed25519)'
    })
    Assert-Condition ($forbiddenTrackedFiles.Count -eq 0) "Tracked secret-like files found: $($forbiddenTrackedFiles -join ', ')"

    $secretPatternParts = @(
        '-----BEGIN ' + '(RSA |EC |OPENSSH )?' + 'PRIVATE KEY-----'
        'AK' + 'IA[0-9A-Z]{16}'
        'gh' + '[pousr]_[A-Za-z0-9]{36,}'
        'xox' + '[baprs]-[A-Za-z0-9-]{10,}'
        'sk' + '-[A-Za-z0-9]{32,}'
    )
    $sourceSecretPattern = '(?i)(' + ($secretPatternParts -join '|') + ')'
    $sourceSecretMatches = @(Get-ChildItem -Path $repositoryRoot -Recurse -File -Force |
        Where-Object {
            $_.FullName -notmatch '[\\/](\.git|node_modules|bin|obj|dist|\.npm-cache)[\\/]' -and
            $_.FullName -ne (Join-Path $PSScriptRoot 'verify-wp01-foundation.ps1') -and
            ($_.Extension -in @('.cs', '.csproj', '.json', '.js', '.ts', '.tsx', '.yml', '.yaml', '.md', '.props', '.xml', '.ps1', '.example') -or
             $_.Name -in @('Dockerfile', '.gitignore', '.dockerignore'))
        } |
        Select-String -Pattern $sourceSecretPattern)
    Assert-Condition ($sourceSecretMatches.Count -eq 0) "High-confidence secret material found in the working tree: $($sourceSecretMatches.Path -join ', ')"

    $historySecretPattern = '(' + ($secretPatternParts -join '|') + ')'
    $historyMatches = [System.Collections.Generic.List[string]]::new()
    $commits = @(& git rev-list --all)
    Assert-Condition ($LASTEXITCODE -eq 0) 'Unable to enumerate Git history.'
    foreach ($commit in $commits) {
        $matchingPaths = @(& git grep -I -i -l -E $historySecretPattern $commit -- . ':(exclude)scripts/verify-wp01-foundation.ps1')
        if ($LASTEXITCODE -notin @(0, 1)) {
            throw "Unable to scan commit $commit for high-confidence secret patterns."
        }

        foreach ($matchingPath in $matchingPaths) {
            $historyMatches.Add("$commit`:$matchingPath")
        }
    }
    Assert-Condition ($historyMatches.Count -eq 0) "High-confidence secret pattern found in Git history: $($historyMatches -join ', ')"

    [xml]$centralPackages = Get-Content -LiteralPath 'Directory.Packages.props' -Raw
    $floatingNuGetVersions = @($centralPackages.Project.ItemGroup.PackageVersion | Where-Object {
        $_.Version -and -not (Test-ExactVersion ([string]$_.Version))
    })
    Assert-Condition ($floatingNuGetVersions.Count -eq 0) 'Directory.Packages.props contains a floating or ranged package version.'

    $packageJson = Get-Content -LiteralPath 'src/web/package.json' -Raw | ConvertFrom-Json
    $webVersions = @($packageJson.dependencies.PSObject.Properties + $packageJson.devDependencies.PSObject.Properties)
    $floatingWebVersions = @($webVersions | Where-Object { -not (Test-ExactVersion ([string]$_.Value)) })
    Assert-Condition ($floatingWebVersions.Count -eq 0) "package.json contains non-exact versions: $($floatingWebVersions.Name -join ', ')"

    $lockfileVersion = & node -e "const fs=require('fs'); const lock=JSON.parse(fs.readFileSync('src/web/package-lock.json','utf8')); process.stdout.write(String(lock.lockfileVersion));"
    Assert-Condition ($LASTEXITCODE -eq 0) 'The npm lockfile is not valid JSON.'
    Assert-Condition ([int]$lockfileVersion -ge 3) 'The npm lockfile must use lockfileVersion 3 or newer.'

    $composeJson = & docker compose config --format json
    Assert-Condition ($LASTEXITCODE -eq 0) 'docker compose config failed.'
    $compose = $composeJson | ConvertFrom-Json
    Assert-Condition ($compose.networks.data.internal -eq $true) 'Compose data network must remain internal.'

    foreach ($serviceProperty in $compose.services.PSObject.Properties) {
        $serviceName = $serviceProperty.Name
        $service = $serviceProperty.Value
        Assert-Condition ($service.privileged -ne $true) "Compose service '$serviceName' must not be privileged."
        Assert-Condition ($service.network_mode -ne 'host') "Compose service '$serviceName' must not use host networking."

        $publishedPorts = @($service.ports | Where-Object { $null -ne $_ })
        if ($serviceName -eq 'api') {
            Assert-Condition ($publishedPorts.Count -eq 1) 'The development API must expose exactly one host port.'
            Assert-Condition ($publishedPorts[0].target -eq 8080) 'The development API host port must target container port 8080.'
        }
        else {
            Assert-Condition ($publishedPorts.Count -eq 0) "Data service '$serviceName' must not expose a host port."
        }

        if ($service.image) {
            Assert-Condition ($service.image -match ':[^/]+$') "Compose image '$($service.image)' must have an explicit tag."
            Assert-Condition ($service.image -notmatch ':latest$') "Compose image '$($service.image)' must not use latest."
        }
    }

    $dockerfileImages = @(Select-String -LiteralPath 'src/backend/EePulse.Api/Dockerfile' -Pattern '^FROM\s+(?<image>\S+)' | ForEach-Object {
        $_.Matches[0].Groups['image'].Value
    })
    Assert-Condition ($dockerfileImages.Count -gt 0) 'The API Dockerfile must declare at least one base image.'
    foreach ($image in $dockerfileImages) {
        Assert-Condition ($image -match ':[^/]+$') "Dockerfile image '$image' must have an explicit tag."
        Assert-Condition ($image -notmatch ':latest$') "Dockerfile image '$image' must not use latest."
    }

    $optionalScanners = @('gitleaks', 'trivy') | ForEach-Object {
        [pscustomobject]@{ Name = $_; Available = [bool](Get-Command $_ -ErrorAction SilentlyContinue) }
    }
    foreach ($scanner in $optionalScanners) {
        $state = if ($scanner.Available) { 'available' } else { 'not installed (WP-11 gap)' }
        Write-Output "Optional scanner $($scanner.Name): $state"
    }

    Write-Output 'Quality/security gate passed: foundation, tracked/history secret checks, exact dependency versions, lockfile, Compose exposure, and pinned container tags.'
}
finally {
    Pop-Location
}
