@echo off
setlocal
set "BIN=%~dp0..\publish\SpywareAgent.exe"
if not exist "%BIN%" set "BIN=%LOCALAPPDATA%\SpywareLab\SpywareAgent\bin\SpywareAgent.exe"

echo Deteniendo el agente, quitando la Run key y borrando datos locales ...
"%BIN%" --uninstall --remove-data --startup-folder --verbose
exit /b %ERRORLEVEL%
