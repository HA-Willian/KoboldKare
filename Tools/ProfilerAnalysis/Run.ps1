param(
    [Parameter(Mandatory = $true)][string]$CaptureDirectory,
    [string]$OutputDirectory,
    [string]$UnityEditor = 'C:/Program Files/Unity/Hub/Editor/2021.3.45f2/Editor/Unity.exe'
)
$ErrorActionPreference = 'Stop'
$CaptureDirectory = (Resolve-Path -LiteralPath $CaptureDirectory).Path
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $CaptureDirectory ('Analysis-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$readerProject = Join-Path ([IO.Path]::GetTempPath()) ('KoboldProfilerReader-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path (Join-Path $readerProject 'Assets/Editor'), (Join-Path $readerProject 'ProjectSettings'), (Join-Path $readerProject 'Packages'), $OutputDirectory -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'ProfilerCaptureExport.cs') -Destination (Join-Path $readerProject 'Assets/Editor/ProfilerCaptureExport.cs')
"m_EditorVersion: 2021.3.45f2" | Set-Content -LiteralPath (Join-Path $readerProject 'ProjectSettings/ProjectVersion.txt') -Encoding ASCII
'{"dependencies":{}}' | Set-Content -LiteralPath (Join-Path $readerProject 'Packages/manifest.json') -Encoding ASCII
$exportLog = Join-Path $OutputDirectory 'export.log'
$readerArguments = '-batchmode -nographics -projectPath "' + $readerProject + '" -executeMethod ProfilerCaptureExport.Run -captureDirectory "' + $CaptureDirectory + '" -exportDirectory "' + $OutputDirectory + '" -logFile "' + $exportLog + '"'
$readerProcess = Start-Process -FilePath $UnityEditor -ArgumentList $readerArguments -WindowStyle Hidden -PassThru
$readerProcess.WaitForExit()
if ($readerProcess.ExitCode -ne 0) { throw "Profiler export failed; inspect $exportLog" }
Write-Output "Export complete: $OutputDirectory"
