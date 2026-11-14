$ErrorActionPreference = 'Stop'

$projectPath = Split-Path -Parent $MyInvocation.MyCommand.Path
$executablePath = Join-Path $projectPath 'MangaXcat.exe'
if (-not (Test-Path -LiteralPath $executablePath)) {
    throw "Application executable not found: $executablePath"
}

$desktopPath = [Environment]::GetFolderPath([Environment+SpecialFolder]::DesktopDirectory)
$shortcutPath = Join-Path $desktopPath 'MangaXcat.lnk'
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $executablePath
$shortcut.WorkingDirectory = $projectPath
$shortcut.Description = 'MangaXcat manga reader'
$shortcut.IconLocation = "$executablePath,0"
$shortcut.Save()

Write-Output "Created $shortcutPath"
