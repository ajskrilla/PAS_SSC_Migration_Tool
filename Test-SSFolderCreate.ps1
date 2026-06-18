<#
    Test-SSFolderCreate.ps1
    Standalone troubleshooting for the "POST /folders -> 403 API_AccessDenied" error.

    This talks DIRECTLY to Secret Server Cloud via the Delinea Platform token - no containers,
    no migration app involved. It runs several folder-create variations so we can see exactly
    which factor causes the 403 (root vs child, echo-stub vs partial body, inherit flags).

    USAGE (PowerShell 7 recommended):
      .\Test-SSFolderCreate.ps1 `
        -PlatformBaseUrl "https://dzntz.delinea.app" `
        -SecretServerBaseUrl "https://dzntz.secretservercloud.com" `
        -ClientId "api@dzntz" `
        -ClientSecret "..." `
        [-ParentFolderId <existing folder id to test child-create under>]

    Nothing is deleted. It only creates test folders named "zzz_test_*".
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$PlatformBaseUrl,
    [Parameter(Mandatory)] [string]$SecretServerBaseUrl,
    [Parameter(Mandatory)] [string]$ClientId,
    [Parameter(Mandatory)] [string]$ClientSecret,
    [int]$ParentFolderId = -1
)

$ErrorActionPreference = "Stop"
# Secret Server Cloud requires modern TLS.
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 -bor [Net.SecurityProtocolType]::Tls13

function Show($label, $obj) {
    Write-Host "`n===== $label =====" -ForegroundColor Cyan
    $obj | ConvertTo-Json -Depth 6 | Write-Host
}

# Helper: POST and capture the FULL error body (PS puts it in $_.ErrorDetails.Message).
function Invoke-SSPost($url, $headers, $bodyJson, $label) {
    Write-Host "`n--- $label ---" -ForegroundColor Yellow
    Write-Host "POST $url"
    Write-Host "BODY: $bodyJson"
    try {
        $r = Invoke-RestMethod -Uri $url -Method POST -Headers $headers -Body $bodyJson -ContentType "application/json"
        Write-Host "RESULT: SUCCESS (id=$($r.id))" -ForegroundColor Green
        return $r
    } catch {
        $status = $_.Exception.Response.StatusCode.value__
        $detail = $_.ErrorDetails.Message
        Write-Host "RESULT: FAILED ($status)" -ForegroundColor Red
        Write-Host "ERROR BODY: $detail" -ForegroundColor Red
        return $null
    }
}

# ---------- 1. Get a Platform token (same flow the app uses) ----------
Write-Host "`n##### STEP 1: Authenticate (Platform client-credentials) #####" -ForegroundColor Magenta
$tokenUrl = "$PlatformBaseUrl/identity/api/oauth2/token/xpmplatform"
$tokenBody = @{
    grant_type    = "client_credentials"
    scope         = "xpmheadless"
    client_id     = $ClientId
    client_secret = $ClientSecret
}
try {
    $tok = Invoke-RestMethod -Uri $tokenUrl -Method POST -Body $tokenBody -ContentType "application/x-www-form-urlencoded"
    Write-Host "Token acquired (expires_in=$($tok.expires_in))." -ForegroundColor Green
} catch {
    Write-Host "TOKEN REQUEST FAILED: $($_.ErrorDetails.Message)" -ForegroundColor Red
    throw
}
$headers = @{ Authorization = "Bearer $($tok.access_token)" }
$api = "$SecretServerBaseUrl/api/v1"

# ---------- 2. Confirm the token works for a READ ----------
Write-Host "`n##### STEP 2: Sanity read (GET /folders) #####" -ForegroundColor Magenta
try {
    $folders = Invoke-RestMethod -Uri "$api/folders?take=5" -Headers $headers
    Write-Host "Read OK. Total folders visible: $($folders.total)" -ForegroundColor Green
    if ($folders.records) {
        Write-Host "First few folders (id : name : parentId):"
        $folders.records | Select-Object -First 5 | ForEach-Object {
            Write-Host ("  {0} : {1} : {2}" -f $_.id, $_.folderName, $_.parentFolderId)
        }
    }
} catch {
    Write-Host "READ FAILED: $($_.ErrorDetails.Message)" -ForegroundColor Red
}

