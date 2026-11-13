param(
    [string]$UnityEditor = 'C:/Program Files/Unity/Hub/Editor/2021.3.45f2/Editor',
    [string]$Baseline = '8a07abe8c07df7cc06150f1872e741aa4949965d'
)
$ErrorActionPreference = 'Stop'
$monoRoot = Join-Path $UnityEditor 'Data/MonoBleedingEdge'
$roslyn = Join-Path $monoRoot 'lib/mono/msbuild/Current/bin/Roslyn'
$mono = Join-Path $monoRoot 'bin/mono.exe'
Push-Location (Join-Path $PSScriptRoot '../..')
try {
    & $mono (Join-Path $roslyn 'csi.exe') "/lib:$monoRoot/lib/mono/4.5" "/r:$roslyn/Microsoft.CodeAnalysis.dll" "/r:$roslyn/Microsoft.CodeAnalysis.CSharp.dll" "/r:$roslyn/System.Collections.Immutable.dll" (Join-Path $PSScriptRoot 'Regression.csx') $Baseline
    if ($LASTEXITCODE -ne 0) { throw 'Performance regression checks failed.' }
} finally {
    Pop-Location
}
