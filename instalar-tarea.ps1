# Instala RamCleaner Tray + CpuWatch: los inicia ya y los deja arrancando con Windows
# (tareas programadas => sin ventana UAC en cada inicio). Se auto-eleva con UAC.
# Ejecutar desde la carpeta donde esten los exes (usa rutas relativas al script).
$dir = Split-Path -Parent $PSCommandPath
$exe = Join-Path $dir 'RamCleanerTray.exe'

$id = [Security.Principal.WindowsIdentity]::GetCurrent()
$esAdmin = ([Security.Principal.WindowsPrincipal]$id).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $esAdmin) {
    Start-Process powershell -Verb RunAs -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`""
    exit
}

# tarea de la antigua version CLI (hoy integrada en el tray como "-clean")
schtasks /Delete /TN "RamCleaner" /F 2>$null

# Register-ScheduledTask en vez de schtasks /Create: sin limite de 72 h de ejecucion
# (el default mataria un proceso residente al tercer dia) y ruta bien entrecomillada
$settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries

$action  = New-ScheduledTaskAction -Execute $exe
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
$trigger.Delay = 'PT10S'
Register-ScheduledTask -TaskName "RamCleanerTray" -Action $action -Trigger $trigger -Settings $settings -RunLevel Highest -Force | Out-Null
Start-Process $exe

# CpuWatch (vigilante de picos de CPU), si esta junto al instalador
$wexe = Join-Path $dir 'CpuWatch.exe'
if (Test-Path $wexe) {
    $waction  = New-ScheduledTaskAction -Execute $wexe
    $wtrigger = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
    $wtrigger.Delay = 'PT20S'
    Register-ScheduledTask -TaskName "CpuWatch" -Action $waction -Trigger $wtrigger -Settings $settings -Force | Out-Null
    Start-Process $wexe
}

Write-Host ""
Write-Host "Listo: RamCleaner esta en la bandeja (doble clic = limpiar, clic derecho = opciones)."
Write-Host "CpuWatch vigila en segundo plano; su log queda junto a los exes."
Start-Sleep 4
