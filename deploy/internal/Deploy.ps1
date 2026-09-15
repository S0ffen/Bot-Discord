[CmdletBinding()]
param(
    [string] $Version,
    [switch] $PackageOnly
)

$ErrorActionPreference = 'Stop'
$VpsHost = '51.75.73.137'
$VpsUser = 'ubuntu'
$deployDirectory = Split-Path -Parent $PSScriptRoot
$projectDirectory = Split-Path -Parent $deployDirectory
$versionsDirectory = Join-Path $projectDirectory 'versions'

function Invoke-NativeCommand {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Command,

        [Parameter(ValueFromRemainingArguments = $true)]
        [string[]] $Arguments
    )

    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Polecenie $Command zakonczylo sie kodem $LASTEXITCODE."
    }
}

$requiredCommands = @('dotnet')
if (-not $PackageOnly) {
    $requiredCommands += 'scp'
}

foreach ($requiredCommand in $requiredCommands) {
    if ($null -eq (Get-Command $requiredCommand -ErrorAction SilentlyContinue)) {
        throw "Brak wymaganego programu: $requiredCommand"
    }
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = Read-Host 'Podaj wersje paczki, np. 1.0.0'
}

$Version = $Version.Trim()
if ($Version -notmatch '^[0-9A-Za-z][0-9A-Za-z._-]{0,49}$') {
    throw 'Wersja moze zawierac tylko litery, cyfry, kropki, myslniki i podkreslenia (maks. 50 znakow).'
}

$archiveName = "discord-activity-bot-$Version.zip"
$archivePath = Join-Path $versionsDirectory $archiveName
$remotePath = "/home/$VpsUser/$archiveName"
$stagingDirectory = Join-Path ([IO.Path]::GetTempPath()) ("discord-activity-bot-" + [Guid]::NewGuid().ToString('N'))

New-Item -ItemType Directory -Path $versionsDirectory -Force | Out-Null

Write-Host "`n[1/3] Testy wersji $Version..."
Push-Location $projectDirectory
try {
    Invoke-NativeCommand dotnet test DiscordActivityBot.sln --configuration Release
}
finally {
    Pop-Location
}

Write-Host "`n[2/3] Tworzenie paczki $archiveName..."
New-Item -ItemType Directory -Path $stagingDirectory -Force | Out-Null
try {
    $sourceFiles = Get-ChildItem -LiteralPath $projectDirectory -Recurse -File -Force
    foreach ($sourceFile in $sourceFiles) {
        $relativePath = $sourceFile.FullName.Substring($projectDirectory.Length).TrimStart([char[]]@('\', '/'))
        $normalizedPath = $relativePath.Replace('\', '/')
        $fileName = $sourceFile.Name

        $isExcluded =
            $normalizedPath -match '^(\.git|\.vs|\.idea|\.dotnet-home|\.nuget-packages|\.appdata|versions|backups|data)(/|$)' -or
            $normalizedPath -match '(^|/)(bin|obj)(/|$)' -or
            $fileName -ieq 'appsettings.Local.json' -or
            $fileName -ieq 'initial-data.tar.gz' -or
            $fileName -match '\.db(-shm|-wal)?$' -or
            $fileName -ieq '.env' -or
            ($fileName -like '.env.*' -and $fileName -ine '.env.example')

        if ($isExcluded) {
            continue
        }

        $destinationPath = Join-Path $stagingDirectory $relativePath
        $destinationDirectory = Split-Path -Parent $destinationPath
        New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null
        Copy-Item -LiteralPath $sourceFile.FullName -Destination $destinationPath -Force
    }

    if (Test-Path -LiteralPath $archivePath) {
        Remove-Item -LiteralPath $archivePath -Force
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory(
        $stagingDirectory,
        $archivePath,
        [IO.Compression.CompressionLevel]::Optimal,
        $false
    )

    $zip = [IO.Compression.ZipFile]::OpenRead($archivePath)
    try {
        $entryNames = @($zip.Entries | ForEach-Object { $_.FullName })
        $forbiddenEntry = $entryNames | Where-Object {
            $_ -match '(^|/)appsettings\.Local\.json$' -or
            ($_ -match '(^|/)\.env($|\.)' -and $_ -notmatch '(^|/)\.env\.example$') -or
            $_ -match '(^|/)(bin|obj|backups|versions)(/|$)' -or
            $_ -cmatch '(^|/)data(/|$)' -or
            $_ -match '\.db(-shm|-wal)?$'
        } | Select-Object -First 1

        if ($null -ne $forbiddenEntry) {
            throw "Paczka zawiera niedozwolony plik: $forbiddenEntry"
        }

        if ($entryNames -notcontains '.env.example') {
            throw 'W paczce brakuje pliku .env.example.'
        }
    }
    finally {
        $zip.Dispose()
    }
}
finally {
    if (Test-Path -LiteralPath $stagingDirectory) {
        Remove-Item -LiteralPath $stagingDirectory -Recurse -Force
    }
}

$archive = Get-Item -LiteralPath $archivePath
$hash = Get-FileHash -LiteralPath $archivePath -Algorithm SHA256
Write-Host "Paczka: $($archive.FullName)"
Write-Host "Rozmiar: $([Math]::Round($archive.Length / 1MB, 2)) MB"
Write-Host "SHA256: $($hash.Hash)"

if ($PackageOnly) {
    Write-Host "`nTryb PackageOnly: paczka zostala sprawdzona bez wysylania na VPS."
    return
}

Write-Host "`n[3/3] Wysylanie paczki na ${VpsUser}@${VpsHost}:$remotePath..."
Invoke-NativeCommand scp $archivePath "${VpsUser}@${VpsHost}:$remotePath"

Write-Host "`nGotowe. Paczka znajduje sie na VPS-ie: $remotePath"
Write-Host 'Skrypt nie rozpakowuje paczki i nie uruchamia Dockera.'
