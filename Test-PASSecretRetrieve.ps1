<#
    Test-PASSecretRetrieve.ps1
    Dumps the RAW JSON shape of PAS text-secret retrieval so we can see why our parser hits
    "requires an element of type 'Object' but target has type 'Null'".

    Talks directly to PAS - no app/containers. Read-only (retrieves a secret's contents into
    memory and prints structure, with an option to mask the actual secret value).

    USAGE (PowerShell 7):
      .\Test-PASSecretRetrieve.ps1 `
        -TenantBaseUrl "https://2.my.centrify.net" `
        -AppId "hackerman" `
        -ClientId "api@andrew.com" `
        -ClientSecret "..." `
        -Scope "all"

    It will: auth, query a few Text secrets via RedRock, then call the retrieval endpoint on
    ONE of them and print the full response structure.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$TenantBaseUrl,
    [Parameter(Mandatory)] [string]$AppId,
    [Parameter(Mandatory)] [string]$ClientId,
    [Parameter(Mandatory)] [string]$ClientSecret,
    [string]$Scope = "all",
    [switch]$ShowSecretValue   # off by default; structure is what we need, not the value
)

$ErrorActionPreference = "Stop"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 -bor [Net.SecurityProtocolType]::Tls13
$TenantBaseUrl = $TenantBaseUrl.TrimEnd('/')

function Hdr($t) { Write-Host "`n##### $t #####" -ForegroundColor Magenta }
$nativeHeader = @{ "X-CENTRIFY-NATIVE-CLIENT" = "true" }

# ---------- 1. Auth ----------
Hdr "STEP 1: Authenticate (OAuth2 client-credentials)"
$tokenUrl = "$TenantBaseUrl/oauth2/token/$AppId"
$tokenBody = @{ grant_type = "client_credentials"; client_id = $ClientId; client_secret = $ClientSecret; scope = $Scope }
try {
    $tok = Invoke-RestMethod -Uri $tokenUrl -Method POST -Body $tokenBody `
        -ContentType "application/x-www-form-urlencoded" -Headers $nativeHeader
    Write-Host "Token acquired (expires_in=$($tok.expires_in))." -ForegroundColor Green
} catch {
    Write-Host "TOKEN FAILED: $($_.ErrorDetails.Message)" -ForegroundColor Red; throw
}
$auth = @{ Authorization = "Bearer $($tok.access_token)"; "X-CENTRIFY-NATIVE-CLIENT" = "true" }

# ---------- 2. Query a few Text secrets ----------
Hdr "STEP 2: RedRock query for Text secrets"
$queryBody = @{
    Script = "SELECT ID, SecretName, Type, ParentPath FROM DataVault WHERE Type='Text'"
    Args   = @{ PageNumber = 1; PageSize = 5; Limit = 5; Caching = -1 }
} | ConvertTo-Json
$q = Invoke-RestMethod -Uri "$TenantBaseUrl/RedRock/Query" -Method POST -Headers $auth -Body $queryBody -ContentType "application/json"
$rows = $q.Result.Results
Write-Host "Returned $($rows.Count) text secrets. IDs and types:" -ForegroundColor Green
$rows | ForEach-Object {
    Write-Host ("  ID={0}  ({1})  Name={2}" -f $_.Row.ID, $_.Row.ID.GetType().Name, $_.Row.SecretName)
}
if ($rows.Count -eq 0) { Write-Host "No text secrets found - nothing to probe." -ForegroundColor Yellow; return }

$probeId = $rows[0].Row.ID
Write-Host "`nProbing retrieval for ID: $probeId" -ForegroundColor Cyan

# ---------- 3. Retrieve contents - DUMP RAW SHAPE ----------
Hdr "STEP 3: POST /ServerManage/RetrieveDataVaultItemContents"
$retrieveBody = @{ ID = $probeId } | ConvertTo-Json
Write-Host "REQUEST BODY: $retrieveBody"
try {
    # Get raw text first so we see the exact JSON, even if it's an error shape.
    $resp = Invoke-WebRequest -Uri "$TenantBaseUrl/ServerManage/RetrieveDataVaultItemContents" `
        -Method POST -Headers $auth -Body $retrieveBody -ContentType "application/json"
    $raw = $resp.Content
    Write-Host "`n--- RAW RESPONSE ---" -ForegroundColor Yellow
    # Pretty-print, but optionally mask the SecretText value.
    $obj = $raw | ConvertFrom-Json
    if (-not $ShowSecretValue -and $obj.Result -and $obj.Result.PSObject.Properties.Name -contains 'SecretText') {
        $obj.Result.SecretText = "***MASKED (length=$($obj.Result.SecretText.Length))***"
    }
    $obj | ConvertTo-Json -Depth 8 | Write-Host

    Write-Host "`n--- STRUCTURE CHECK ---" -ForegroundColor Yellow
    Write-Host ("success field : {0}" -f $obj.success)
    Write-Host ("Result is null?: {0}" -f ($null -eq $obj.Result))
    if ($null -ne $obj.Result) {
        Write-Host ("Result fields : {0}" -f ($obj.Result.PSObject.Properties.Name -join ", "))
    }
    Write-Host ("Message field : {0}" -f $obj.Message)
} catch {
    Write-Host "RETRIEVE FAILED: status=$($_.Exception.Response.StatusCode.value__)" -ForegroundColor Red
    Write-Host "BODY: $($_.ErrorDetails.Message)" -ForegroundColor Red
}

Hdr "DONE"
Write-Host "What we're looking for:"
Write-Host "  - Is 'Result' null while 'success' is true/false? (our code assumes Result is always an object)"
Write-Host "  - Is the text under 'Result.SecretText' or some other field/casing?"
Write-Host "  - Does 'success=false' come back with the value under 'Message' (e.g. checkout/approval needed)?"
