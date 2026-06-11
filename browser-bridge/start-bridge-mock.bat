@echo off
REM ============================================================
REM  TIA Var Analyzer - Bridge in modalita' MOCK (dati finti)
REM  Per provare l'interfaccia "Leggi da progetto TIA" SENZA
REM  avere TIA Portal installato/aperto. Nessuna Openness.
REM ============================================================
title TIA Var Analyzer - Bridge (MOCK)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0tia-bridge.ps1" -Mock %*
echo.
echo Bridge (mock) terminato. Premi un tasto per chiudere.
pause >nul
