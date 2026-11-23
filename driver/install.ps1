# Регистрирует драйвер RollWorld в SteamVR через vrpathreg (копировать файлы в папку Steam не нужно, права администратора не нужны).
$ErrorActionPreference = 'Stop'
$pkg = Join-Path $PSScriptRoot 'package\01rollworld'
$dll = Join-Path $pkg 'bin\win64\driver_01rollworld.dll'
if (-not (Test-Path $dll)) { throw "Не найден $dll" }
Unblock-File $dll -ErrorAction SilentlyContinue

if (Get-Process vrserver -ErrorAction SilentlyContinue) {
    Write-Warning 'SteamVR сейчас запущен. Закрой его; драйвер подхватится при следующем запуске SteamVR.'
}

$cfg = Join-Path $env:LOCALAPPDATA 'openvr\openvrpaths.vrpath'
if (-not (Test-Path $cfg)) { throw "Не найден $cfg. Запусти SteamVR один раз и повтори." }
$runtime = (Get-Content $cfg -Raw | ConvertFrom-Json).runtime | Select-Object -First 1
$reg = Join-Path $runtime 'bin\win64\vrpathreg.exe'
if (-not (Test-Path $reg)) { throw "Не найден vrpathreg.exe: $reg" }

& $reg adddriver $pkg
Write-Host ''
& $reg show
Write-Host ''
Write-Host 'Готово. Запусти SteamVR. В SteamVR -> Настройки -> Разработчик драйвер "01rollworld" должен быть в списке. Затем запусти RollWorld.' -ForegroundColor Green
