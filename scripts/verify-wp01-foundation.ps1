[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot

function Get-RepositoryRelativePath {
    param([Parameter(Mandatory)][string]$Path)

    $absolutePath = [System.IO.Path]::GetFullPath($Path)
    $absoluteRoot = [System.IO.Path]::GetFullPath($repositoryRoot).TrimEnd([System.IO.Path]::DirectorySeparatorChar)
    if (-not $absolutePath.StartsWith($absoluteRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Path is outside the repository: $absolutePath"
    }

    return $absolutePath.Substring($absoluteRoot.Length + 1).Replace('\', '/')
}

$expectedProjects = @{
    'src/backend/EePulse.Contracts/EePulse.Contracts.csproj' = @()
    'src/backend/EePulse.Domain/EePulse.Domain.csproj' = @()
    'src/backend/EePulse.Application/EePulse.Application.csproj' = @(
        'src/backend/EePulse.Contracts/EePulse.Contracts.csproj'
        'src/backend/EePulse.Domain/EePulse.Domain.csproj'
    )
    'src/backend/EePulse.Infrastructure/EePulse.Infrastructure.csproj' = @(
        'src/backend/EePulse.Application/EePulse.Application.csproj'
        'src/backend/EePulse.Domain/EePulse.Domain.csproj'
    )
    'src/backend/EePulse.Api/EePulse.Api.csproj' = @(
        'src/backend/EePulse.Application/EePulse.Application.csproj'
        'src/backend/EePulse.Contracts/EePulse.Contracts.csproj'
        'src/backend/EePulse.Infrastructure/EePulse.Infrastructure.csproj'
    )
    'src/backend/EePulse.Worker/EePulse.Worker.csproj' = @(
        'src/backend/EePulse.Application/EePulse.Application.csproj'
        'src/backend/EePulse.Infrastructure/EePulse.Infrastructure.csproj'
    )
    'src/agent/EePulse.Agent.Core/EePulse.Agent.Core.csproj' = @(
        'src/backend/EePulse.Contracts/EePulse.Contracts.csproj'
    )
    'src/agent/EePulse.Agent.Infrastructure/EePulse.Agent.Infrastructure.csproj' = @(
        'src/agent/EePulse.Agent.Core/EePulse.Agent.Core.csproj'
    )
    'src/agent/EePulse.Agent/EePulse.Agent.csproj' = @(
        'src/agent/EePulse.Agent.Core/EePulse.Agent.Core.csproj'
        'src/agent/EePulse.Agent.Infrastructure/EePulse.Agent.Infrastructure.csproj'
        'src/backend/EePulse.Contracts/EePulse.Contracts.csproj'
    )
    'tests/EePulse.UnitTests/EePulse.UnitTests.csproj' = @(
        'src/backend/EePulse.Application/EePulse.Application.csproj'
        'src/backend/EePulse.Contracts/EePulse.Contracts.csproj'
        'src/backend/EePulse.Infrastructure/EePulse.Infrastructure.csproj'
    )
    'tests/EePulse.IntegrationTests/EePulse.IntegrationTests.csproj' = @(
        'src/backend/EePulse.Api/EePulse.Api.csproj'
        'src/backend/EePulse.Contracts/EePulse.Contracts.csproj'
    )
    'tests/EePulse.Agent.Tests/EePulse.Agent.Tests.csproj' = @(
        'src/agent/EePulse.Agent.Core/EePulse.Agent.Core.csproj'
        'src/agent/EePulse.Agent.Infrastructure/EePulse.Agent.Infrastructure.csproj'
        'src/backend/EePulse.Contracts/EePulse.Contracts.csproj'
    )
    'tests/EePulse.SecurityTests/EePulse.SecurityTests.csproj' = @(
        'src/backend/EePulse.Api/EePulse.Api.csproj'
    )
}

$actualProjects = Get-ChildItem -Path (Join-Path $repositoryRoot 'src'), (Join-Path $repositoryRoot 'tests') -Recurse -Filter '*.csproj' |
    ForEach-Object { Get-RepositoryRelativePath $_.FullName } |
    Sort-Object
$expectedProjectPaths = @($expectedProjects.Keys | Sort-Object)
if (Compare-Object $expectedProjectPaths $actualProjects) {
    throw 'The frozen WP-01 project set changed. Update the ownership decision and this gate through Lead/Integration review.'
}

foreach ($projectPath in $expectedProjectPaths) {
    $absoluteProjectPath = Join-Path $repositoryRoot $projectPath
    [xml]$project = Get-Content -LiteralPath $absoluteProjectPath -Raw
    $projectDirectory = Split-Path -Parent $absoluteProjectPath
    $actualReferences = @($project.Project.ItemGroup.ProjectReference |
        Where-Object { $_ -and $_.Include } |
        ForEach-Object {
        $portableInclude = $_.Include.Replace('\', [System.IO.Path]::DirectorySeparatorChar).Replace('/', [System.IO.Path]::DirectorySeparatorChar)
        $resolved = [System.IO.Path]::GetFullPath((Join-Path $projectDirectory $portableInclude))
        Get-RepositoryRelativePath $resolved
    } | Sort-Object)
    $expectedReferences = @($expectedProjects[$projectPath] | Sort-Object)
    if (Compare-Object $expectedReferences $actualReferences) {
        throw "Project dependency direction changed for $projectPath."
    }
}

$contractsProject = Get-Content -LiteralPath (Join-Path $repositoryRoot 'src/backend/EePulse.Contracts/EePulse.Contracts.csproj') -Raw
$apiVersions = Get-Content -LiteralPath (Join-Path $repositoryRoot 'src/backend/EePulse.Contracts/ApiVersions.cs') -Raw
if ($contractsProject -notmatch '<VersionPrefix>1\.0\.0</VersionPrefix>' -or
    $apiVersions -notmatch 'const int V1 = 1' -or
    $apiVersions -notmatch 'const int Current = V1') {
    throw 'The shared contract package or schema version is not frozen at v1.'
}

foreach ($adrNumber in 1..6) {
    $pattern = 'ADR-{0:D3}-*.md' -f $adrNumber
    if (@(Get-ChildItem -Path (Join-Path $repositoryRoot 'docs/adr') -Filter $pattern).Count -ne 1) {
        throw "Expected exactly one $pattern file."
    }
}

$forbiddenSecretFiles = Get-ChildItem -Path $repositoryRoot -Recurse -File -Force |
    Where-Object {
        $_.FullName -notmatch '[\\/](\.git|node_modules|bin|obj|dist|\.npm-cache)[\\/]' -and
        ($_.Name -match '^\.env(?:\..+)?$' -and $_.Name -ne '.env.example' -or
         $_.Extension -in @('.pem', '.key', '.pfx', '.p12', '.jks') -or
         $_.Name -match '^id_rsa')
    }
if ($forbiddenSecretFiles) {
    throw "Potential secret files found: $($forbiddenSecretFiles.FullName -join ', ')"
}

$textExtensions = @('.cs', '.csproj', '.json', '.js', '.ts', '.tsx', '.yml', '.yaml', '.md', '.props', '.xml', '.example')
$highConfidenceSecretPattern = '(?i)(-----BEGIN (RSA |EC |OPENSSH )?PRIVATE KEY-----|AKIA[0-9A-Z]{16}|gh[pousr]_[A-Za-z0-9]{36,}|xox[baprs]-[A-Za-z0-9-]{10,}|sk-[A-Za-z0-9]{32,})'
$secretMatches = Get-ChildItem -Path $repositoryRoot -Recurse -File -Force |
    Where-Object {
        $_.FullName -notmatch '[\\/](\.git|node_modules|bin|obj|dist|\.npm-cache)[\\/]' -and
        ($textExtensions -contains $_.Extension -or $_.Name -in @('Dockerfile', '.gitignore', '.dockerignore'))
    } |
    Select-String -Pattern $highConfidenceSecretPattern
if ($secretMatches) {
    throw "High-confidence secret material found: $($secretMatches.Path -join ', ')"
}

Write-Output 'WP-01 foundation freeze verified: 12 projects, approved dependency directions, contracts v1, ADR-001..006, and no source-tree secret material.'
