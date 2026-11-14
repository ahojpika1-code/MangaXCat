$ErrorActionPreference = 'Stop'

$projectPath = Split-Path -Parent $MyInvocation.MyCommand.Path
$executablePath = Join-Path $projectPath 'MangaXcat.exe'
$inputs = @(
    (Join-Path $projectPath 'MangaXcat.cs'),
    (Join-Path $projectPath 'build.ps1'),
    (Join-Path $projectPath 'packages.config'),
    (Join-Path $projectPath 'extensions.config'),
    (Join-Path $projectPath 'create-icon.ps1'),
    (Join-Path $projectPath 'MangaXcat.ico')
)

$needsBuild = -not (Test-Path -LiteralPath $executablePath)
if (-not $needsBuild) {
    $executableTime = (Get-Item -LiteralPath $executablePath).LastWriteTimeUtc
    $needsBuild = [bool]($inputs | Where-Object {
        -not (Test-Path -LiteralPath $_) -or (Get-Item -LiteralPath $_).LastWriteTimeUtc -gt $executableTime
    })
}

if ($needsBuild) {
    & (Join-Path $projectPath 'build.ps1')
}

& (Join-Path $projectPath 'CreateShortcut.ps1')
Start-Process -FilePath $executablePath -WorkingDirectory $projectPath
