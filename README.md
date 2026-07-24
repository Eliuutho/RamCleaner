# RamCleaner

Limpiador de RAM para Windows 11 estilo Mem Reduct, en C# puro (.NET Framework, sin dependencias): se compila con el `csc.exe` que trae Windows.

## Componentes

- **RamCleanerTray.cs** — app residente de bandeja: icono con % de RAM en vivo (rojo al superar el umbral), doble clic = limpiar, menú con opciones de qué limpiar, auto-limpieza por umbral de uso y por intervalo, notificación con MB liberados, "Iniciar con Windows" (tarea programada elevada sin límite de ejecución, sin UAC por arranque) y config persistente en `config.ini`.
- **RamCleaner.cs** — versión CLI silenciosa de un disparo (la original), útil para tareas programadas.
- **app.manifest** — pide `requireAdministrator` (la purga de standby list lo exige).
- **instalar-tarea.ps1** — instala el tray: lo inicia ya y registra la tarea de logon vía `Register-ScheduledTask`.

## Qué limpia

| Opción | Mecanismo | Por defecto |
|---|---|---|
| Working sets de procesos | `EmptyWorkingSet` por proceso, **salta el proceso en primer plano** (no congela juegos) | ✅ |
| Standby list | `NtSetSystemInformation(SystemMemoryListInformation, MemoryPurgeStandbyList)` | ✅ |
| Standby list (baja prioridad) | ídem, comando 5 | ✅ |
| Modified page list | ídem, comando 3 (escribe al pagefile) | ❌ |
| Caché de archivos del sistema | `SetSystemFileCacheSize(-1, -1, 0)` | ❌ |

## Detalles de robustez

- `ChangeWindowMessageFilter(TaskbarCreated)` para que UIPI no deje al proceso elevado sin icono tras un reinicio de explorer o en la carrera del logon.
- La tarea de arranque se registra vía XML/`Register-ScheduledTask` con `ExecutionTimeLimit = PT0S` (el default de `schtasks` mataría el proceso a las 72 h) y ruta entrecomillada (soporta espacios).
- Instancia única por mutex; handles de token cerrados; sin fuga de HICON en el redibujado del icono.

## Compilar

```
csc /nologo /optimize /target:winexe /win32manifest:app.manifest /out:RamCleanerTray.exe RamCleanerTray.cs
csc /nologo /optimize /target:winexe /out:RamCleaner.exe RamCleaner.cs
```

(`csc` = `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`)

## Instalar

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File instalar-tarea.ps1
```

Acepta el UAC y listo: icono en la bandeja y arranque automático con Windows. El log de limpiezas queda en `RamCleaner.log`.
