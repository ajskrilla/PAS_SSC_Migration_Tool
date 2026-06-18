<#
    Test-PASRetrieveShapes.ps1
    The app keeps getting "Parameter 'ID' must be specified" on text-secret retrieval.
    This sends the retrieval request several DIFFERENT ways so we can see exactly which
    body shape PAS accepts - confirming (or killing) the camelCase theory.

    Read-only. USAGE:
      .\Test-PASRetrieveShapes.ps1 `
        -TenantBaseUrl "https://2.my.centrify.net" `
        -AppId "hackerman" -ClientId "api@andrew.com" -ClientSecret "..." -Scope "all"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$TenantBaseUrl,
    [Parameter(Mandatory)] [string]$AppId,
    [Parameter(Mandatory)] [string]$ClientId,
    [Parameter(Mandatory)] [string]$ClientSecret,
    [string]$Scope = "all"
)
$ErrorActionPreference = "Stop"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 -bor [Net.SecurityProtocolType]::Tls13
$TenantBaseUrl = $TenantBaseUrl.TrimEnd('/')
$nh = @{ "X-CENTRIFY-NATIVE-CLIENT" = "true" }

Write-Host "`n##### Auth #####" -ForegroundColor Magenta
$tok = Invoke-RestMethod -Uri "$TenantBaseUrl/oauth2/token/$AppId" -Method POST `
    -Body @{ grant_type="client_credentials"; client_id=$ClientId; client_secret=$ClientSecret; scope=$Scope } `
    -ContentType "application/x-www-form-urlencoded" -Headers $nh
$auth = @{ Authorization = "Bearer $($tok.access_token)"; "X-CENTRIFY-NATIVE-CLIENT" = "true" }
Write-Host "OK" -ForegroundColor Green

Write-Host "`n##### Get one Text secret ID #####" -ForegroundColor Magenta
$q = Invoke-RestMethod -Uri "$TenantBaseUrl/RedRock/Query" -Method POST -Headers $auth -ContentType "application/json" `
    -Body (@{ Script="SELECT ID, SecretName FROM DataVault WHERE Type='Text'"; Args=@{ PageNumber=1; PageSize=1; Limit=1; Caching=-1 } } | ConvertTo-Json)
$id = $q.Result.Results[0].Row.ID
Write-Host "Using secret ID: $id" -ForegroundColor Cyan

$endpoint = "$TenantBaseUrl/ServerManage/RetrieveDataVaultItemContents"

function Try-Body($label, $json) {
    Write-Host "`n--- $label ---" -ForegroundColor Yellow
    Write-Host "BODY: $json"
    try {
        $r = Invoke-RestMethod -Uri $endpoint -Method POST -Headers $auth -Body $json -ContentType "application/json"
        $ok = $r.success
        $hasText = $null -ne $r.Result -and ($r.Result.PSObject.Properties.Name -contains 'SecretText')
        Write-Host "  RESULT: success=$ok  hasSecretText=$hasText  Message=$($r.Message)" -ForegroundColor Green
    } catch {
        Write-Host "  HTTP ERROR: $($_.ErrorDetails.Message)" -ForegroundColor Red
    }
}

Write-Host "`n##### Trying different body shapes #####" -ForegroundColor Magenta
# 1. PascalCase ID (what the app SHOULD now send)
Try-Body "1. { ID } PascalCase"      ("{`"ID`":`"$id`"}")
# 2. lowercase id (what camelCase serialization produced)
Try-Body "2. { id } lowercase"       ("{`"id`":`"$id`"}")
# 3. Name variant some PAS versions use
Try-Body "3. { Name }"               ("{`"Name`":`"$id`"}")
# 4. RRFormat sometimes required
Try-Body "4. { ID, RRFormat=true }"  ("{`"ID`":`"$id`",`"RRFormat`":true}")
# 5. ID as a query-ish field 'secretID' (file endpoint uses this)
Try-Body "5. { secretID }"           ("{`"secretID`":`"$id`"}")

Write-Host "`n##### DONE #####" -ForegroundColor Magenta
Write-Host "Whichever shape returns success=True hasSecretText=True is the one the app must send."
