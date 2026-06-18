<#
    Test-SSSecretCreate.ps1
    Probes the Secret Server SECRET-CREATE path to find the "requires an element of type
    'Object' but target has type 'Null'" error. The PAS read side is confirmed working; this
    isolates the WRITE side.

    Read-mostly: it looks up the Password template, dumps the secret stub, and (optionally)
    creates ONE test secret so we can see the exact request/response shapes.

    USAGE (PowerShell 7):
      .\Test-SSSecretCreate.ps1 `
        -PlatformBaseUrl "https://dzntz.delinea.app" `
        -SecretServerBaseUrl "https://dzntz.secretservercloud.com" `
        -ClientId "api@dzntz" `
        -ClientSecret "..." `
        [-FolderId <id to create the test secret in>] `
        [-DoCreate]      # actually create a test secret (otherwise just dumps shapes)
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$PlatformBaseUrl,
    [Parameter(Mandatory)] [string]$SecretServerBaseUrl,
    [Parameter(Mandatory)] [string]$ClientId,
    [Parameter(Mandatory)] [string]$ClientSecret,
    [int]$FolderId = -1,
    [switch]$DoCreate
)

$ErrorActionPreference = "Stop"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 -bor [Net.SecurityProtocolType]::Tls13
$PlatformBaseUrl = $PlatformBaseUrl.TrimEnd('/')
$SecretServerBaseUrl = $SecretServerBaseUrl.TrimEnd('/')

function Hdr($t) { Write-Host "`n##### $t #####" -ForegroundColor Magenta }
function Dump($label, $obj) {
    Write-Host "`n--- $label ---" -ForegroundColor Yellow
    $obj | ConvertTo-Json -Depth 10 | Write-Host
}

# ---------- 1. Auth ----------
Hdr "STEP 1: Platform token"
$tok = Invoke-RestMethod -Uri "$PlatformBaseUrl/identity/api/oauth2/token/xpmplatform" -Method POST `
    -Body @{ grant_type="client_credentials"; scope="xpmheadless"; client_id=$ClientId; client_secret=$ClientSecret } `
    -ContentType "application/x-www-form-urlencoded"
Write-Host "Token acquired." -ForegroundColor Green
$h = @{ Authorization = "Bearer $($tok.access_token)" }
$api = "$SecretServerBaseUrl/api/v1"

# ---------- 2. Find the Password template ----------
Hdr "STEP 2: GET /secret-templates?filter.searchText=Password"
$tpl = Invoke-RestMethod -Uri "$api/secret-templates?filter.searchText=Password" -Headers $h
Write-Host "Records returned: $($tpl.records.Count)"
$tpl.records | ForEach-Object { Write-Host ("  id={0}  name={1}" -f $_.id, $_.name) }
$pw = $tpl.records | Where-Object { $_.name -eq 'Password' } | Select-Object -First 1
if (-not $pw) { $pw = $tpl.records | Select-Object -First 1 }
if (-not $pw) { Write-Host "No template found!" -ForegroundColor Red; return }
$templateId = $pw.id
Write-Host "Using templateId=$templateId ($($pw.name))" -ForegroundColor Cyan

# ---------- 3. Dump the secret stub for that template (THE KEY PART) ----------
# Secret Server Cloud requires folderId on the stub call. Use the provided -FolderId,
# or fall back to the first folder we can see.
$stubFolderId = $FolderId
if ($stubFolderId -le 0) {
    $someFolder = Invoke-RestMethod -Uri "$api/folders?take=1" -Headers $h
    if ($someFolder.records -and $someFolder.records.Count -gt 0) {
        $stubFolderId = $someFolder.records[0].id
        Write-Host "No -FolderId given; using first visible folder id=$stubFolderId for the stub." -ForegroundColor DarkGray
    }
}
Hdr "STEP 3: GET /secrets/stub?filter.secrettemplateid=$templateId&filter.folderId=$stubFolderId"
$stub = Invoke-RestMethod -Uri "$api/secrets/stub?filter.secrettemplateid=$templateId&filter.folderId=$stubFolderId" -Headers $h
Dump "FULL SECRET STUB" $stub
Write-Host "`n--- top-level fields on stub ---" -ForegroundColor Yellow
Write-Host ($stub.PSObject.Properties.Name -join ", ")
Write-Host "`n--- items[] shape (first item) ---" -ForegroundColor Yellow
if ($stub.items -and $stub.items.Count -gt 0) {
    Dump "stub.items[0]" $stub.items[0]
    Write-Host "All item slugs / fieldIds:"
    $stub.items | ForEach-Object {
        Write-Host ("  slug={0}  fieldId={1}  isFile={2}  isPassword={3}" -f $_.slug, $_.fieldId, $_.isFile, $_.isPassword)
    }
} else {
    Write-Host "stub.items is EMPTY or NULL  <-- this would break a parser that assumes items exist" -ForegroundColor Red
}

# ---------- 4. Optionally create a test secret ----------
if ($DoCreate) {
    Hdr "STEP 4: POST /secrets (create a test secret)"
    # Echo the stub, fill name/folder and the password field by slug.
    $body = $stub.PSObject.Copy()
    $body.name = "zzz_test_secret_$(Get-Date -Format HHmmss)"
    $body.folderId = $stubFolderId
    $body.secretTemplateId = $templateId
    if ($body.PSObject.Properties.Name -contains 'siteId') { $body.siteId = 1 }
    foreach ($it in $body.items) {
        if ($it.slug -eq 'password' -or $it.isPassword) { $it.itemValue = "TestValue123!" }
        elseif ($it.slug -eq 'notes') { $it.itemValue = "migration probe" }
    }
    $json = $body | ConvertTo-Json -Depth 10
    Write-Host "REQUEST BODY:" -ForegroundColor Yellow
    Write-Host $json
    try {
        $created = Invoke-RestMethod -Uri "$api/secrets" -Method POST -Headers $h -Body $json -ContentType "application/json"
        Write-Host "`nCREATE SUCCESS (id=$($created.id))" -ForegroundColor Green
        Write-Host "Created secret named $($body.name) in folder $FolderId. Delete it from the UI when done."
    } catch {
        Write-Host "`nCREATE FAILED ($($_.Exception.Response.StatusCode.value__))" -ForegroundColor Red
        Write-Host "BODY: $($_.ErrorDetails.Message)" -ForegroundColor Red
    }
} else {
    Write-Host "`n(Skipping create. Re-run with -DoCreate -FolderId <id> to test an actual create.)" -ForegroundColor DarkGray
}

Hdr "DONE"
Write-Host "Key questions:"
Write-Host "  - Does the stub have an 'items' array? Is each item an object with slug/fieldId?"
Write-Host "  - Is there a 'password' slug, or is it named differently on this template?"
Write-Host "  - If -DoCreate was used: did it succeed, and what did a failure body say?"
