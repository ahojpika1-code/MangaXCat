$ErrorActionPreference = 'Stop'

$projectPath = Split-Path -Parent $MyInvocation.MyCommand.Path
$sourcePath = Join-Path $projectPath 'MangaXcat.cs'
$outputPath = Join-Path $projectPath 'MangaXcat.exe'
$manifestPath = Join-Path $projectPath 'packages.config'
$extensionManifestPath = Join-Path $projectPath 'extensions.config'
$iconPath = Join-Path $projectPath 'MangaXcat.ico'

if (-not (Test-Path -LiteralPath $sourcePath)) {
    throw "Application source not found: $sourcePath"
}
if (-not (Test-Path -LiteralPath $manifestPath)) {
    throw "Package manifest not found: $manifestPath"
}
if (-not (Test-Path -LiteralPath $extensionManifestPath)) {
    throw "Extension manifest not found: $extensionManifestPath"
}
if (-not (Test-Path -LiteralPath $iconPath)) {
    & (Join-Path $projectPath 'create-icon.ps1')
}

$frameworkPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$manifest = [xml](Get-Content -Raw -LiteralPath $manifestPath)
$webViewPackage = $manifest.packages.package | Where-Object { $_.id -eq 'Microsoft.Web.WebView2' } | Select-Object -First 1
if (-not $webViewPackage) {
    throw 'Microsoft.Web.WebView2 is missing from packages.config'
}

$packageVersion = $webViewPackage.version
$packagePath = Join-Path $projectPath (Join-Path 'packages' "Microsoft.Web.WebView2.$packageVersion")
$packageArchive = "$packagePath.nupkg"
if (-not (Test-Path -LiteralPath (Join-Path $packagePath 'lib\net462\Microsoft.Web.WebView2.Wpf.dll'))) {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $packagePath) | Out-Null
    $packageUrl = "https://api.nuget.org/v3-flatcontainer/microsoft.web.webview2/$packageVersion/microsoft.web.webview2.$packageVersion.nupkg"
    & curl.exe --fail --silent --show-error --max-time 60 --output $packageArchive --url $packageUrl
    if ($LASTEXITCODE -ne 0) {
        throw "Could not download Microsoft.Web.WebView2 $packageVersion"
    }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    New-Item -ItemType Directory -Force -Path $packagePath | Out-Null
    [System.IO.Compression.ZipFile]::ExtractToDirectory($packageArchive, $packagePath)
    Remove-Item -LiteralPath $packageArchive -Force
}

$extensions = [xml](Get-Content -Raw -LiteralPath $extensionManifestPath)
$adBlocker = $extensions.extensions.extension | Where-Object { $_.id -eq 'uBlockOriginLite' } | Select-Object -First 1
if (-not $adBlocker) {
    throw 'uBlock Origin Lite is missing from extensions.config'
}

$adBlockerPath = Join-Path $projectPath 'extensions\uBOLite'
$adBlockerZip = Join-Path $projectPath (Join-Path 'packages' "uBOLite_$($adBlocker.version).chromium.zip")
if (-not (Test-Path -LiteralPath (Join-Path $adBlockerPath 'manifest.json'))) {
    if (Test-Path -LiteralPath $adBlockerPath) {
        throw "The uBlock Origin Lite folder is incomplete: $adBlockerPath"
    }
    if (-not (Test-Path -LiteralPath $adBlockerZip)) {
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $adBlockerZip) | Out-Null
        & curl.exe --fail --silent --show-error --location --max-time 120 --output $adBlockerZip --url $adBlocker.url
        if ($LASTEXITCODE -ne 0) {
            throw "Could not download uBlock Origin Lite $($adBlocker.version)"
        }
    }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    New-Item -ItemType Directory -Force -Path $adBlockerPath | Out-Null
    [System.IO.Compression.ZipFile]::ExtractToDirectory($adBlockerZip, $adBlockerPath)
}

$webViewLibraries = Join-Path $packagePath 'lib\net462'
$webViewWpf = Join-Path $webViewLibraries 'Microsoft.Web.WebView2.Wpf.dll'
$webViewCore = Join-Path $webViewLibraries 'Microsoft.Web.WebView2.Core.dll'
$webViewLoader = Join-Path $packagePath 'runtimes\win-x64\native\WebView2Loader.dll'
foreach ($requiredFile in @($webViewWpf, $webViewCore, $webViewLoader)) {
    if (-not (Test-Path -LiteralPath $requiredFile)) {
        throw "Required WebView2 file not found: $requiredFile"
    }
}

$references = @(
    'System.dll',
    'System.Core.dll',
    'System.Security.dll',
    'System.Web.Extensions.dll',
    'System.Xaml.dll',
    'WindowsBase.dll',
    'PresentationCore.dll',
    'PresentationFramework.dll',
    $webViewCore,
    $webViewWpf
)

$compiler = New-Object Microsoft.CSharp.CSharpCodeProvider
$parameters = New-Object System.CodeDom.Compiler.CompilerParameters
$parameters.GenerateExecutable = $true
$parameters.OutputAssembly = $outputPath
$parameters.CompilerOptions = "/target:winexe /optimize+ /platform:x64 /win32icon:`"$iconPath`""
$parameters.TreatWarningsAsErrors = $false

foreach ($reference in $references) {
    if (Test-Path -LiteralPath $reference) {
        $resolved = $reference
    } else {
        $resolved = Join-Path $frameworkPath $reference
        if (-not (Test-Path -LiteralPath $resolved)) {
            $resolved = Join-Path (Join-Path $frameworkPath 'WPF') $reference
        }
    }
    if (-not (Test-Path -LiteralPath $resolved)) {
        throw "Required Windows desktop assembly not found: $reference"
    }
    [void]$parameters.ReferencedAssemblies.Add($resolved)
}

$result = $compiler.CompileAssemblyFromFile($parameters, $sourcePath)
if ($result.Errors.HasErrors) {
    $messages = $result.Errors | ForEach-Object { $_.ToString() }
    throw ("MangaXcat build failed:`n" + ($messages -join "`n"))
}

Copy-Item -LiteralPath $webViewCore -Destination $projectPath -Force
Copy-Item -LiteralPath $webViewWpf -Destination $projectPath -Force
Copy-Item -LiteralPath $webViewLoader -Destination $projectPath -Force
Write-Output "Built $outputPath"
