[CmdletBinding()]
param([string]$BuildDirectory='artifacts/native/ninja',[string]$ProjectDirectory='artifacts/NativeUnity')
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$build=[IO.Path]::GetFullPath((Join-Path $root $BuildDirectory))
$project=[IO.Path]::GetFullPath((Join-Path $root $ProjectDirectory))
$package=Join-Path $root 'unity/com.polarble.sdk'
$plugin=Join-Path $package 'Runtime/Plugins/x86_64'
New-Item -ItemType Directory -Force $plugin,(Join-Path $project 'Assets/Validation/Editor'),(Join-Path $project 'Packages'),(Join-Path $project 'ProjectSettings') | Out-Null
Copy-Item -LiteralPath (Join-Path $build 'polar_sdk.dll') -Destination $plugin -Force
foreach($pair in @(@('proto','Schemas~'),@('docs','Documentation~'),@('LICENSES','Licenses~'))) {
    $destination=Join-Path $package $pair[1];New-Item -ItemType Directory -Force $destination | Out-Null
    Copy-Item -Path (Join-Path $root ($pair[0]+'/*')) -Destination $destination -Recurse -Force
}
Copy-Item -LiteralPath (Join-Path $root 'NOTICE') -Destination (Join-Path $package 'NOTICE.md') -Force
Copy-Item -LiteralPath (Join-Path $root 'validation/NativeUnity/Runtime/PolarPerfRunner.cs') -Destination (Join-Path $project 'Assets/Validation/PolarPerfRunner.cs') -Force
Copy-Item -LiteralPath (Join-Path $root 'validation/NativeUnity/Editor/NativeBuild.cs') -Destination (Join-Path $project 'Assets/Validation/Editor/NativeBuild.cs') -Force
Copy-Item -LiteralPath (Join-Path $root 'validation/NativeUnity/Editor/PlayStopCycles.cs') -Destination (Join-Path $project 'Assets/Validation/Editor/PlayStopCycles.cs') -Force
@{dependencies=@{'com.polarble.sdk'=('file:'+($package.Replace('\','/')));'com.unity.modules.jsonserialize'='1.0.0';'com.unity.modules.imgui'='1.0.0'}} | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $project 'Packages/manifest.json') -Encoding utf8
"m_EditorVersion: 6000.3.24f1`nm_EditorVersionWithRevision: 6000.3.24f1" | Set-Content (Join-Path $project 'ProjectSettings/ProjectVersion.txt') -Encoding utf8
Get-FileHash -LiteralPath (Join-Path $plugin 'polar_sdk.dll') -Algorithm SHA256
Write-Host "Unity project: $project"
