[CmdletBinding()]
param(
    [string]$OpenApiPath = 'docs/api/openapi-v1.json'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$wp02Commit = '34718aa13727d8e84e5f56b61e854cbbabc5adab'
$initialMigration = 'src/backend/EePulse.Infrastructure/Persistence/Migrations/20260810040920_InitialInventory.cs'
$initialMigrationDesigner = 'src/backend/EePulse.Infrastructure/Persistence/Migrations/20260810040920_InitialInventory.Designer.cs'

function Assert-Condition {
    param(
        [Parameter(Mandatory)][bool]$Condition,
        [Parameter(Mandatory)][string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Get-Operation {
    param(
        [Parameter(Mandatory)]$Document,
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Method
    )

    $pathProperty = $Document.paths.PSObject.Properties[$Path]
    Assert-Condition ($null -ne $pathProperty) "OpenAPI path is missing: $Path"
    $operation = $pathProperty.Value.PSObject.Properties[$Method]
    Assert-Condition ($null -ne $operation) "OpenAPI operation is missing: $($Method.ToUpperInvariant()) $Path"
    return $operation.Value
}

function Assert-SecurityScheme {
    param(
        [Parameter(Mandatory)]$Operation,
        [Parameter(Mandatory)][string]$Scheme,
        [Parameter(Mandatory)][string]$Label
    )

    $requirements = @($Operation.security)
    Assert-Condition ($requirements.Count -eq 1) "$Label must declare exactly one security requirement."
    $matches = @($requirements | Where-Object { $null -ne $_.PSObject.Properties[$Scheme] })
    Assert-Condition ($matches.Count -eq 1) "$Label must require only the $Scheme security scheme."
    foreach ($requirement in $requirements) {
        Assert-Condition ($requirement.PSObject.Properties.Count -eq 1) "$Label has an unexpected additional security scheme."
    }
}

function Assert-Response {
    param(
        [Parameter(Mandatory)]$Operation,
        [Parameter(Mandatory)][string]$Status,
        [Parameter(Mandatory)][string]$Label
    )

    Assert-Condition ($null -ne $Operation.responses.PSObject.Properties[$Status]) "$Label is missing response $Status."
}

function Test-SecretSchemaNodes {
    param(
        [Parameter(Mandatory)]$Node,
        [string]$Location = '$'
    )

    if ($null -eq $Node) {
        return
    }

    if ($Node -is [System.Collections.IEnumerable] -and $Node -isnot [string] -and
        $Node -isnot [System.Management.Automation.PSCustomObject]) {
        $index = 0
        foreach ($item in $Node) {
            Test-SecretSchemaNodes $item "$Location[$index]"
            $index++
        }
        return
    }

    if ($Node -isnot [System.Management.Automation.PSCustomObject]) {
        return
    }

    foreach ($property in $Node.PSObject.Properties) {
        $childLocation = "$Location.$($property.Name)"
        if ($property.Name -in @('enrollmentToken', 'agentCredential')) {
            $secretSchema = $property.Value
            Assert-Condition ($secretSchema.writeOnly -eq $true) "$childLocation must be writeOnly."
            Assert-Condition ($null -eq $secretSchema.PSObject.Properties['example']) "$childLocation must not have an example."
            Assert-Condition ($null -eq $secretSchema.PSObject.Properties['default']) "$childLocation must not have a default."
            Assert-Condition ($null -eq $secretSchema.PSObject.Properties['enum']) "$childLocation must not expose an enum value."
        }
        Test-SecretSchemaNodes $property.Value $childLocation
    }
}

Push-Location $repositoryRoot
try {
    & git cat-file -e "$wp02Commit`^{commit}"
    Assert-Condition ($LASTEXITCODE -eq 0) "Frozen WP-03 contract commit $wp02Commit is unavailable."

    & git diff --quiet $wp02Commit -- $initialMigration $initialMigrationDesigner
    Assert-Condition ($LASTEXITCODE -eq 0) 'The committed WP-02 migration was modified.'

    $migrationRoot = 'src/backend/EePulse.Infrastructure/Persistence/Migrations'
    $migrationFiles = @(Get-ChildItem -LiteralPath $migrationRoot -Filter '*.cs' -File |
        Where-Object { $_.Name -notlike '*.Designer.cs' -and $_.Name -ne 'EePulseDbContextModelSnapshot.cs' })
    Assert-Condition ($migrationFiles.Count -eq 2) "Expected the WP-02 migration plus exactly one additive WP-03 migration; found $($migrationFiles.Count)."
    Assert-Condition (($migrationFiles | Where-Object Name -ne '20260810040920_InitialInventory.cs').Count -eq 1) 'Unable to identify exactly one additive WP-03 migration.'

    $migrationText = ($migrationFiles | Where-Object Name -ne '20260810040920_InitialInventory.cs' | Get-Content -Raw)
    Assert-Condition ($migrationText -match '(?i)ck_agent_enrollment_token_digest[^\r\n]+octet_length\(digest\)\s*=\s*32') 'WP-03 migration does not enforce a fixed 32-byte digest in agent_enrollment_tokens.'
    Assert-Condition ($migrationText -match '(?i)ck_agent_credential_digest[^\r\n]+octet_length\(digest\)\s*=\s*32') 'WP-03 migration does not enforce a fixed 32-byte digest in agent_credentials.'
    Assert-Condition ($migrationText -notmatch '(?i)name:\s*"(?:enrollment_token|agent_credential|token_secret|credential_secret)"') 'WP-03 migration appears to persist a plaintext token or credential column.'
    Assert-Condition ($migrationText -match '(?s)name:\s*"ux_agent_credentials_active".*?unique:\s*true,.*?filter:\s*"state = ''Active''"') 'WP-03 migration is missing the filtered unique Active credential index.'
    Assert-Condition ($migrationText -match '(?s)name:\s*"ux_agent_credentials_pending".*?unique:\s*true,.*?filter:\s*"state = ''Pending''"') 'WP-03 migration is missing the filtered unique Pending credential index.'
    Assert-Condition ($migrationText -notmatch 'migrationBuilder\.Sql\s*\(') 'WP-03 migration must represent schema objects in the EF model instead of raw SQL.'

    Assert-Condition (Test-Path -LiteralPath $OpenApiPath -PathType Leaf) "Generated OpenAPI artifact is missing: $OpenApiPath"
    $openApi = Get-Content -LiteralPath $OpenApiPath -Raw | ConvertFrom-Json
    Assert-Condition ($openApi.openapi -eq '3.1.1') 'OpenAPI must remain version 3.1.1.'

    $schemes = $openApi.components.securitySchemes
    foreach ($schemeName in @('Bearer', 'AgentCredential')) {
        $scheme = $schemes.PSObject.Properties[$schemeName].Value
        Assert-Condition ($null -ne $scheme) "OpenAPI security scheme is missing: $schemeName"
        Assert-Condition ($scheme.type -eq 'http' -and $scheme.scheme -eq 'bearer') "$schemeName must be an HTTP bearer scheme."
    }
    Assert-Condition ($schemes.AgentCredential.bearerFormat -eq 'EE-Pulse-Agent-v1') 'AgentCredential bearerFormat drifted from the frozen contract.'

    $userOperations = @(
        @('/api/v1/agent-enrollment-tokens', 'post'),
        @('/api/v1/agent-enrollment-tokens/{tokenId}', 'delete'),
        @('/api/v1/agents', 'get'),
        @('/api/v1/agents/{agentId}', 'get'),
        @('/api/v1/agent-groups/{agentGroupId}/allowed-networks', 'put'),
        @('/api/v1/agents/{agentId}/allowed-networks', 'put'),
        @('/api/v1/agent-groups/{agentGroupId}/configuration/rollback', 'post'),
        @('/api/v1/agents/{agentId}/revoke', 'post')
    )
    foreach ($entry in $userOperations) {
        $label = "$($entry[1].ToUpperInvariant()) $($entry[0])"
        $operation = Get-Operation $openApi $entry[0] $entry[1]
        Assert-SecurityScheme $operation 'Bearer' $label
        Assert-Response $operation '401' $label
        Assert-Response $operation '403' $label
    }

    $agentOperations = @(
        @('/api/v1/agents/{agentId}/heartbeat', 'post'),
        @('/api/v1/agents/{agentId}/configuration', 'get'),
        @('/api/v1/agents/{agentId}/configuration/acknowledgements', 'post'),
        @('/api/v1/agents/{agentId}/credentials/rotate', 'post')
    )
    foreach ($entry in $agentOperations) {
        $label = "$($entry[1].ToUpperInvariant()) $($entry[0])"
        $operation = Get-Operation $openApi $entry[0] $entry[1]
        Assert-SecurityScheme $operation 'AgentCredential' $label
        foreach ($status in @('401', '403', '410')) {
            Assert-Response $operation $status $label
        }
    }

    $enrollment = Get-Operation $openApi '/api/v1/agents/enroll' 'post'
    Assert-Condition ($null -eq $enrollment.PSObject.Properties['security'] -or @($enrollment.security).Count -eq 0) 'Enrollment bootstrap must not require user or Agent authentication.'
    foreach ($status in @('201', '400', '401', '403', '410', '426', '429')) {
        Assert-Response $enrollment $status 'POST /api/v1/agents/enroll'
    }

    $configurationPull = Get-Operation $openApi '/api/v1/agents/{agentId}/configuration' 'get'
    Assert-Response $configurationPull '304' 'GET /api/v1/agents/{agentId}/configuration'
    $ifNoneMatch = @($configurationPull.parameters | Where-Object { $_.in -eq 'header' -and $_.name -eq 'If-None-Match' })
    Assert-Condition ($ifNoneMatch.Count -eq 1) 'Configuration pull must declare the If-None-Match request header.'
    $configurationOk = $configurationPull.responses.PSObject.Properties['200'].Value
    Assert-Condition ($null -ne $configurationOk.headers -and $null -ne $configurationOk.headers.PSObject.Properties['ETag']) 'Configuration pull response 200 must declare the strong ETag header.'
    $configurationNotModified = $configurationPull.responses.PSObject.Properties['304'].Value
    Assert-Condition ($null -ne $configurationNotModified.headers -and $null -ne $configurationNotModified.headers.PSObject.Properties['ETag']) 'Configuration pull response 304 must declare the strong ETag header.'

    foreach ($pathProperty in $openApi.paths.PSObject.Properties) {
        $isWp03Path = $pathProperty.Name -match '^/api/v1/(?:agent-enrollment-tokens(?:/|$)|agents(?:/|$)|agent-groups/[^/]+/(?:allowed-networks|configuration/rollback)$)'
        if (-not $isWp03Path) {
            continue
        }
        foreach ($method in @('get', 'post', 'put', 'delete', 'patch')) {
            $operationProperty = $pathProperty.Value.PSObject.Properties[$method]
            if ($null -eq $operationProperty) {
                continue
            }
            foreach ($responseProperty in $operationProperty.Value.responses.PSObject.Properties) {
                if ($responseProperty.Name -match '^[45]\d\d$') {
                    $content = $responseProperty.Value.content
                    Assert-Condition ($null -ne $content -and $null -ne $content.PSObject.Properties['application/problem+json']) "$($method.ToUpperInvariant()) $($pathProperty.Name) response $($responseProperty.Name) must declare application/problem+json."
                }
                if ($responseProperty.Name -eq '429') {
                    $headers = $responseProperty.Value.headers
                    Assert-Condition ($null -ne $headers -and $null -ne $headers.PSObject.Properties['Retry-After']) "$($method.ToUpperInvariant()) $($pathProperty.Name) response 429 must declare Retry-After."
                }
            }
        }
    }

    Test-SecretSchemaNodes $openApi

    $closedWp03Schemas = @(
        'CreateAgentEnrollmentTokenRequest', 'CreateAgentEnrollmentTokenResponse',
        'AgentEnrollmentRequest', 'AgentEnrollmentResponse', 'AgentResponse', 'PagedAgentResponse',
        'AgentHeartbeatRequest', 'AgentHeartbeatResponse', 'AgentConfigurationResponse', 'AgentProbeConfiguration',
        'AgentConfigurationAcknowledgementRequest', 'AgentConfigurationAcknowledgementResponse',
        'RotateAgentCredentialRequest', 'RotateAgentCredentialResponse', 'RevokeAgentRequest',
        'UpdateAgentAllowedNetworksRequest', 'UpdateAgentGroupAllowedNetworksRequest', 'AgentNetworkPolicyResponse',
        'RollbackAgentConfigurationRequest', 'AgentConfigurationPublicationResponse'
    )
    foreach ($schemaName in $closedWp03Schemas) {
        $schema = $openApi.components.schemas.PSObject.Properties[$schemaName].Value
        Assert-Condition ($null -ne $schema) "WP-03 schema is missing: $schemaName"
        Assert-Condition ($schema.additionalProperties -eq $false) "$schemaName must set additionalProperties to false."
    }

    $configurationSchema = $openApi.components.schemas.PSObject.Properties['AgentConfigurationResponse'].Value
    Assert-Condition ($null -ne $configurationSchema) 'AgentConfigurationResponse schema is missing.'
    $allowedConfigurationProperties = @('schemaVersion', 'agentId', 'agentGroupId', 'configurationVersion', 'generatedAt', 'rollbackOfVersion', 'allowedNetworks', 'probes')
    $unexpectedConfigurationProperties = @($configurationSchema.properties.PSObject.Properties.Name | Where-Object { $_ -notin $allowedConfigurationProperties })
    Assert-Condition ($unexpectedConfigurationProperties.Count -eq 0) "Agent configuration exposes prohibited or unexpected properties: $($unexpectedConfigurationProperties -join ', ')"

    $sourceFiles = @(Get-ChildItem -Path 'src' -Recurse -Filter '*.cs' -File |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' })
    $fixedTimeComparison = @($sourceFiles | Select-String -SimpleMatch 'CryptographicOperations.FixedTimeEquals')
    Assert-Condition ($fixedTimeComparison.Count -gt 0) 'No constant-time digest comparison was found for token/credential authentication.'

    Write-Output 'WP-03 static contract gate passed: frozen migration, single additive migration, auth separation, errors, secret metadata, configuration closure, and digest controls.'
}
finally {
    Pop-Location
}