# ---------- 3. Inspect the folder stub ----------
Write-Host "`n##### STEP 3: GET /folders/stub #####" -ForegroundColor Magenta
$stub = $null
try {
    $stub = Invoke-RestMethod -Uri "$api/folders/stub" -Headers $headers -ContentType "application/json"
    Show "Folder stub returned by the API" $stub
} catch {
    Write-Host "STUB FAILED: $($_.ErrorDetails.Message)" -ForegroundColor Red
}

$ts = Get-Date -Format "HHmmss"

# ---------- 4. Variation A: echo the stub back (Delinea documented pattern) ----------
Write-Host "`n##### STEP 4: Create folder variations #####" -ForegroundColor Magenta
if ($stub) {
    $a = $stub.PSObject.Copy()
    $a.folderName        = "zzz_test_echo_$ts"
    $a.folderTypeId      = 1
    $a.parentFolderId    = $ParentFolderId
    $a.inheritPermissions = $true
    $a.inheritSecretPolicy = $true
    Invoke-SSPost "$api/folders" $headers ($a | ConvertTo-Json -Depth 6) `
        "Variation A: full stub echoed back, inherit=TRUE, parent=$ParentFolderId" | Out-Null
}

# ---------- 5. Variation B: stub echo but inherit = FALSE (matches the Delinea sample exactly) ----------
if ($stub) {
    $b = $stub.PSObject.Copy()
    $b.folderName        = "zzz_test_echo_noinherit_$ts"
    $b.folderTypeId      = 1
    $b.parentFolderId    = $ParentFolderId
    $b.inheritPermissions = $false
    $b.inheritSecretPolicy = $false
    Invoke-SSPost "$api/folders" $headers ($b | ConvertTo-Json -Depth 6) `
        "Variation B: full stub echoed, inherit=FALSE, parent=$ParentFolderId" | Out-Null
}

# ---------- 6. Variation C: minimal hand-built body (what the app used to send) ----------
$c = @{
    folderName        = "zzz_test_minimal_$ts"
    folderTypeId      = 1
    parentFolderId    = $ParentFolderId
    inheritPermissions = $true
    inheritSecretPolicy = $true
}
Invoke-SSPost "$api/folders" $headers ($c | ConvertTo-Json) `
    "Variation C: minimal partial body, parent=$ParentFolderId" | Out-Null

# ---------- 7. If we tested root (-1), also try creating UNDER the first visible folder ----------
if ($ParentFolderId -eq -1 -and $folders -and $folders.records) {
    $childParent = $folders.records[0].id
    Write-Host "`n##### STEP 5: Retry child-create under existing folder id $childParent #####" -ForegroundColor Magenta
    if ($stub) {
        $d = $stub.PSObject.Copy()
        $d.folderName        = "zzz_test_child_$ts"
        $d.folderTypeId      = 1
        $d.parentFolderId    = $childParent
        $d.inheritPermissions = $true
        $d.inheritSecretPolicy = $true
        Invoke-SSPost "$api/folders" $headers ($d | ConvertTo-Json -Depth 6) `
            "Variation D: stub echo under existing folder $childParent (NOT root)" | Out-Null
    }
}

Write-Host "`n##### DONE #####" -ForegroundColor Magenta
Write-Host "Interpretation:"
Write-Host "  - If A/B/C all FAIL at root (-1) but D SUCCEEDS under an existing folder:"
Write-Host "      => root-folder creation needs the 'Create Root Folders' permission;"
Write-Host "         fix by pre-creating the staging folder, or granting that permission."
Write-Host "  - If only A/B (stub echo) SUCCEED and C (minimal) FAILS:"
Write-Host "      => the body shape was the bug; the app fix is correct."
Write-Host "  - If everything FAILS including D:"
Write-Host "      => the API user genuinely lacks folder-add rights, or Webservices/role config."
Write-Host "  - Note any difference between inherit=TRUE (A) and inherit=FALSE (B)."
