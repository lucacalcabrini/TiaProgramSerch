@echo off
REM Avvia l'app collegandosi a un progetto TIA Portal V18 reale.
REM Requisito: avere un progetto aperto in TIA e far parte del gruppo "Siemens TIA Openness".
title TIA Var Analyzer (build + run)
pushd "%~dp0TiaVarAnalyzer"
dotnet run -c Debug
popd
