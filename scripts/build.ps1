[CmdletBinding()]
param(
    [ValidateSet('Release','Debug')][string]$Configuration='Release',
    [string]$BuildDirectory='artifacts/native/ninja',
    [string]$VisualStudioPath,
    [switch]$Asan,
    [switch]$SkipTests,
    [switch]$NoGui,
    [int]$Jobs=8
)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$build=[IO.Path]::GetFullPath((Join-Path $root $BuildDirectory))
$toolRoot=Join-Path $root '.tools'
New-Item -ItemType Directory -Force $toolRoot | Out-Null
function Get-PinnedZip([string]$Url,[string]$Hash,[string]$Archive,[string]$Destination) {
    if(-not(Test-Path -LiteralPath $Archive)){Invoke-WebRequest -Uri $Url -OutFile $Archive}
    if((Get-FileHash -LiteralPath $Archive -Algorithm SHA256).Hash -ne $Hash){throw "Checksum mismatch: $Archive"}
    Expand-Archive -LiteralPath $Archive -DestinationPath $Destination -Force
}
$cmake=Join-Path $toolRoot 'cmake-3.31.6-windows-x86_64/bin/cmake.exe'
if(-not(Test-Path -LiteralPath $cmake)) {
    Get-PinnedZip 'https://github.com/Kitware/CMake/releases/download/v3.31.6/cmake-3.31.6-windows-x86_64.zip' 'D163CD3AB4959B0A53FA8988F2DDBD2E6C501658201E6A154386BAD9DBE4F836' (Join-Path $toolRoot 'cmake-3.31.6.zip') $toolRoot
}
$ninja=Join-Path $toolRoot 'ninja/ninja.exe'
if(-not(Test-Path -LiteralPath $ninja)) {
    Get-PinnedZip 'https://github.com/ninja-build/ninja/releases/download/v1.12.1/ninja-win.zip' 'F550FEC705B6D6FF58F2DB3C374C2277A37691678D6ABA463ADCBB129108467A' (Join-Path $toolRoot 'ninja-1.12.1.zip') (Join-Path $toolRoot 'ninja')
}
if(-not $VisualStudioPath) {
    $local=Join-Path $toolRoot 'BuildTools'
    if(Test-Path -LiteralPath (Join-Path $local 'VC/Auxiliary/Build/vcvars64.bat')){$VisualStudioPath=$local}
    else {
        $vswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
        if(Test-Path -LiteralPath $vswhere){$VisualStudioPath=& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath}
    }
}
if(-not $VisualStudioPath){throw 'Install VS 2022 Build Tools with Desktop development with C++, Windows SDK >= 10.0.22000 and optional AddressSanitizer.'}
$vcvars=Join-Path $VisualStudioPath 'VC/Auxiliary/Build/vcvars64.bat'
if($vcvars.Contains('"')){throw 'Invalid Visual Studio path'}
# Import the toolchain environment once; build arguments stay an argument array.
$environment=& cmd /d /s /c "`"$vcvars`" >nul && set"
if($LASTEXITCODE -ne 0){throw 'vcvars64 failed'}
foreach($line in $environment){if($line -match '^([^=]+)=(.*)$'){[Environment]::SetEnvironmentVariable($matches[1],$matches[2],'Process')}}
$compiler=(Get-Command cl.exe -ErrorAction Stop).Source
if(-not(Test-Path -LiteralPath (Join-Path (Split-Path $compiler) '1033/clui.dll'))){throw 'Install the English language pack for Visual Studio Build Tools (en-US); Ninja header dependency tracking uses English /showIncludes output.'}
if($Asan){$env:ASAN_OPTIONS='halt_on_error=1:alloc_dealloc_mismatch=1'}
$asanValue=if($Asan){'ON'}else{'OFF'}
$guiValue=if($NoGui){'OFF'}else{'ON'}
& $cmake -S $root -B $build -G Ninja "-DCMAKE_BUILD_TYPE=$Configuration" "-DCMAKE_MAKE_PROGRAM=$ninja" "-DPOLAR_ASAN=$asanValue" "-DPOLAR_GUI=$guiValue"
if($LASTEXITCODE -ne 0){throw 'CMake configure failed'}
& $cmake --build $build --parallel $Jobs
if($LASTEXITCODE -ne 0){throw 'CMake build failed'}
if(-not $SkipTests) {
    Push-Location $root
    try {& (Join-Path (Split-Path $cmake) 'ctest.exe') --test-dir $build --output-on-failure;if($LASTEXITCODE -ne 0){throw 'Native tests failed'}}
    finally {Pop-Location}
}
Write-Host "Native SDK build: $build"
