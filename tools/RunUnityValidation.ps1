param([string]$UnityExe = 'E:\unity\Unity\Hub\Editor\2022.3.22f1\Editor\Unity.exe')
$ErrorActionPreference = 'Stop'
$taskWorkspace = Split-Path -Parent $PSScriptRoot
$taskPackage = Join-Path $taskWorkspace 'Packages\com.codex.stylized-mesh'
$taskAssets = Join-Path $taskWorkspace 'ValidationProject\Assets\StylizedMesh'
New-Item -ItemType Directory -Path $taskAssets -Force | Out-Null
foreach ($taskFolder in @('Editor', 'Shaders')) {
    Copy-Item -LiteralPath (Join-Path $taskPackage $taskFolder) -Destination $taskAssets -Recurse -Force
}
Copy-Item -LiteralPath (Join-Path $taskWorkspace 'README.md') -Destination (Join-Path $taskAssets 'README.md') -Force
$taskLog = Join-Path $taskWorkspace 'artifacts\unity-validation.log'
$taskProject = Join-Path $taskWorkspace 'ValidationProject'
$taskArguments = @('-batchmode', '-nographics', '-projectPath', ('"' + $taskProject + '"'), '-executeMethod', 'ValidationRunner.Run', '-logFile', ('"' + $taskLog + '"'))
$taskProcess = Start-Process -FilePath $UnityExe -ArgumentList $taskArguments -WindowStyle Hidden -PassThru
$taskProcess.WaitForExit()
if ($taskProcess.ExitCode -ne 0) { throw ('Unity validation did not complete successfully. See ' + $taskLog) }
Write-Output 'Unity integration validation finished. See artifacts/validation.txt.'
