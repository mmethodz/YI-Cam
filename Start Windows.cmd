@echo off
if not exist "%~dp0dist\YI-Local\YiLocal.Windows.exe" (
  echo Run Build Windows.cmd first. See README.md for prerequisites.
  pause
  exit /b 1
)
start "YI Local" "%~dp0dist\YI-Local\YiLocal.Windows.exe"
