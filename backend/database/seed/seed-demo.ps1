<#
.SYNOPSIS
    Seeds the running DocuPilot AI stack with the four demo documents.

.DESCRIPTION
    Uploads sample-contract.txt, sample-invoice.txt, sample-employee-record.txt,
    and sample-compliance-policy.txt to the running stack via a single multipart
    POST /api/documents/upload (form field "files"), exactly as the Document
    Upload page does. After upload the background Worker classifies, extracts
    metadata, chunks, and embeds each document automatically.

    Compatible with Windows PowerShell 5.1 and PowerShell 7+ (the multipart
    body is built by hand, so no PS7-only `-Form` parameter is required).

    This is a MANUAL, on-demand seeder -- it is intentionally NOT an automatic
    startup seeder, so a fresh demo stays predictable (you can show the
    empty -> populated transition live, and there are no duplicate rows on each
    `docker compose up`). Re-running it uploads the four docs AGAIN (new ids).

.PARAMETER BaseUrl
    The stack base URL. Defaults to the web/nginx origin http://localhost:4210
    (which reverse-proxies /api to the API). Use http://localhost:5010 to hit
    the API directly. Override WEB_PORT/API_PORT in .env if you remapped ports.

.EXAMPLE
    .\seed-demo.ps1
    # uploads all four docs through http://localhost:4210/api/documents/upload

.EXAMPLE
    .\seed-demo.ps1 -BaseUrl http://localhost:5010
    # uploads straight to the API (bypassing nginx)
#>
[CmdletBinding()]
param(
    [string]$BaseUrl = "http://localhost:4210"
)

$ErrorActionPreference = "Stop"

# The four seed docs live alongside this script.
$seedDir = $PSScriptRoot
$files = @(
    "sample-contract.txt",
    "sample-invoice.txt",
    "sample-employee-record.txt",
    "sample-compliance-policy.txt"
) | ForEach-Object { Join-Path $seedDir $_ }

foreach ($f in $files) {
    if (-not (Test-Path $f)) {
        Write-Error "Seed file not found: $f"
        exit 1
    }
}

$uploadUrl = "$BaseUrl/api/documents/upload"
Write-Host "Uploading $($files.Count) seed documents to $uploadUrl ..." -ForegroundColor Cyan

# Build one multipart/form-data body by hand so this works on BOTH Windows
# PowerShell 5.1 (the host default — no `Invoke-RestMethod -Form`) and
# PowerShell 7+. All four files go under the "files" field (the frozen
# contract — DocumentsController binds [FromForm(Name="files")]).
$boundary = "----DocuPilotSeed$([Guid]::NewGuid().ToString('N'))"
$LF = "`r`n"
$enc = [System.Text.Encoding]::GetEncoding('iso-8859-1')   # 1:1 byte<->char
$sb  = New-Object System.Text.StringBuilder
foreach ($f in $files) {
    $name  = [System.IO.Path]::GetFileName($f)
    $bytes = [System.IO.File]::ReadAllBytes($f)
    $content = $enc.GetString($bytes)
    [void]$sb.Append("--$boundary$LF")
    [void]$sb.Append("Content-Disposition: form-data; name=`"files`"; filename=`"$name`"$LF")
    [void]$sb.Append("Content-Type: text/plain$LF$LF")
    [void]$sb.Append($content)
    [void]$sb.Append($LF)
}
[void]$sb.Append("--$boundary--$LF")
$bodyBytes = $enc.GetBytes($sb.ToString())

try {
    $response = Invoke-RestMethod -Uri $uploadUrl -Method Post `
        -ContentType "multipart/form-data; boundary=$boundary" `
        -Body $bodyBytes
} catch {
    Write-Error "Upload failed against $uploadUrl. Is the stack up? ($($_.Exception.Message))"
    Write-Host  "Tip: confirm the stack with 'docker compose ps' and that http://localhost:4210 loads." -ForegroundColor Yellow
    exit 1
}

Write-Host "Upload accepted. Server response:" -ForegroundColor Green
$response | ConvertTo-Json -Depth 6

Write-Host ""
Write-Host "The Worker will now classify -> extract metadata -> chunk -> embed each doc." -ForegroundColor Cyan
Write-Host "Watch them progress to ReadyForSearch in the Library (/library) or Dashboard (/dashboard)." -ForegroundColor Cyan
Write-Host "Note: LLM inference is CPU-only, so processing takes seconds to tens of seconds per doc." -ForegroundColor DarkGray
