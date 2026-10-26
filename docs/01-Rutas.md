# 01 · Rutas: código, compilados y datos

## 1. Raíz del repositorio

```

├── SpywareAgent.sln                  solución (12 proyectos)
├── Directory.Build.props             TFM net8.0-windows, x64, RollForward = LatestMajor
├── README.md                         resumen + matriz de verificación
├── docs\                             esta documentación
├── installer\                        install.bat · uninstall.bat · installer.iss (Inno Setup 6)
├── src\                              11 proyectos de producción
├── tests\                            Spyware.UnitTests (xUnit, 90 pruebas)
└── publish\                          ejecutable publicado (ver §3)
```

## 2. Código fuente

### `src\Spyware.Core` → `Spyware.Core.dll`

| Archivo | Líneas | Contenido |
|---|---|---|
| `Paths\AgentPaths.cs` | 56 | único directorio raíz de datos y todas sus subcarpetas (incluidas `keylog\` y `notes\`) |
| `Configuration\AgentConfig.cs` | 199 | modelo de configuración (9 secciones) con valores por defecto: `KeyboardSection` (12 claves) y `NotesSection` (12 claves) incluidos |
| `Configuration\ConfigLoader.cs` | 96 | carga/creación, respaldo de JSON corrupto, niveles de log |
| `Contracts\IAgentEvent.cs` · `Events.cs` · `CredentialStatus.cs` · `ICollector.cs` | 170 | contratos de evento (`ScreenshotEvent`, `ClipboardEvent`, `CredentialEvent`, `KeyEvent`, `NoteEvent`, `StatusEvent`) y estados |
| `Contracts\NoteStatus.cs` | 26 | estados de lectura de notas: `read`/`deleted`/`unsupported`/`corrupted`/`locked`/`access_denied`/`not_found` |
| `Logging\ILog.cs` · `FileLogger.cs` | 123 | log en texto plano con rotación por tamaño |
| `Scheduling\Jitter.cs` · `PeriodicTask.cs` | 117 | desfase aleatorio y tarea periódica anti-solapamiento |
| `Imaging\ImageCompressor.cs` | 50 | resize + JPEG con calidad + PNG |
| `Security\DpapiHelper.cs` · `HashHelper.cs` | 55 | **solo `Unprotect`** y SHA-256/base64 |
| `Runtime\SingleInstanceGuard.cs` · `ProcessContext.cs` | 152 | mutex de instancia única y contexto de proceso/token/sesión |

### `src\Spyware.Persistence` → `Spyware.Persistence.dll`

| Archivo | Líneas | Contenido |
|---|---|---|
| `RegistryRunInstaller.cs` | 60 | `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` (ruta entre comillas) |
| `StartupFolderInstaller.cs` | 94 | acceso directo `.lnk` en la carpeta de Inicio (WScript.Shell + reintentos de borrado) |

### `src\Spyware.Storage` → `Spyware.Storage.dll`

| Archivo | Líneas | Contenido |
|---|---|---|
| `LocalEventRepository.cs` | 664 | SQLite en claro: alta en 5 tablas (`screenshots`, `clipboard_events`, `credentials`, `keystrokes`, `notes`), cola `sent`, estadísticas, purga por edad, `DeleteByPaths` (incluidas las filas de teclado) y `GetReferencedFilePaths` para el barrido de huérfanos |
| `SpoolManager.cs` | 142 | escritura atómica de JPEG/PNG/TXT/JSON (`scr_`, `clip_`, `keys_`, `note_`, `notes_`, `creds_`) dentro del directorio de datos |
| `RetentionPolicy.cs` | 105 | purga por edad (filas + archivos), barrido de archivos sin fila y purga por tamaño |
| `Rows.cs` · `TimeText.cs` | 46 | filas leídas (incluidas `KeystrokeRow` y `NoteRow`) y formato ISO-8601 UTC |

### `src\Spyware.Collectors.Screenshot` → `Spyware.Collectors.Screenshot.dll`

| Archivo | Líneas | Contenido |
|---|---|---|
| `ScreenCaptureService.cs` | 137 | T1113: captura del `VirtualScreen` multi-monitor, resize, hash anti-duplicado |
| `DesktopState.cs` | 71 | detección de escritorio seguro/bloqueado (`OpenInputDesktop`) |

### `src\Spyware.Collectors.Clipboard` → `Spyware.Collectors.Clipboard.dll`

| Archivo | Líneas | Contenido |
|---|---|---|
| `ClipboardMonitor.cs` | 212 | T1115: hilo STA + `ApplicationContext`, almacenamiento por formato |
| `ClipboardMessageWindow.cs` | 106 | ventana invisible con `AddClipboardFormatListener` y debounce |
| `ClipboardReader.cs` | 80 | lectura de texto/imagen/HTML/archivos con reintentos |

### `src\Spyware.Collectors.Keyboard` → `Spyware.Collectors.Keyboard.dll`

| Archivo | Líneas | Contenido |
|---|---|---|
| `KeyCaptureService.cs` | 287 | T1056.001: drena la cola del hook, traduce, agrupa en ráfagas, cierra por pausa/llenado/cambio de ventana, guarda la fila, la copia `.txt` y avisa si el hook no quedó instalado |
| `KeylogHook.cs` | 265 | `WH_KEYBOARD_LL` en un hilo con bomba `GetMessage`; cola de 8192 pulsaciones, contador de descartadas, estado honesto del alta (`PendingInstall`) y `Enqueue` para inyectar pulsaciones |
| `KeyTranslator.cs` | 243 | `ToUnicodeEx` con el reparto del hilo de la ventana en primer plano, teclas muertas, `VK_PACKET` (IME/dictado/RDP), atajos Ctrl+letra y tabla de marcadores (`[enter]`, `[f5]`, `[pausa Ns]`…) |
| `KeylogBurst.cs` | 81 | texto acumulado de una ventana: separador de pausa, retroceso, tope de caracteres |
| `ForegroundWindow.cs` | 95 | T1010: HWND, título (clase como respaldo) y nombre del proceso |
| `NativeMethods.cs` | 42 | `SetWindowsHookEx`, `KBDLLHOOKSTRUCT`, `CallNextHookEx`, `GetModuleHandle` |

### `src\Spyware.Collectors.Notes` → `Spyware.Collectors.Notes.dll`

| Archivo | Líneas | Contenido |
|---|---|---|
| `StickyNotesProvider.cs` | 251 | T1555.001: descubre almacenes, copia, lee, filtra por hash (solo lo nuevo/cambiado), inserta, copia `.txt` por nota y volcado JSON |
| `StickyNotesSource.cs` | 146 | rutas del paquete `Microsoft.MicrosoftStickyNotes*` (`plum.sqlite` y variantes `plum-<hash>.sqlite`) y del clásico `%APPDATA%\Microsoft\Sticky Notes\StickyNotes.snt`; overrides de demostración |
| `PlumDatabaseReader.cs` | 292 | lectura de la copia con esquema resuelto en ejecución (`PRAGMA table_info`): texto claro/HTML, id, fechas, borrado; `unsupported` si el esquema no se interpreta |
| `SntFileReader.cs` | 167 | `.snt` CFB/OLE: secuencias `Threshold<N>` en orden natural, texto UTF-16LE hasta el NUL y rescate del segmento imprimible |
| `CompoundFile.cs` | 363 | lector CFB/OLE propio (encabezado, FAT, mini FAT, directorio de entradas, cadenas grandes y pequeñas) |
| `SnapshotCopy.cs` | 85 | copia del almacén con reintentos + sidecars `-wal`/`-shm`; nunca se abre el original |
| `NoteText.cs` | 42 | quita las marcas internas `\id=<guid>`, normaliza saltos, recorta marcando el corte |
| `NoteTime.cs` | 48 | FILETIME (ticks de 100 ns) → `DateTime`, tolerante a texto y con guarda de rango |

### `src\Spyware.Collectors.Browsers` → `Spyware.Collectors.Browsers.dll`

| Archivo | Líneas | Contenido |
|---|---|---|
| `BrowserCredentialProvider.cs` | 166 | T1555.003: ciclo programado sobre Chrome/Edge, control de credenciales ya vistas (`skipUnchanged`) y alta en la base |
| `ChromiumCredentialExtractor.cs` | 192 | perfiles reales, copia de `Login Data`, volcado JSON opcional |
| `FileCopier.cs` | 70 | copia con reintentos + sidecars `-wal`/`-shm` |
| `SqliteReader.cs` | 85 | consulta `logins` (moderna y de respaldo) sobre la copia |
| `ChromiumKeyProvider.cs` | 68 | `Local State` → clave AES-256 vía DPAPI |
| `ChromePasswordDecryptor.cs` | 74 | `v10`/`v11` AES-GCM, `v20` App-Bound, blob legado DPAPI |
| `ChromeTime.cs` | 19 | microsegundos desde 1601 → `DateTime` (×10 ticks) |
| `BrowserDefinition.cs` | 12 | rutas relativas de Chrome y Edge |

### `src\Spyware.Transport` → `Spyware.Transport.dll`

| Archivo | Líneas | Contenido |
|---|---|---|
| `LocalFolderExporter.cs` | 67 | exportador primario: lotes JSON en claro |
| `HttpEventExporter.cs` | 122 | opcional: POST JSON, TLS con pinning, reintentos |
| `EventBatcher.cs` | 226 | lee no enviados de las 5 tablas (`screenshot`, `clipboard`, `credential`, `keystroke`, `note`), exporta y marca `sent = 1` |
| `TransportFactory.cs` · `IEventExporter.cs` · `RetryPolicy.cs` | 74 | `local`/`http`/`both`/`none` y backoff con jitter |

### `src\Spyware.Maintenance` → `Spyware.Maintenance.dll`

| Archivo | Líneas | Contenido |
|---|---|---|
| `Installer.cs` | 115 | persistencia + copia opcional de binarios a `bin\` + arranque |
| `Uninstaller.cs` | 118 | detiene procesos, quita Run key/`.lnk`, borra datos |
| `StatusReport.cs` | 63 | informe de consola (persistencia, latido, conteos de las 5 tablas) |
| `HealthCheck.cs` | 64 | `health.json` atómico (lectura y escritura) |

### `src\Spyware.Agent` → `SpywareAgent.dll` + `SpywareAgent.exe` (WinExe)

| Archivo | Líneas | Contenido |
|---|---|---|
| `Program.cs` | 203 | `Main`, despacho de verbos, `RunAgent`, `RunOnce` (teclado, portapapeles, navegadores y notas en un ciclo) |
| `ProgramOptions.cs` | 184 | parser de argumentos y ayuda (`--key-seconds`, `--notes-file`, `--snt-file` incluidos) |
| `AgentRuntime.cs` | 224 | orquesta los 5 collectors, transporte, retención y salud; el teclado se detiene el último para cerrar la ráfaga |
| `AgentApplicationContext.cs` · `HiddenMessageWindow.cs` | 83 | bomba de mensajes sin ventana visible y cierre en logoff |
| `ConsoleAdapter.cs` | 49 | `AttachConsole` al proceso padre para los verbos CLI |
| `app.manifest` | 32 | `asInvoker`, PerMonitorV2, longPathAware |
| `config.sample.json` | 93 | ejemplo exacto de lo que genera `ConfigLoader` (9 secciones, teclado y notas incluidos) |

### `tests\Spyware.UnitTests` → `Spyware.UnitTests.dll` (90 pruebas)

| Archivo | Líneas | Cubre |
|---|---|---|
| `KeyboardTests.cs` | 223 | 23 · modificadores, teclas sin glifo y su desactivación, espacio/retroceso, tolerancia del glifo según reparto, marcadores de pausa, ráfagas (llenado, retroceso, pausa, ventana) y ciclo de vida + cola del hook |
| `KeyboardPipelineTests.cs` | 333 | 8 · pulsaciones inyectadas → traducción → `storage.db` → copia `keys_*.txt` → lote JSON con `sent`; cierre de ráfaga al cambiar de ventana, retroceso que edita, búfer lleno sin perder pulsaciones, ráfaga idéntica omitida, `recordWindowTitles = false`, teclado deshabilitado y aviso en el log si el hook no instala |
| `StickyNotesReaderTests.cs` | 284 | 18 · esquema moderno y 3.x, solo HTML, esquema/bandeja no soportada, nota borrada (estado y bandera), BLOB UTF-8, `MaxResultsPerRun`, lectura sin tocar el almacén, resolución de columnas, `NoteText` y `NoteTime` |
| `StickyNotesSntTests.cs` | 328 | 10 · CFB/OLE (secuencias pequeñas y por cadena de sectores), `.snt` en orden natural, archivo que no es CFB, decodificación UTF-16 y provider de extremo a extremo: alta, deduplicado por hash, nota editada, copias por nota, archivo inexistente y exportación |
| `CoreAndStorageTests.cs` | 298 | 15 · `config.json` (alta/corrupto/niveles), spool, retención por edad (también teclado y volcados de notas sin fila), retención por tamaño con sus filas, DPAPI, `HashHelper`, `ChromeTime` |
| `ChromiumPipelineTests.cs` | 320 | 5 · perfil Chromium sintético de extremo a extremo + `ChromiumKeyProvider` + el provider completo: la credencial repetida no vuelve a insertarse, la contraseña cambiada sí, y con `skipUnchanged = false` se repite la lectura |
| `ChromePasswordDecryptorTests.cs` | 97 | 5 · `v10` con/sin clave, clave equivocada, `v20`, blob vacío |
| `CompoundFileFixture.cs` | 244 | escriba un CFB/OLE mínimo para probar el lector `.snt` (no es una clase de prueba) |
| `LocalEventRepositoryTests.cs` | 107 | 2 · inserción → exportación → purga, `meta`, estadísticas |
| `JitterTests.cs` | 36 | 4 · límites del desfase |

## 3. Ejecutables compilados

| Qué | Ruta | Detalle |
|---|---|---|
| **Publicación (la que se instala/ejecuta)** | `…\SpywareAgent\publish\` | 31 archivos · 2,54 MB · `dotnet publish -c Release -r win-x64 --self-contained false` |
| Apphost nativo | `publish\SpywareAgent.exe` | 152.576 B, manifiesto `asInvoker` (sin UAC) |
| Ensamblado administrado | `publish\SpywareAgent.dll` | 29.184 B; `SpywareAgent.deps.json` y `.runtimeconfig.json` (`RollForward` LatestMajor) |
| DLL de los 10 proyectos | `Spyware.Core.dll`, `Spyware.Storage.dll`, `Spyware.Persistence.dll`, `Spyware.Transport.dll`, `Spyware.Maintenance.dll` y `Spyware.Collectors.{Screenshot,Clipboard,Browsers,Keyboard,Notes}.dll` | dependencias del agente (con su `.pdb`) |
| Dependencias NuGet | `publish\Microsoft.Data.Sqlite.dll`, `SQLitePCLRaw.*.dll`, `e_sqlite3.dll` (1,69 MB nativo x64) | SQLite sin SQLCipher → datos en claro |
| Salida intermedia por proyecto | `src\<Proyecto>\bin\Release\net8.0-windows\` y `obj\` | la que genera `dotnet build` |
| Binarios de prueba | `tests\Spyware.UnitTests\bin\Release\net8.0-windows\Spyware.UnitTests.dll` | la que ejecuta `dotnet test` |
| Copia instalada por `--install --copy` | `%LOCALAPPDATA%\SpywareLab\SpywareAgent\bin\` | 19 archivos (sin `.pdb`); es la ruta que queda en la Run key |

## 4. Datos de ejecución (todo sin cifrar)

Raíz única: `%LOCALAPPDATA%\SpywareLab\SpywareAgent` (equivalente a `C:\Users\<usuario>\AppData\Local\SpywareLab\SpywareAgent`). Se puede cambiar con `--data-dir <ruta>`.

| Dato | Ruta | Generado por |
|---|---|---|
| Configuración | `config.json` | `ConfigLoader` (se crea en el primer arranque) |
| Latido de salud | `health.json` | `HealthCheck`, cada 60 s |
| Bitácora | `logs\agent.log` (+ `.1`, `.2`, `.3` rotados) | `FileLogger` |
| Base de datos | `storage\storage.db` (+ `-wal`, `-shm`) | `LocalEventRepository` |
| Capturas | `storage\screenshots\scr_<yyyyMMdd'T'HHmmss'Z'>_<hash8>.jpg` | `SpoolManager.SaveScreenshot` |
| Imágenes del portapapeles | `storage\clipboard\clip_<UTC>_<hash8>.png` | `SpoolManager.SaveClipboardImage` |
| Copia de texto del portapapeles | `storage\clipboard\clip_<UTC>.txt` | solo con `clipboard.saveTextCopy = true` |
| Volcado de credenciales | `storage\credentials\creds_<navegador>_<UTC>.json` | solo con `browsers.writeDumpFile = true` |
| Ráfagas de teclado | `storage\keylog\keys_<UTC>_<hash8>.txt` | solo con `keyboard.saveTextCopy = true` |
| Copias de notas | `storage\notes\note_<UTC>_<id>.txt` | solo con `notes.saveTextCopy = true` |
| Volcado de notas | `storage\notes\notes_<UTC>.json` | solo con `notes.writeDumpFile = true` |
| Lotes exportados | `storage\export\<yyyymmdd>\batch_<HHmmss_fff>_<guid>.json` | `LocalFolderExporter` |
| Copias temporales de `Login Data` | `temp\<chrome\|edge>-<guid>\` (se borra al terminar) | `ChromiumCredentialExtractor` |
| Copias temporales de `plum.sqlite` / `StickyNotes.snt` | `temp\sticky-<guid>\` (se borra al terminar) | `StickyNotesProvider` |
| Binarios instalados | `bin\` | `Installer` con `--copy` |

Fuera de ese directorio solo se toca: el valor `MicrosoftSupportAgent` de `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` y, si se pide, `%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup\MicrosoftSupportAgent.lnk`. El mutex se llama `SpywareLab.SpywareAgent.<8 bytes SHA-256 del mutexSeed>` bajo `Global\` con repliegue a `Local\`. El keylogger no escribe nada fuera de la carpeta de datos: el hook es solo memoria.

## 5. Herramientas instaladas en esta máquina

| Componente | Ruta |
|---|---|
| SDK .NET 10.0.402 (ya estaba) | `C:\Program Files\dotnet\` |
| SDK .NET 8.0.426 | `%LOCALAPPDATA%\Microsoft\dotnet\sdk\8.0.426\` |
| Runtime .NET 8.0.32 (base + Windows Desktop) | `%LOCALAPPDATA%\Microsoft\dotnet\shared\Microsoft.NETCore.App\8.0.32\` y `…\Microsoft.WindowsDesktop.App\8.0.32\` |
| Caché NuGet | `%USERPROFILE%\.nuget\packages\` |

Instalación de .NET 8 sin permisos administrativos:

```powershell
Invoke-WebRequest https://dot.net/v1/dotnet-install.ps1 -OutFile $env:TEMP\dotnet-install.ps1
& $env:TEMP\dotnet-install.ps1 -Channel 8.0 -InstallDir "$env:LOCALAPPDATA\Microsoft\dotnet"
```
