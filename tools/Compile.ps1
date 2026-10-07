param(
    [string]$UnityEditor = 'E:\unity\Unity\Hub\Editor\2022.3.22f1\Editor',
    [string]$MonoRoot = 'D:\Mono'
)
$ErrorActionPreference = 'Stop'
$taskWorkspace = Split-Path -Parent $PSScriptRoot
$taskArtifacts = Join-Path $taskWorkspace 'artifacts'
New-Item -ItemType Directory -Path $taskArtifacts -Force | Out-Null
$taskManaged = Join-Path $UnityEditor 'Data\Managed'
$taskReferences = @(Get-ChildItem -LiteralPath (Join-Path $taskManaged 'UnityEngine') -Filter '*.dll' | Select-Object -ExpandProperty FullName)
$taskFacade = Join-Path $MonoRoot 'lib\mono\4.5\Facades\netstandard.dll'
if (Test-Path -LiteralPath $taskFacade) { $taskReferences += $taskFacade }
$taskSources = Get-ChildItem -LiteralPath (Join-Path $taskWorkspace 'Packages\com.codex.stylized-mesh\Editor') -Recurse -Filter '*.cs' | Select-Object -ExpandProperty FullName
$taskSources += Join-Path $taskWorkspace 'ValidationProject\Assets\Editor\ValidationRunner.cs'
$taskResponse = @('-target:library', '-langversion:7.2', '-define:UNITY_EDITOR,UNITY_2017_3_OR_NEWER,UNITY_2018_2_OR_NEWER,UNITY_2019_3_OR_NEWER,UNITY_2022_3_OR_NEWER')
$taskResponse += '-out:"' + (Join-Path $taskArtifacts 'StylizedMesh.CompileCheck.dll') + '"'
$taskResponse += $taskReferences | Select-Object -Unique | ForEach-Object { '-r:"' + $_ + '"' }
$taskResponse += $taskSources | ForEach-Object { '"' + $_ + '"' }
$taskResponsePath = Join-Path $taskArtifacts 'compile.rsp'
[IO.File]::WriteAllLines($taskResponsePath, $taskResponse, [Text.UTF8Encoding]::new($false))
& (Join-Path $MonoRoot 'bin\mono.exe') (Join-Path $MonoRoot 'lib\mono\4.5\mcs.exe') ('@' + $taskResponsePath) 2>&1 | Tee-Object -FilePath (Join-Path $taskArtifacts 'compile.log')
if ($LASTEXITCODE -ne 0) { throw 'C# compilation failed. See artifacts/compile.log.' }
'C# compilation passed against Unity 2022.3 assemblies (not an engine execution test).' | Tee-Object -FilePath (Join-Path $taskArtifacts 'compile.log') -Append
