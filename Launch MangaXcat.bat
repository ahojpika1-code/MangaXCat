@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Launch.ps1"
if errorlevel 1 (
  echo.
  echo MangaXcat could not be launched. Review the error above.
  pause
  exit /b 1
)
