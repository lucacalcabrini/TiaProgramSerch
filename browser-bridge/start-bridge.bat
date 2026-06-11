@echo off
REM ============================================================
REM  TIA Var Analyzer - avvio Bridge Openness (progetto reale)
REM  Doppio click per avviare. Lascia la finestra aperta:
REM  finche' resta aperta, TIA Var Analyzer puo' leggere da TIA.
REM ============================================================
title TIA Var Analyzer - Bridge Openness
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0tia-bridge.ps1" %*
echo.
echo Bridge terminato. Premi un tasto per chiudere.
pause >nul
