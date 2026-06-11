# TIA Var Analyzer

Strumento per analizzare l'uso di **`VS_Pos[n]`** e **`additionalPiecePresenceN`** nei programmi
TIA Portal. Estrae dove queste variabili sono usate, in quale blocco/segmento, con commento e
operazione, e permette di confrontare versioni (Excel ↔ programma).

Due modi per fornire i dati, **stessa interfaccia**:

| Sorgente | Come |
|----------|------|
| 📂 **Progetto TIA** | Apri un **`.ap18`** o un archivio **`.zap18`/`.zap`**: l'app avvia TIA Portal **headless** in background (Openness), esporta i blocchi e li analizza. Non serve avere TIA aperto. |
| 📊 **Excel** | Re-import di un export precedente di questo tool (per il confronto/diff) |

Gli archivi `.zap` vengono estratti in una cartella temporanea: **il file originale non viene mai
modificato**. Archivi di versioni TIA precedenti vengono aggiornati automaticamente sulla sola
copia temporanea.

---

## App desktop — `app/`

App Windows (**C# / .NET Framework 4.8 + WebView2**) con **TIA Openness** in-process e
**auto-update all'avvio** tramite Velopack.

### Requisiti
- **TIA Portal V18** installato (l'apertura headless usa l'engine V18).
- Utente nel gruppo Windows **`Siemens TIA Openness`** (logout/login dopo averlo aggiunto) —
  senza, Windows mostra una richiesta di conferma a ogni avvio dell'istanza headless.
- **WebView2 Runtime** (preinstallato su Win11; evergreen).
- Per buildare: **.NET SDK 8+** (`winget install Microsoft.DotNet.SDK.8`). Nessun Visual Studio.

### Build & avvio (da sviluppatore)
```powershell
# prova UI senza TIA (dati finti)
app\run-mock.bat
# uso reale
app\run.bat
```
oppure manualmente:
```powershell
dotnet build app\TiaVarAnalyzer\TiaVarAnalyzer.csproj -c Debug
```

### Export da riga di comando (batch / debug)
```powershell
TiaVarAnalyzer.exe --export "C:\percorso\Progetto.zap18" --out "C:\temp\bundle.json"
```
Scrive il bundle JSON dell'analisi e un log testuale (`<out>.log`). Exit code 0 = ok.

### Release (installer + auto-update)
La build **non** può girare in CI (i runner GitHub non hanno la DLL Openness): si fa **in locale**
su una macchina con TIA V18.
```powershell
app\build-release.ps1                        # genera app\Releases\Setup.exe
app\build-release.ps1 -Upload -Token <ghp>   # pubblica su GitHub Releases (auto-update)
```
L'app si auto-aggiorna **solo se installata via `Setup.exe`** di Velopack.

---

## Componenti legacy

| Percorso | Cosa |
|----------|------|
| `TIA_Analisi_Variabili_v2.4.html` | HTML standalone (analisi da **PDF** stampato da TIA); canale auto-update `version.json` |
| `browser-bridge/` | Bridge PowerShell per leggere da un progetto TIA **aperto** dal browser (sostituito dall'app) |

> **Roadmap**: import diretto di blocchi **SimaticML (.xml)** esportati manualmente da TIA, così
> l'analisi funziona anche senza TIA Portal installato.

> **Calibrazione parser**: l'estrazione dal SimaticML riusa le stesse regex del parser PDF; per
> affinarla su blocchi reali usa l'azione di debug `raw` (XML grezzo di un blocco).
