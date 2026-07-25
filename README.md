# RamCleaner

**Mem Reduct-style RAM cleaner + CPU spike watcher for Windows 10/11, in pure C#.** No dependencies, no frameworks — it builds with the `csc.exe` compiler that ships with every Windows install. UI in Spanish.

---

## EN — Overview

### RamCleanerTray.exe

Tray app with a live RAM % icon (turns red above your threshold):

- **Double-click** the icon = clean now; balloon shows how many MB were freed.
- **Right-click menu**: choose what to clean (process working sets, standby list, low-priority standby list, modified page list, system file cache), auto-clean by usage threshold (80–95%) and/or interval (15/30/60 min), notification modes (always / **not while gaming** — uses `SHQueryUserNotificationState` / manual cleans only / never), and "Start with Windows".
- When emptying working sets it **skips the foreground process**, so it never freezes the game you are playing.
- `RamCleanerTray.exe -clean` = silent one-shot clean and exit, for scripts and scheduled tasks (same config and log).
- Settings persist in `config.ini`; every clean is logged to `RamCleaner.log`.

### CpuWatch.exe

Headless CPU spike watcher: samples every 2 s and, when total CPU stays above the threshold (default 80% sustained 10 s), logs the **top 5 consumers** and the foreground process to `CpuWatch.log` — so unexplained background spikes get caught with a timestamp even when you are not looking. Spikes caused mostly by the app you are actively using are ignored (configurable in `CpuWatch.ini`).

### Build

```
csc /nologo /optimize /codepage:65001 /target:winexe /win32manifest:app.manifest /out:RamCleanerTray.exe RamCleanerTray.cs
csc /nologo /optimize /codepage:65001 /target:winexe /out:CpuWatch.exe CpuWatch.cs
```

(`csc` = `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`)

### Install

Put the exes and `instalar-tarea.ps1` in a permanent folder, then:

```
powershell -NoProfile -ExecutionPolicy Bypass -File instalar-tarea.ps1
```

One UAC prompt: starts both tools and registers autostart tasks (no execution time limit, no UAC at logon). Purging the standby list requires administrator rights — that is why the tray's manifest requests elevation. Uninstall: "Salir" in the tray menu, delete the `RamCleanerTray` and `CpuWatch` tasks in Task Scheduler, delete the folder.

> **Note:** binaries are unsigned; SmartScreen may warn on download. The full source is in this repo and builds with Windows' own compiler — audit and compile it yourself if you prefer.

---

## ES — Documentación

### Componentes

- **RamCleanerTray.cs** — app residente de bandeja: icono con % de RAM en vivo (rojo al superar el umbral), doble clic = limpiar, menú con opciones de qué limpiar, auto-limpieza por umbral de uso y por intervalo, notificación con MB liberados (configurable: siempre / no durante juegos / solo manuales / nunca), "Iniciar con Windows" y config persistente en `config.ini`. Con `-clean` hace una limpieza silenciosa de un disparo y sale (para scripts y tareas programadas).
- **CpuWatch.cs** — vigilante de picos de CPU: sin UI, muestrea cada 2 s y registra en `CpuWatch.log` qué procesos consumían cuando la CPU total supera el umbral de forma sostenida (config en `CpuWatch.ini`; omite picos causados mayormente por la app en primer plano, p. ej. un juego).
- **app.manifest** — pide `requireAdministrator` (la purga de standby list lo exige).
- **instalar-tarea.ps1** — instala ambos: los inicia ya y registra sus tareas de logon vía `Register-ScheduledTask`.

### Qué limpia

| Opción | Mecanismo | Por defecto |
|---|---|---|
| Working sets de procesos | `EmptyWorkingSet` por proceso, **salta el proceso en primer plano** (no congela juegos) | ✅ |
| Standby list | `NtSetSystemInformation(SystemMemoryListInformation, MemoryPurgeStandbyList)` | ✅ |
| Standby list (baja prioridad) | ídem, comando 5 | ✅ |
| Modified page list | ídem, comando 3 (escribe al pagefile) | ❌ |
| Caché de archivos del sistema | `SetSystemFileCacheSize(-1, -1, 0)` | ❌ |

### Detalles de robustez

- `ChangeWindowMessageFilter(TaskbarCreated)` para que UIPI no deje al proceso elevado sin icono tras un reinicio de explorer o en la carrera del logon.
- Tareas de arranque con `ExecutionTimeLimit = PT0S` (el default de `schtasks` mataría un proceso residente a las 72 h) y rutas entrecomilladas (soportan espacios).
- Instancia única por mutex; handles de token cerrados; sin fuga de HICON en el redibujado del icono; notificaciones silenciadas en pantalla completa vía `SHQueryUserNotificationState`.

### Compilar e instalar

Igual que en la sección EN: compila con el `csc` de Windows y ejecuta `instalar-tarea.ps1` desde la carpeta definitiva. Los logs (`RamCleaner.log`, `CpuWatch.log`) quedan junto a los exes.
