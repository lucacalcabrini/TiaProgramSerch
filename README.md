# TIA Var Analyzer

Strumento per analizzare l'uso di **`VS_Pos[n]`** e **`additionalPiecePresenceN`** nei programmi
TIA Portal. Estrae dove queste variabili sono usate, in quale blocco/segmento, con commento e
operazione, e permette di confrontare versioni (Excel ↔ programma).

Tre modi per fornire i dati, **stessa interfaccia**:

| Sorgente | Come |
|----------|------|
| 📄 **PDF** | Stampa del programma da TIA Portal (analisi completa offline) |
| 📊 **Excel** | Re-import di un export precedente di questo tool |
| 📡 **Progetto TIA** | Lettura **diretta** da un progetto TIA Portal aperto, via Openness |

---

## 1) App desktop (consigliata) — `app/`

App Windows (**C# / .NET Framework 4.8 + WebView2**) che ingloba l'interfaccia e parla con
**TIA Openness** internamente: niente bridge, niente browser, niente popup di sicurezza del
browser. **Auto-update all'avvio** tramite Velopack.

### Requisiti
- **TIA Portal V18** installato (per la modalità "Progetto TIA").
- Utente nel gruppo Windows **`Siemens TIA Openness`** (logout/login dopo averlo aggiunto).
- **WebView2 Runtime** (preinstallato su Win11; evergreen).
- Per buildare: **.NET SDK 8+** (`winget install Microsoft.DotNet.SDK.8`). Nessun Visual Studio.

### Build & avvio (da sviluppatore)
```powershell
# prova UI senza TIA (dati finti)
app\run-mock.bat
# uso reale: apri prima un progetto in TIA Portal V18, poi
app\run.bat
```
oppure manualmente:
```powershell
dotnet build app\TiaVarAnalyzer\TiaVarAnalyzer.csproj -c Debug
```

### Release (installer + auto-update)
La build **non** può girare in CI (i runner GitHub non hanno la DLL Openness): si fa **in locale**
su una macchina con TIA V18.
```powershell
app\build-release.ps1                        # genera app\Releases\Setup.exe
app\build-release.ps1 -Upload -Token <ghp>   # pubblica su GitHub Releases (auto-update)
```
L'app si auto-aggiorna **solo se installata via `Setup.exe`** di Velopack.

---

## 2) Modalità browser + bridge — `browser-bridge/`

Alternativa senza app: si apre l'interfaccia in un browser e un piccolo **bridge PowerShell**
(`tia-bridge.ps1`) fa da ponte verso Openness su `http://127.0.0.1:8731`.
Stesso file UI dell'app (`app/TiaVarAnalyzer/web/index.html`): rileva da solo se è dentro l'app
(in-process) o nel browser (usa il bridge). Vedi `browser-bridge/README.md`.

---

## Struttura

```
app/                      App desktop C# + WebView2 (prodotto principale)
  TiaVarAnalyzer/         sorgenti, Openness/, web/index.html (UI)
  build-release.ps1       build + packaging Velopack
browser-bridge/           Bridge PowerShell per la modalità browser (fallback)
TIA_Analisi_Variabili_v2.4.html   HTML standalone legacy (canale auto-update pubblico)
version.json              manifest auto-update dell'HTML legacy
```

> **Calibrazione parser**: l'estrazione dal SimaticML segue il modello standard Openness e riusa
> le stesse regex del parser PDF; va verificata sui blocchi reali (SCL/LAD/FBD). Usa l'azione di
> debug `raw` (XML grezzo di un blocco) per affinare `SimaticMlParser` sul progetto reale.
