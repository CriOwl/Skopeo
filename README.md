# SpywareAgent — spyware/monitor local para Windows 10-11

## Build

```
dotnet build SpywareAgent.sln -c Release
dotnet publish src/Spyware.Agent/Spyware.Agent.csproj -c Release -r win-x64 --self-contained false -o publish
```

## Ejecutar

```
publish\SpywareAgent.exe --run-once                          # ciclo completo de prueba
publish\SpywareAgent.exe --run-once --key-seconds 10         # escucha de teclado 10 s
publish\SpywareAgent.exe --run-once --notes-file plum.sqlite # Sticky Notes de demostración
publish\SpywareAgent.exe --install --copy --start
publish\SpywareAgent.exe --status
publish\SpywareAgent.exe --uninstall --remove-data
```

## Dónde queda todo

| Dato | Ruta |
|---|---|
| Configuración (claro) | `%LOCALAPPDATA%\SpywareLab\SpywareAgent\config.json` |
| Base de datos SQLite (claro) | `...\storage\storage.db` |
| Capturas JPEG | `...\storage\screenshots\scr_<UTC>_<hash>.jpg` |
| Imágenes del portapapeles | `...\storage\clipboard\clip_<UTC>_<hash>.png` |
| Volcados de credenciales | `...\storage\credentials\` (solo con `writeDumpFile`) |
| Ráfagas de teclado (texto) | `...\storage\keylog\keys_<UTC>_<hash>.txt` (solo con `keyboard.saveTextCopy`) |
| Copias de notas | `...\storage\notes\note_<UTC>_<id>.txt` (con `notes.saveTextCopy`) y `notes_<UTC>.json` (con `notes.writeDumpFile`) |
| Archivo/exportación local | `...\storage\export\<yyyymmdd>\batch_*.json` |
| Copias temporales de `Login Data` y de `plum.sqlite` | `...\temp\` (se borran al terminar) |
| Log de texto | `...\logs\agent.log` |

