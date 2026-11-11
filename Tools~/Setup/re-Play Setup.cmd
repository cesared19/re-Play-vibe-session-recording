@echo off
rem re-Play - Asistente de instalacion. Abre la ventana con el PowerShell que trae Windows.
powershell.exe -NoProfile -ExecutionPolicy Bypass -STA -WindowStyle Hidden -File "%~dp0re-play-setup.ps1"
if errorlevel 1 pause
