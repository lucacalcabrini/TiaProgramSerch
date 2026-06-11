@echo off
REM Avvia l'app in modalita' MOCK (dati finti, niente TIA/Openness) per provare la UI.
title TIA Var Analyzer (build + run MOCK)
pushd "%~dp0TiaVarAnalyzer"
dotnet run -c Debug -- --mock
popd
