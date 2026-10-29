[CmdletBinding()]
param([string]$BuildDirectory='artifacts/native/ninja',[string]$OutputDirectory='artifacts/packages')
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$build=[IO.Path]::GetFullPath((Join-Path $root $BuildDirectory))
$output=[IO.Path]::GetFullPath((Join-Path $root $OutputDirectory))
$version='0.2.0-preview'
$stage=Join-Path $output ('stage-'+[guid]::NewGuid().ToString('N'))
$sdk=Join-Path $stage "polar-native-sdk-$version-windows-x64"
$example=Join-Path $stage "polar-windows-example-$version-x64"
$upm=Join-Path $stage 'package'
New-Item -ItemType Directory -Force $output,$stage,$sdk,$example,$upm | Out-Null
$cmake=Join-Path $root '.tools/cmake-3.31.6-windows-x86_64/bin/cmake.exe'
& $cmake --install $build --prefix $sdk
if($LASTEXITCODE -ne 0){throw 'CMake install failed'}
Copy-Item -LiteralPath (Join-Path $build 'polar_example.exe'),(Join-Path $build 'polar_sdk.dll') -Destination $example
Copy-Item -LiteralPath (Join-Path $root 'LICENSES') -Destination $example -Recurse
Copy-Item -LiteralPath (Join-Path $root 'NOTICE'),(Join-Path $root 'README.md') -Destination $example
Copy-Item -Path (Join-Path $root 'unity/com.polarble.sdk/*') -Destination $upm -Recurse
Copy-Item -LiteralPath (Join-Path $build 'polar_sdk.dll') -Destination (Join-Path $upm 'Runtime/Plugins/x86_64/polar_sdk.dll') -Force
if((Get-FileHash (Join-Path $build 'polar_sdk.dll')).Hash -ne (Get-FileHash (Join-Path $root 'unity/com.polarble.sdk/Runtime/Plugins/x86_64/polar_sdk.dll')).Hash){throw 'Build DLL differs from Unity candidate; refresh and validate before packaging'}
$manifest=@{version=$version;abi=2;target='windows-x64';minimumWindows='10.0.19041';dllSha256=(Get-FileHash (Join-Path $build 'polar_sdk.dll')).Hash.ToLowerInvariant();sourceCommit=(& git -C $root rev-parse HEAD);sourceDirty=[bool](& git -C $root status --porcelain);createdUtc=[DateTime]::UtcNow.ToString('o');verificationStatus='pending-final-verification';verificationNotes='See docs/VERIFICATION.md. Building and packaging do not certify tests or live device support.'} | ConvertTo-Json -Depth 4
foreach($directory in @($sdk,$example,$upm)){$manifest | Set-Content (Join-Path $directory 'build-manifest.json') -Encoding utf8}
$sdkZip=Join-Path $output "polar-native-sdk-$version-windows-x64.zip"
$guiZip=Join-Path $output "polar-windows-example-$version-x64.zip"
$upmTgz=Join-Path $output 'com.polarble.sdk-0.2.0-preview.1.tgz'
Compress-Archive -LiteralPath $sdk -DestinationPath $sdkZip -Force
Compress-Archive -LiteralPath $example -DestinationPath $guiZip -Force
& tar -czf $upmTgz -C $stage package
if($LASTEXITCODE -ne 0){throw 'UPM tar failed'}
@($sdkZip,$guiZip,$upmTgz) | ForEach-Object { $h=Get-FileHash -LiteralPath $_;"$($h.Hash.ToLowerInvariant())  $([IO.Path]::GetFileName($_))" } | Set-Content (Join-Path $output 'SHA256SUMS.txt') -Encoding ascii
Write-Host "Packages: $output"
