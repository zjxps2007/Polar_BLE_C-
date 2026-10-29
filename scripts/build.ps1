[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [string]$DotNetPath,
    [switch]$SkipTests,
    [switch]$SkipNative,
    [switch]$NoRestore
)
$ErrorActionPreference = 'Stop'
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $DotNetPath) {
    $bundledSdk = Join-Path $repoRoot '.tools/dotnet/dotnet.exe'
    if (Test-Path -LiteralPath $bundledSdk) { $DotNetPath = $bundledSdk }
    else { $DotNetPath = (Get-Command dotnet -ErrorAction Stop).Source }
}
$env:DOTNET_CLI_HOME = Join-Path $repoRoot '.tools/cli'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
function Invoke-DotNet([string[]]$Arguments) {
    & $DotNetPath @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE" }
}
Push-Location $repoRoot
try {
    $projects = @('src/Polar.Core/Polar.Core.csproj', 'src/Polar.Windows/Polar.Windows.csproj', 'samples/Polar.Console/Polar.Console.csproj')
    if (-not $SkipTests) {
        $projects += 'tests/Polar.Tests/Polar.Tests.csproj'
        $projects += 'src/Polar.Windows/Verification/Polar.Windows.Verification.csproj'
        $projects += 'src/Polar.Windows/Verification/UnityAdapter/UnityAdapter.Compile.csproj'
    }
    if (-not $SkipNative) { $projects += 'src/Polar.Native/Polar.Native.csproj' }
    if (-not $NoRestore) { foreach ($project in $projects) { Invoke-DotNet -Arguments @('restore', $project) } }
    Invoke-DotNet -Arguments @('build', 'src/Polar.Windows/Polar.Windows.csproj', '-c', $Configuration, '--no-restore')
    Invoke-DotNet -Arguments @('build', 'samples/Polar.Console/Polar.Console.csproj', '-c', $Configuration, '--no-restore')
    if (-not $SkipTests) {
        Invoke-DotNet -Arguments @('run', '--project', 'tests/Polar.Tests/Polar.Tests.csproj', '-c', $Configuration, '--no-restore')
        Invoke-DotNet -Arguments @('run', '--project', 'src/Polar.Windows/Verification/Polar.Windows.Verification.csproj', '-c', $Configuration, '--no-restore')
        Invoke-DotNet -Arguments @('build', 'src/Polar.Windows/Verification/UnityAdapter/UnityAdapter.Compile.csproj', '-c', $Configuration, '--no-restore')
    }
    if (-not $SkipNative) {
        Invoke-DotNet -Arguments @('publish', 'src/Polar.Native/Polar.Native.csproj', '-c', $Configuration, '--no-restore')
    }
    $managedTarget = Join-Path $repoRoot 'unity/com.polarble.sdk/Runtime/Plugins/Managed'
    $nativeTarget = Join-Path $repoRoot 'unity/com.polarble.sdk/Runtime/Plugins/x86_64'
    New-Item -ItemType Directory -Path $managedTarget, $nativeTarget -Force | Out-Null
    $copyFiles = @{
        (Join-Path $repoRoot "src/Polar.Core/bin/$Configuration/netstandard2.1/Polar.Core.dll") = (Join-Path $managedTarget 'Polar.Core.dll')
    }
    # Resolve exact restored runtime assets from project.assets.json rather than assuming
    # a package-cache path or silently copying a different NuGet version.
    $assets = Get-Content -LiteralPath (Join-Path $repoRoot 'src/Polar.Core/obj/project.assets.json') -Raw | ConvertFrom-Json
    $packageFolders = @($assets.packageFolders.PSObject.Properties.Name)
    foreach ($spec in @(
        @{ Name = 'Google.Protobuf'; File = 'lib/netstandard2.0/Google.Protobuf.dll' },
        @{ Name = 'System.Runtime.CompilerServices.Unsafe'; File = 'lib/netstandard2.0/System.Runtime.CompilerServices.Unsafe.dll' }
    )) {
        $library = @($assets.libraries.PSObject.Properties | Where-Object { $_.Name.StartsWith($spec.Name + '/', [StringComparison]::OrdinalIgnoreCase) })
        if ($library.Count -ne 1) { throw "Expected one restored package for $($spec.Name)." }
        $source = $null
        foreach ($folder in $packageFolders) {
            $candidate = Join-Path (Join-Path $folder $library[0].Value.path) $spec.File
            if (Test-Path -LiteralPath $candidate) { $source = $candidate; break }
        }
        if (-not $source) { throw "Restored runtime asset missing: $($spec.Name)/$($spec.File)" }
        $copyFiles[$source] = Join-Path $managedTarget ([IO.Path]::GetFileName($spec.File))
    }
    if (-not $SkipNative) {
        # Only publish/ contains the native DLL; bin/<configuration>/ contains managed IL.
        $nativeSource = Join-Path $repoRoot "src/Polar.Native/bin/$Configuration/net10.0-windows10.0.19041.0/win-x64/publish/Polar.Native.dll"
        $copyFiles[$nativeSource] = Join-Path $nativeTarget 'Polar.Native.dll'
        $verificationOutput = Join-Path $repoRoot "src/Polar.Windows/Verification/bin/$Configuration/net10.0-windows10.0.19041.0"
        if (Test-Path -LiteralPath $verificationOutput) {
            Copy-Item -LiteralPath $nativeSource -Destination (Join-Path $verificationOutput 'Polar.Native.dll') -Force
        }
    }
    foreach ($entry in $copyFiles.GetEnumerator()) {
        if (-not (Test-Path -LiteralPath $entry.Key)) { throw "Build output missing: $($entry.Key)" }
        Copy-Item -LiteralPath $entry.Key -Destination $entry.Value -Force
    }
    $manifest = foreach ($entry in $copyFiles.GetEnumerator()) {
        $file = Get-Item -LiteralPath $entry.Value
        [ordered]@{ file = $file.FullName.Substring($repoRoot.Length + 1).Replace('\', '/'); bytes = $file.Length; sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash }
    }
    $evidence = Join-Path $repoRoot 'artifacts/build'
    New-Item -ItemType Directory -Path $evidence -Force | Out-Null
    [ordered]@{ generatedUtc = [DateTime]::UtcNow.ToString('O'); configuration = $Configuration; nativePublished = -not $SkipNative; files = @($manifest) } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $evidence 'package-manifest.json') -Encoding utf8
    Write-Host "Unity package refreshed: $(Join-Path $repoRoot 'unity/com.polarble.sdk')"
    if ($SkipNative) { Write-Warning 'Native publish was skipped; an existing native DLL was not refreshed.' }
}
finally { Pop-Location }
