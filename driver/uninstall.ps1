# Убирает драйвер RollWorld из SteamVR.
$ErrorActionPreference = 'Stop'
$pkg = Join-Path $PSScriptRoot 'package\01rollworld'
$cfg = Join-Path $env:LOCALAPPDATA 'openvr\openvrpaths.vrpath'
$runtime = (Get-Content $cfg -Raw | ConvertFrom-Json).runtime | Select-Object -First 1
$reg = Join-Path $runtime 'bin\win64\vrpathreg.exe'
& $reg removedriver $pkg
& $reg show
