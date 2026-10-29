[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$packagePath = Join-Path $repoRoot 'unity/com.polarble.sdk'
foreach ($relative in @('Runtime/Plugins/Managed/Polar.Core.dll', 'Runtime/Plugins/x86_64/Polar.Native.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $packagePath $relative))) { throw 'Run scripts/build.ps1 first.' }
}
$licenseTarget = Join-Path $packagePath 'Licenses~'
$docsTarget = Join-Path $packagePath 'Documentation~'
New-Item -ItemType Directory -Path $licenseTarget, $docsTarget -Force | Out-Null
Get-ChildItem -LiteralPath (Join-Path $repoRoot 'LICENSES') -File | Copy-Item -Destination $licenseTarget -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSES/Polar_SDK_License.txt') -Destination (Join-Path $packagePath 'LICENSE.md') -Force
$notice = Get-Content -LiteralPath (Join-Path $repoRoot 'NOTICE') -Raw
$notice.Replace('Original license: LICENSES/Polar_SDK_License.txt', 'Original license: LICENSE.md') |
    Set-Content -LiteralPath (Join-Path $packagePath 'NOTICE.md') -Encoding utf8 -NoNewline
foreach ($name in @('PORTING-STATUS.md', 'VERIFICATION.md', 'api-inventory.csv', 'api-mapping.json')) {
    Copy-Item -LiteralPath (Join-Path $repoRoot ('docs/' + $name)) -Destination $docsTarget -Force
}
$statusPath = Join-Path $docsTarget 'PORTING-STATUS.md'
$status = Get-Content -LiteralPath $statusPath -Raw
$status.Replace('(../scripts/generate_api_inventory.py)', '(https://github.com/zjxps2007/Polar_BLE_C-/blob/main/scripts/generate_api_inventory.py)') |
    Set-Content -LiteralPath $statusPath -Encoding utf8 -NoNewline
Copy-Item -LiteralPath (Join-Path $repoRoot 'artifacts/build/package-manifest.json') -Destination (Join-Path $docsTarget 'build-manifest.json') -Force
$package = Get-Content -LiteralPath (Join-Path $packagePath 'package.json') -Raw | ConvertFrom-Json
$stage = Join-Path $repoRoot ('artifacts/package-staging/' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage -Force | Out-Null
Copy-Item -LiteralPath $packagePath -Destination (Join-Path $stage 'package') -Recurse
$output = Join-Path $repoRoot ('artifacts/' + $package.name + '-' + $package.version + '.tgz')
& tar -czf $output -C $stage package
if ($LASTEXITCODE -ne 0) { throw 'tar creation failed.' }
$entries = @(& tar -tf $output)
if ($LASTEXITCODE -ne 0 -or $entries -notcontains 'package/package.json' -or $entries -notcontains 'package/Runtime/Plugins/x86_64/Polar.Native.dll') {
    throw 'Archive validation failed.'
}
$file = Get-Item -LiteralPath $output
[ordered]@{ file = $file.Name; bytes = $file.Length; sha256 = (Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash } |
    ConvertTo-Json | Set-Content -LiteralPath ($output + '.sha256.json') -Encoding utf8
Write-Host "Unity package: $output"
