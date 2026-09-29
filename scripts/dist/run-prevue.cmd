@echo off
rem Run Prevue Guide with run-prevue.ps1. Windows does not run PowerShell scripts by default, so this file starts the
rem script for this run only. It has the same options as run-prevue.ps1. Use run-prevue.cmd --help to see them.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-prevue.ps1" %*
exit /b %errorlevel%
