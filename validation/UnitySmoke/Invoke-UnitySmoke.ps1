[CmdletBinding()]
param(
    [string]$UnityEditorPath = 'C:\Program Files\Unity\Hub\Editor\6000.3.24f1\Editor\Unity.exe',
    [string]$ProjectPath,
    [ValidateSet('Editor', 'Mono', 'IL2CPP')][string[]]$Targets = @('Editor', 'Mono', 'IL2CPP'),
    [string]$DeviceId,
    [switch]$BuildOnly
)
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if (-not $ProjectPath) { $ProjectPath = Join-Path $repoRoot 'artifacts/UnityValidation' }
$ProjectPath = [IO.Path]::GetFullPath($ProjectPath)
if (-not (Test-Path -LiteralPath $UnityEditorPath)) { throw "Unity Editor is missing: $UnityEditorPath" }
$packagePath = Join-Path $repoRoot 'unity/com.polarble.sdk'
foreach ($dll in @('Runtime/Plugins/Managed/Polar.Core.dll', 'Runtime/Plugins/x86_64/Polar.Native.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $packagePath $dll))) { throw 'Run scripts/build.ps1 to publish the SDK and refresh package binaries first.' }
}
$assets = Join-Path $ProjectPath 'Assets/PolarSmoke'
$packages = Join-Path $ProjectPath 'Packages'
New-Item -ItemType Directory -Path $assets, (Join-Path $assets 'Editor'), $packages, (Join-Path $ProjectPath 'ProjectSettings') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Runtime/PolarSmokeRunner.cs') -Destination (Join-Path $assets 'PolarSmokeRunner.cs') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Editor/PolarSmokeBuild.cs') -Destination (Join-Path $assets 'Editor/PolarSmokeBuild.cs') -Force
$manifestPath = Join-Path $packages 'manifest.json'
if (Test-Path -LiteralPath $manifestPath) { $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json }
else { $manifest = [pscustomobject]@{ dependencies = [pscustomobject]@{ 'com.unity.modules.jsonserialize' = '1.0.0' } } }
$packageBase = [Uri]::new($packages.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar)
$packageRelative = [Uri]::UnescapeDataString($packageBase.MakeRelativeUri([Uri]::new($packagePath)).ToString())
$manifest.dependencies | Add-Member -NotePropertyName 'com.polarble.sdk' -NotePropertyValue ('file:' + $packageRelative) -Force
$manifest | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $manifestPath -Encoding utf8
$results = Join-Path $ProjectPath ('SmokeResults/' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ'))
New-Item -ItemType Directory -Path $results -Force | Out-Null
function Invoke-SmokeProcess([string]$Executable, [string[]]$Arguments, [int]$TimeoutSeconds) {
    # Start-Process joins an argument string on Windows; quote every argument so paths
    # containing spaces remain one argument. No arguments are evaluated as shell code.
    $quoted = @($Arguments | ForEach-Object { '"' + $_.Replace('"', '\"') + '"' })
    $process = Start-Process -FilePath $Executable -ArgumentList $quoted -PassThru -WindowStyle Hidden
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while (-not $process.WaitForExit(1000)) {
        if ($watch.Elapsed.TotalSeconds -gt $TimeoutSeconds) {
            Stop-Process -Id $process.Id -Force
            throw "Smoke process exceeded $TimeoutSeconds seconds. Logs: $results"
        }
    }
    if ($process.ExitCode -ne 0) { throw "Smoke process exited $($process.ExitCode). Logs: $results" }
}
function Get-SmokeArgs([string]$LogName) {
    $arguments = @('-batchmode', '-nographics', '-logFile', (Join-Path $results $LogName), '--polar-results', $results)
    if ($DeviceId) { $arguments += @('--polar-device', $DeviceId) }
    return $arguments
}
foreach ($target in $Targets) {
    if ($target -eq 'Editor') {
        $arguments = @(Get-SmokeArgs 'Editor.log') + @('-projectPath', $ProjectPath, '-executeMethod', 'PolarValidation.PolarSmokeBuild.RunEditor')
        Invoke-SmokeProcess -Executable $UnityEditorPath -Arguments $arguments -TimeoutSeconds 300
        $resultFile = Join-Path $results 'UnityEditor-Mono.json'
    }
    else {
        $method = if ($target -eq 'Mono') { 'BuildMono' } else { 'BuildIl2Cpp' }
        $arguments = @(Get-SmokeArgs ('Build-' + $target + '.log')) + @('-projectPath', $ProjectPath, '-executeMethod', ('PolarValidation.PolarSmokeBuild.' + $method))
        Invoke-SmokeProcess -Executable $UnityEditorPath -Arguments $arguments -TimeoutSeconds 1200
        if ($BuildOnly) { continue }
        $player = Join-Path $ProjectPath ('Builds/' + $target + '/PolarSmoke.exe')
        Invoke-SmokeProcess -Executable $player -Arguments @(Get-SmokeArgs ('Player-' + $target + '.log')) -TimeoutSeconds 90
        $resultFile = Join-Path $results ('WindowsStandalone-' + $target + '.json')
    }
    $result = Get-Content -LiteralPath $resultFile -Raw | ConvertFrom-Json
    if (-not $result.success) { throw "Smoke result failed: $resultFile" }
    Write-Host "PASS $target; advertisements=$($result.advertisements); Polar devices=$($result.polarDevices); connected=$($result.connected)"
}
Write-Host "Unity smoke evidence: $results"
