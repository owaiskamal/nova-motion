@echo off
rem Puts NOVA Motion's logs and settings into one zip on your Desktop, to send when something goes wrong.
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$out = Join-Path ([Environment]::GetFolderPath('Desktop')) 'nova-motion-logs.zip';" ^
  "$tmp = Join-Path $env:TEMP ('nm-logs-' + [guid]::NewGuid().ToString('N').Substring(0,6)); New-Item -ItemType Directory $tmp | Out-Null;" ^
  "$app = Join-Path $env:APPDATA 'NovaMotion';" ^
  "foreach ($f in 'mpv.log','engine-debug.log','engine.json','engine-state.json','settings.json') { $p = Join-Path $app $f; if (Test-Path $p) { Copy-Item $p $tmp } };" ^
  "foreach ($f in 'tuning.json','tune.log','benchmark.log','setup.log','setup-tensorrt.log','versions.json') { $p = Join-Path 'runtime' $f; if (Test-Path $p) { Copy-Item $p $tmp } };" ^
  "if (Test-Path 'runtime\trt-engines') { Get-ChildItem 'runtime\trt-engines' | Select-Object Name, Length, LastWriteTime | Out-File (Join-Path $tmp 'trt-engines.txt') };" ^
  "if (Test-Path 'runtime\mpv\vs-plugins\models\rife') { Get-ChildItem 'runtime\mpv\vs-plugins\models\rife' | Select-Object Name, Length | Out-File (Join-Path $tmp 'models.txt') };" ^
  "& { Get-CimInstance Win32_VideoController | Select-Object Name, DriverVersion, AdapterRAM; Get-CimInstance Win32_Processor | Select-Object Name, NumberOfCores; (Get-CimInstance Win32_Battery | Select-Object BatteryStatus, EstimatedChargeRemaining); git log --oneline -3 } | Out-File (Join-Path $tmp 'system.txt');" ^
  "if (Test-Path $out) { Remove-Item $out }; Compress-Archive -Path (Join-Path $tmp '*') -DestinationPath $out; Remove-Item $tmp -Recurse;" ^
  "Write-Host ('Saved ' + $out)"
echo.
echo Attach nova-motion-logs.zip (on your Desktop) to the chat.
pause
