param(
    [switch]$SelfContained,
    [string]$FfmpegPath
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $projectRoot
try {
    $publishArguments = @('publish', 'src/YiLocal.Windows', '-c', 'Release', '-r', 'win-x64', '-o', 'dist/YI-Local', '--nologo')
    if ($SelfContained) { $publishArguments += '--self-contained' } else { $publishArguments += '--no-self-contained' }
    & dotnet @publishArguments
    if ($LASTEXITCODE -ne 0) { throw 'Windows build failed.' }
    if ($FfmpegPath) {
        $ffmpegSource = (Resolve-Path -LiteralPath $FfmpegPath).Path
        Copy-Item -LiteralPath $ffmpegSource -Destination 'dist/YI-Local/ffmpeg.exe'
    }
    Copy-Item -LiteralPath 'LICENSE','THIRD-PARTY.md' -Destination 'dist/YI-Local'
    Write-Output 'Built dist/YI-Local/YiLocal.Windows.exe'
    Write-Output 'For preview and 4K export, choose an FFmpeg executable in the Storage tab.'
} finally { Pop-Location }
