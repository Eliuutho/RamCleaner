# Instala RamCleaner Tray: lo inicia ahora y lo deja arrancando con Windows
# (tarea programada elevada => sin ventana UAC en cada inicio). Se auto-eleva con UAC.
$exe = 'C:\Tools\RamCleaner\RamCleanerTray.exe'

$id = [Security.Principal.WindowsIdentity]::GetCurrent()
$esAdmin = ([Security.Principal.WindowsPrincipal]$id).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $esAdmin) {
    Start-Process powershell -Verb RunAs -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`""
    exit
}

# La tarea periódica de la versión CLI ya no hace falta: el tray limpia por umbral/intervalo
schtasks /Delete /TN "RamCleaner" /F 2>$null

# Register-ScheduledTask en vez de schtasks /Create: sin límite de 72 h de ejecución
# (el default mataría el tray al tercer día) y con la ruta bien entrecomillada
$action   = New-ScheduledTaskAction -Execute $exe
$trigger  = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
$trigger.Delay = 'PT10S'
$settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
Register-ScheduledTask -TaskName "RamCleanerTray" -Action $action -Trigger $trigger -Settings $settings -RunLevel Highest -Force | Out-Null
Start-Process $exe
Write-Host ""
Write-Host "Listo: RamCleaner esta en la bandeja del sistema y arrancara con Windows."
Write-Host "Doble clic en el icono = limpiar ahora. Clic derecho = opciones."
Start-Sleep 4
