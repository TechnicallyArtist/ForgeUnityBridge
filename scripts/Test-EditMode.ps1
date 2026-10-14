param(
    [ValidateSet('6000.0.40f1', '6000.4.6f1')][string]$Version = '6000.0.40f1',
    [string]$ProjectPath,
    [string]$ResultsPath,
    [switch]$CreateProbe
)
$ErrorActionPreference = 'Stop'
$repositoryPath = Split-Path $PSScriptRoot -Parent
if (!$ProjectPath) {
    $ProjectPath = if ($Version -eq '6000.0.40f1') { $repositoryPath } else { 'E:\b\fu\probe-6000.4' }
}
$ProjectPath = [IO.Path]::GetFullPath($ProjectPath)
if ($Version -ne '6000.0.40f1' -and $ProjectPath.TrimEnd('\') -eq $repositoryPath.TrimEnd('\')) {
    throw 'Never upgrade the development host. Use a separate probe project.'
}
if (!$ResultsPath) { $ResultsPath = Join-Path $repositoryPath 'TestResults' }
if ($CreateProbe) {
    if (Test-Path (Join-Path $ProjectPath 'Packages/manifest.json')) { throw "Probe already exists: $ProjectPath" }
    New-Item -ItemType Directory -Force (Join-Path $ProjectPath 'Assets'), (Join-Path $ProjectPath 'Packages'), (Join-Path $ProjectPath 'ProjectSettings') | Out-Null
    $manifest = @{
        dependencies = @{
            'com.technicallyartist.forge-bridge' = 'file:' + (Join-Path $repositoryPath 'Packages/com.technicallyartist.forge-bridge').Replace('\', '/')
            'com.unity.test-framework' = '1.4.6'
            'com.unity.render-pipelines.universal' = if ($Version -eq '6000.0.40f1') { '17.0.3' } else { '17.4.0' }
            'com.unity.render-pipelines.high-definition' = if ($Version -eq '6000.0.40f1') { '17.0.3' } else { '17.4.0' }
            'com.unity.modules.physics' = '1.0.0'
        }
        testables = @('com.technicallyartist.forge-bridge')
    }
    [IO.File]::WriteAllText((Join-Path $ProjectPath 'Packages/manifest.json'), ($manifest | ConvertTo-Json -Depth 5) + "`n")
    [IO.File]::WriteAllText((Join-Path $ProjectPath 'ProjectSettings/ProjectVersion.txt'), "m_EditorVersion: $Version`n")
}
$editorPath = "E:\Unity\$Version\Editor\Unity.exe"
if (!(Test-Path $editorPath)) { throw "Unity is not installed at $editorPath" }
New-Item -ItemType Directory -Force $ResultsPath | Out-Null
$xmlPath = Join-Path $ResultsPath "editmode-$Version.xml"
$logPath = Join-Path $ResultsPath "editmode-$Version.log"
if (Test-Path -LiteralPath $xmlPath) { Remove-Item -LiteralPath $xmlPath -Force }
$arguments = "-batchmode -nographics -projectPath `"$ProjectPath`" -runTests -testPlatform EditMode -testFilter ForgeBridge -testResults `"$xmlPath`" -logFile `"$logPath`""
$process = Start-Process -FilePath $editorPath -ArgumentList $arguments -WindowStyle Hidden -PassThru
$process.WaitForExit()
if ($process.ExitCode -ne 0) { throw "Unity tests exited $($process.ExitCode). See $logPath" }
if (!(Test-Path $xmlPath)) { throw "Unity produced no test report. See $logPath" }
[xml]$report = Get-Content -Raw $xmlPath
if ([int]$report.'test-run'.total -eq 0 -or [int]$report.'test-run'.failed -ne 0) { throw "Invalid or failed test report: $xmlPath" }
Write-Output "$Version : $($report.'test-run'.passed) passed, $($report.'test-run'.skipped) skipped. $xmlPath"
