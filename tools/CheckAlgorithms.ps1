param(
    [string]$UnityEditor = 'E:\unity\Unity\Hub\Editor\2022.3.22f1\Editor',
    [string]$MonoRoot = 'D:\Mono'
)
$ErrorActionPreference = 'Stop'
$taskWorkspace = Split-Path -Parent $PSScriptRoot
$taskArtifacts = Join-Path $taskWorkspace 'artifacts'
$taskEngine = Join-Path $UnityEditor 'Data\Managed\UnityEngine'
$taskArguments = @('-target:exe', '-langversion:7.2', ('-r:' + (Join-Path $taskArtifacts 'StylizedMesh.CompileCheck.dll')),
    ('-r:' + (Join-Path $taskEngine 'UnityEngine.CoreModule.dll')), ('-r:' + (Join-Path $taskEngine 'UnityEngine.dll')),
    ('-r:' + (Join-Path $MonoRoot 'lib\mono\4.5\Facades\netstandard.dll')),
    ('-out:' + (Join-Path $taskArtifacts 'AlgorithmChecks.exe')), (Join-Path $PSScriptRoot 'AlgorithmChecks.cs'))
& (Join-Path $MonoRoot 'bin\mono.exe') (Join-Path $MonoRoot 'lib\mono\4.5\mcs.exe') @taskArguments
if ($LASTEXITCODE -ne 0) { throw 'Algorithm test compilation failed.' }
$taskOldMonoPath = $env:MONO_PATH
try {
    $env:MONO_PATH = $taskEngine + ';' + $taskArtifacts
    & (Join-Path $MonoRoot 'bin\mono.exe') (Join-Path $taskArtifacts 'AlgorithmChecks.exe') (Join-Path $taskArtifacts 'algorithm-checks.txt')
    if ($LASTEXITCODE -ne 0) { throw 'Algorithm checks failed.' }
} finally { $env:MONO_PATH = $taskOldMonoPath }
