param(
    [string]$DotnetPath = 'dotnet',
    [string]$OutputRoot,
    [switch]$ValidateUI
)
$ErrorActionPreference = 'Stop'
function Invoke-Dotnet {
    param([string[]]$Arguments)
    # Windows PowerShell treats redirected native stderr as ErrorRecords.
    # Warnings are allowed; the native exit code determines success.
    $ErrorActionPreference = 'Continue'
    & $DotnetPath @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet falhou (codigo $LASTEXITCODE)." }
}
$projectRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
if (-not $OutputRoot) { $OutputRoot = Join-Path $projectRoot 'artifacts' }
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
# Every build gets its own directory; never delete or overwrite an existing release.
$buildId = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$buildDirectory = Join-Path $OutputRoot ('build-' + $buildId)
$publishDirectory = Join-Path $buildDirectory 'Publish'
$packageDirectory = Join-Path $buildDirectory 'Windows'
New-Item $publishDirectory, $packageDirectory -ItemType Directory -Force | Out-Null
Push-Location $projectRoot
try {
    Invoke-Dotnet -Arguments @('run', '--project', './tests/NightShift.Tests/NightShift.Tests.csproj', '-c', 'Release', '-p:BuildInParallel=false', '-p:UseSharedCompilation=false')
    if ($ValidateUI) {
        Invoke-Dotnet -Arguments @('run', '--project', './tests/NightShift.Windows.Tests/NightShift.Windows.Tests.csproj', '-c', 'Release', '-p:BuildInParallel=false', '-p:UseSharedCompilation=false', '--', (Join-Path $buildDirectory 'Captures'))
    }
    Invoke-Dotnet -Arguments @('publish', './src/NightShift.App/NightShift.App.csproj', '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-m:1', '-p:UseSharedCompilation=false', '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:PublishTrimmed=false', '-p:DebugType=None', '-p:DebugSymbols=false', '-p:CopyOutputSymbolsToPublishDirectory=false', '-o', $publishDirectory)
    $publishedFiles = @(Get-ChildItem $publishDirectory -File -Recurse)
    if ($publishedFiles.Count -ne 1 -or $publishedFiles[0].Name -ne 'NightShift.exe') {
        throw 'A publicacao nao gerou somente NightShift.exe. Confira as dependencias.'
    }
    Copy-Item -LiteralPath (Join-Path $publishDirectory 'NightShift.exe') -Destination $packageDirectory
    Copy-Item ./docs/LEIA-ME.txt, ./docs/nightshift.portable $packageDirectory
    $documentationDirectory = Join-Path $packageDirectory 'Documentacao'
    New-Item $documentationDirectory -ItemType Directory | Out-Null
    Copy-Item ./README.md (Join-Path $documentationDirectory 'Manual.md')
    # Preserve repository-relative links in the packaged documentation.
    Copy-Item ./README.md, ./README.en.md, ./LICENSE, ./CONTRIBUTING.md, ./SECURITY.md, ./CHANGELOG.md $documentationDirectory
    Copy-Item ./docs (Join-Path $documentationDirectory 'docs') -Recurse
    Copy-Item ./NIGHT_SHIFT_PROTOCOL.md, ./docs/VALIDATION.md, ./docs/COMPATIBILITY.md, ./docs/CONTRIBUTING-AGENTS.md $documentationDirectory
    Copy-Item ./docs/licenses (Join-Path $documentationDirectory 'Licencas') -Recurse
    $sourceDirectory = Join-Path $packageDirectory 'Codigo-fonte'
    New-Item $sourceDirectory -ItemType Directory | Out-Null
    Copy-Item ./.gitignore, ./.gitattributes, ./.editorconfig, ./global.json, ./Directory.Build.props, ./README.md, ./README.en.md, ./LICENSE, ./CONTRIBUTING.md, ./SECURITY.md, ./CHANGELOG.md, ./NIGHT_SHIFT_PROTOCOL.md $sourceDirectory
    Copy-Item ./.github (Join-Path $sourceDirectory '.github') -Recurse -Force
    Get-ChildItem ./src, ./tests, ./scripts, ./docs -Recurse -File |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj|Dados|captures)[\\/]' -and $_.Name -notlike '*_wpftmp.csproj' } |
        ForEach-Object {
            $relativePath = $_.FullName.Substring($projectRoot.Length + 1)
            $destination = Join-Path $sourceDirectory $relativePath
            New-Item (Split-Path -Parent $destination) -ItemType Directory -Force | Out-Null
            Copy-Item -LiteralPath $_.FullName -Destination $destination
        }
    $zipPath = Join-Path $buildDirectory 'Night-Shift-v0.7.1-Windows-x64.zip'
    # -Path '*' skips dotfiles in Windows PowerShell; include .gitignore explicitly.
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($packageDirectory, $zipPath, [IO.Compression.CompressionLevel]::Optimal, $false)
    Write-Host "Aplicativo: $(Join-Path $packageDirectory 'NightShift.exe')"
    Write-Host "Pacote: $zipPath"
    Write-Output $buildDirectory
} finally { Pop-Location }
