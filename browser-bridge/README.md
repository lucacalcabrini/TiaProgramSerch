# TIA Var Analyzer — Bridge Openness

Questo bridge permette a **TIA Var Analyzer** (il file HTML) di leggere le variabili
**direttamente da un progetto TIA Portal aperto**, senza dover stampare il PDF.

È un piccolo server HTTP locale (solo `127.0.0.1`) scritto in PowerShell, che usa
**TIA Portal Openness** per esportare i blocchi del PLC e cercare `VS_Pos[n]` e
`additionalPiecePresenceN`, esattamente come fa l'analisi del PDF.

```
HTML (browser)  ──HTTP localhost──►  tia-bridge.ps1  ──Openness──►  TIA Portal V18
```

---

## Requisiti

1. **TIA Portal V18** installato (con la cartella `PublicAPI`, presente di default).
2. Il tuo utente Windows deve far parte del gruppo **`Siemens TIA Openness`**
   (creato dall'installazione di TIA). Se non ci sei: Pannello di controllo →
   Gestione utenti → aggiungi il tuo utente al gruppo, poi **logout/login**.
3. **Windows PowerShell 5.1** (già presente su Windows 10/11).

> Non serve installare nient'altro: niente Visual Studio, niente compilazione.

---

## Avvio

### Con un progetto reale
1. Apri il tuo progetto in **TIA Portal V18**.
2. Doppio click su **`start-bridge.bat`** (oppure `start-bridge-mock.bat` per la prova).
   Si apre una finestra che resta in ascolto su `http://127.0.0.1:8731`.
   **Lascia la finestra aperta** mentre usi l'analizzatore.
3. Apri **`TIA_Analisi_Variabili_v2.4.html`** nel browser.
4. Clicca **📡 Leggi da progetto TIA** (nella schermata iniziale o il pulsante **📡 TIA** in alto).
   - se è aperto **un solo** progetto → parte subito;
   - se sono aperti **più progetti** → compare il **popup di selezione**.
5. La **prima volta** TIA Portal mostra un popup di sicurezza
   («un'applicazione sta tentando di accedere…»): conferma **Sì** per consentire l'accesso.

### Prova senza TIA (mock)
Doppio click su **`start-bridge-mock.bat`**: il bridge risponde con dati finti,
così puoi vedere il popup di selezione e l'analisi senza avere TIA installato.

Da riga di comando:
```powershell
.\tia-bridge.ps1                 # progetto reale, TIA V18, porta 8731
.\tia-bridge.ps1 -Mock           # dati finti
.\tia-bridge.ps1 -Port 8750      # porta diversa
.\tia-bridge.ps1 -TiaVersion V19 # altra versione di TIA
```

> Se cambi porta, nell'HTML imposta una volta sola (console del browser, F12):
> `localStorage.setItem('tiaBridgeUrl','http://127.0.0.1:8750')`

---

## Endpoint (per debug)

| Endpoint                         | Cosa fa                                                        |
|----------------------------------|----------------------------------------------------------------|
| `GET /ping`                      | Verifica che il bridge è attivo                                |
| `GET /projects`                  | Elenco dei progetti TIA Portal aperti                          |
| `GET /export?pid=<id>`           | Esporta i blocchi e restituisce il **bundle JSON** analizzato  |
| `GET /raw?pid=<id>&block=<nome>` | **DEBUG**: XML SimaticML grezzo di un blocco                   |

---

## Calibrazione del parser (importante)

L'estrazione da SimaticML è scritta seguendo il modello standard di Openness e
riusa le stesse regex validate sul PDF, **ma va verificata sul tuo progetto reale**
perché la struttura XML cambia in base al linguaggio del blocco (SCL/LAD/FBD/KOP).

Per calibrare, esporta l'XML grezzo di un blocco che usa `vsPos`/`additionalPiecePresence`:

```
http://127.0.0.1:8731/raw?pid=<PID>&block=<NomeBlocco>
```

(il `<PID>` lo trovi da `/projects`). Salva l'output e usalo per affinare le funzioni
`Convert-AccessToText` / `Get-MatchesFromBlockXml` in `tia-bridge.ps1`.

---

## Problemi frequenti

- **«Bridge TIA non raggiungibile»** → la finestra del bridge non è aperta, oppure
  porta diversa. Avvia `start-bridge.bat`.
- **«Nessun progetto TIA Portal aperto»** → apri un progetto in TIA e riprova.
- **Errore caricamento Openness / DLL non trovata** → TIA V18 non installato o
  percorso `PublicAPI` diverso; controlla `-TiaVersion`.
- **Errore di accesso / niente popup di sicurezza** → manca l'appartenenza al gruppo
  `Siemens TIA Openness` (vedi Requisiti). Dopo l'aggiunta serve logout/login.
- **Molti blocchi «saltati»** in console → blocchi non compilabili/protetti: vengono
  ignorati e conteggiati, l'analisi prosegue sugli altri.
