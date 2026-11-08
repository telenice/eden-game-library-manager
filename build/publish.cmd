@echo off
rem Double-click or run from cmd.exe: builds the Windows release (see publish.ps1 for options).
rem Example: build\publish.cmd -Runtime win-x64,win-arm64
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0publish.ps1" %*
if errorlevel 1 (
  echo.
  echo Build failed.
  if "%~1"=="" pause
  exit /b 1
)
if "%~1"=="" pause
