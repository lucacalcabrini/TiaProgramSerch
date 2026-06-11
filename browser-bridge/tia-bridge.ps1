<#
.SYNOPSIS
    TIA Var Analyzer - Bridge Openness locale.

.DESCRIPTION
    Piccolo server HTTP locale (solo 127.0.0.1) che permette a TIA Var Analyzer
    (il file HTML aperto nel browser) di leggere direttamente da un progetto
    TIA Portal aperto, senza passare dal PDF.

    Espone:
      GET /ping              -> stato del bridge
      GET /projects          -> elenco progetti TIA Portal aperti
      GET /export?pid=<id>   -> esporta i blocchi del progetto e restituisce
                                un "bundle" JSON gia' analizzato (VS_Pos + APP)
      GET /raw?pid=<id>&block=<nome>  -> DEBUG: XML SimaticML grezzo di un blocco
                                (serve per calibrare il parser sul progetto reale)

    Tutte le risposte sono JSON con header CORS aperti (Access-Control-Allow-Origin: *)
    cosi' l'HTML aperto da file:// puo' chiamarlo.

.PARAMETER Port
    Porta TCP locale (default 8731).

.PARAMETER TiaVersion
    Versione di TIA Portal (default V18). Determina il path della DLL Openness.

.PARAMETER Mock
    Avvia in modalita' finta: NON carica Openness, restituisce dati di test.
    Serve per provare tutta l'interfaccia HTML senza TIA installato.

.EXAMPLE
    .\tia-bridge.ps1
    .\tia-bridge.ps1 -Mock
    .\tia-bridge.ps1 -Port 8731 -TiaVersion V18
#>

[CmdletBinding()]
param(
    [int]    $Port        = 8731,
    [string] $TiaVersion  = 'V18',
    [switch] $Mock
)

$ErrorActionPreference = 'Stop'
$BridgeVersion = '1.0'

# Lingue da preferire quando un commento e' multilingua (prima trovata vince)
$PreferredLangs = @('it-IT','it','en-US','en')

# ============================================================================
#  OPENNESS — caricamento assembly
# ============================================================================

function Get-OpennessDllPath {
    param([string]$Version)
    # Path standard di installazione della PublicAPI Openness
    $candidates = @(
        "C:\Program Files\Siemens\Automation\Portal $Version\PublicAPI\$Version\Siemens.Engineering.dll",
        "C:\Program Files\Siemens\Automation\Portal $Version\PublicAPI\Siemens.Engineering.dll"
    )
    foreach ($c in $candidates) { if (Test-Path $c) { return $c } }
    return $null
}

function Initialize-Openness {
    param([string]$Version)

    $dll = Get-OpennessDllPath -Version $Version
    if (-not $dll) {
        throw "Siemens.Engineering.dll non trovata per TIA $Version. " +
              "Verifica che TIA Portal $Version sia installato (cartella PublicAPI)."
    }
    $script:OpennessDir = Split-Path $dll -Parent

    # Resolver: quando il CLR cerca una dipendenza Siemens.* la carica dalla
    # cartella PublicAPI (requisito Openness).
    $resolveDir = $script:OpennessDir
    $onResolve = [System.ResolveEventHandler]{
        param($sender, $e)
        $name = (New-Object System.Reflection.AssemblyName($e.Name)).Name
        $path = Join-Path $resolveDir ($name + '.dll')
        if (Test-Path $path) { return [System.Reflection.Assembly]::LoadFrom($path) }
        return $null
    }
    [System.AppDomain]::CurrentDomain.add_AssemblyResolve($onResolve)

    [System.Reflection.Assembly]::LoadFrom($dll) | Out-Null
    Write-Host "[OK] Openness $Version caricata da: $dll" -ForegroundColor Green
}

# ============================================================================
#  OPENNESS — progetti aperti
# ============================================================================

function Get-OpenProjects {
    if ($Mock) {
        return @(
            [ordered]@{ pid = 11111; name = 'Linea_Imballaggio.ap18'; path = 'D:\TIA\Linea_Imballaggio\Linea_Imballaggio.ap18'; tiaVersion = 'V18' },
            [ordered]@{ pid = 22222; name = 'Cella_Robot_07.ap18';    path = 'D:\TIA\Cella_Robot_07\Cella_Robot_07.ap18';       tiaVersion = 'V18' }
        )
    }

    $result = @()
    $procs = [Siemens.Engineering.TiaPortal]::GetProcesses()
    foreach ($p in $procs) {
        $projPath = $null
        try { if ($p.ProjectPath) { $projPath = $p.ProjectPath.FullName } } catch {}
        $name = if ($projPath) { Split-Path $projPath -Leaf } else { '(nessun progetto aperto)' }
        $result += [ordered]@{
            pid        = $p.Id
            name       = $name
            path       = $projPath
            tiaVersion = $TiaVersion
        }
    }
    return $result
}

# ============================================================================
#  OPENNESS — accesso al PLC software e ai blocchi
# ============================================================================

function Get-PlcSoftwares {
    param($project)
    $list = @()
    foreach ($device in $project.Devices) {
        foreach ($di in $device.DeviceItems) {
            Get-PlcSoftwareFromDeviceItem -DeviceItem $di -Acc ([ref]$list)
        }
    }
    return $list
}

# PowerShell 5.1 non sa chiamare metodi generici ($x.GetService[T]()), quindi
# invochiamo IEngineeringServiceProvider.GetService<SoftwareContainer> via reflection.
function Invoke-GetSoftwareContainer {
    param($DeviceItem)
    $svcType  = [Siemens.Engineering.HW.Features.SoftwareContainer]
    $provType = [Siemens.Engineering.IEngineeringServiceProvider]
    $method   = $provType.GetMethod('GetService')
    $generic  = $method.MakeGenericMethod($svcType)
    return $generic.Invoke($DeviceItem, $null)
}

function Get-PlcSoftwareFromDeviceItem {
    param($DeviceItem, [ref]$Acc)
    try {
        $container = Invoke-GetSoftwareContainer -DeviceItem $DeviceItem
        if ($container -and $container.Software -is [Siemens.Engineering.SW.PlcSoftware]) {
            $Acc.Value += $container.Software
        }
    } catch {}
    foreach ($child in $DeviceItem.DeviceItems) {
        Get-PlcSoftwareFromDeviceItem -DeviceItem $child -Acc $Acc
    }
}

function Get-AllBlocks {
    param($group, [ref]$Acc)
    foreach ($b in $group.Blocks) { $Acc.Value += $b }
    foreach ($g in $group.Groups) { Get-AllBlocks -group $g -Acc $Acc }
}

# ============================================================================
#  SIMATIC ML — ricostruzione operandi e estrazione
# ============================================================================

# Cerca, ricorsivamente per LocalName (namespace-agnostic), i figli con quel nome.
function Get-ChildByName {
    param($node, [string]$localName)
    $out = @()
    foreach ($c in $node.ChildNodes) {
        if ($c.LocalName -eq $localName) { $out += $c }
    }
    return $out
}

# Ricostruisce il testo di un nodo <Access> nella sintassi TIA.
#   <Access Scope="LiteralConstant">  -> "3"
#   <Access Scope="GlobalVariable">   -> "DB".axes[Asse111].vsPos[3]
#   ...
function Convert-AccessToText {
    param($access)
    if (-not $access) { return '' }
    $scope = $access.GetAttribute('Scope')

    # Costante letterale / tipizzata
    if ($scope -like '*Constant*') {
        $cv = $access.SelectNodes('.//*[local-name()="ConstantValue"]')
        if ($cv -and $cv.Count -gt 0) { return $cv[0].InnerText.Trim() }
        return $access.InnerText.Trim()
    }

    # Simbolo: catena di <Component Name="...">, con eventuali indici array
    $symbol = (Get-ChildByName -node $access -localName 'Symbol') | Select-Object -First 1
    if ($symbol) {
        $parts = @()
        foreach ($comp in (Get-ChildByName -node $symbol -localName 'Component')) {
            $cname = $comp.GetAttribute('Name')
            # indici array: <Access> figli del Component
            $idxAccesses = Get-ChildByName -node $comp -localName 'Access'
            if ($idxAccesses.Count -gt 0) {
                $idxTexts = @()
                foreach ($ia in $idxAccesses) { $idxTexts += (Convert-AccessToText -access $ia) }
                $cname = $cname + '[' + ($idxTexts -join ',') + ']'
            }
            $parts += $cname
        }
        return ($parts -join '.')
    }

    return $access.InnerText.Trim()
}

# Estrae il primo testo "utile" da un nodo MultilingualText (commento / titolo).
function Get-MultilingualText {
    param($node)
    if (-not $node) { return '' }
    $items = $node.SelectNodes('.//*[local-name()="MultilingualTextItem"]')
    if (-not $items -or $items.Count -eq 0) {
        return ($node.InnerText -replace '\s+',' ').Trim()
    }
    # prova le lingue preferite
    foreach ($lang in $PreferredLangs) {
        foreach ($it in $items) {
            $attr = $it.SelectSingleNode('.//*[local-name()="AttributeList"]/*[local-name()="Culture"]')
            $txt  = $it.SelectSingleNode('.//*[local-name()="AttributeList"]/*[local-name()="Text"]')
            if ($attr -and $attr.InnerText -eq $lang -and $txt) {
                $t = ($txt.InnerText -replace '\s+',' ').Trim()
                if ($t) { return $t }
            }
        }
    }
    # fallback: primo non vuoto
    foreach ($it in $items) {
        $txt = $it.SelectSingleNode('.//*[local-name()="AttributeList"]/*[local-name()="Text"]')
        if ($txt) {
            $t = ($txt.InnerText -replace '\s+',' ').Trim()
            if ($t) { return $t }
        }
    }
    return ''
}

# Determina l'operazione (=, S, R, contatto...) dal contesto di un Access in LAD/FBD.
function Get-OperationFromContext {
    param($access)
    $n = $access.ParentNode
    $depth = 0
    while ($n -and $depth -lt 6) {
        if ($n.LocalName -eq 'Part') {
            $pn = $n.GetAttribute('Name')
            switch -Wildcard ($pn) {
                'Coil'    { return '=' }
                'SCoil'   { return 'S' }
                'SetCoil' { return 'S' }
                'RCoil'   { return 'R' }
                'ResetCoil'{return 'R' }
                'Contact' { return '' }
                default   { return $pn }
            }
        }
        $n = $n.ParentNode
        $depth++
    }
    return ''
}

# Trova il titolo del Network/segmento contenente l'Access.
function Get-SegmentTitle {
    param($access)
    $n = $access.ParentNode
    $depth = 0
    while ($n -and $depth -lt 30) {
        if ($n.LocalName -eq 'SW.Blocks.CompileUnit' -or $n.LocalName -like '*CompileUnit*') {
            # cerca il titolo nei MultilingualText del CompileUnit
            $title = $n.SelectSingleNode('.//*[local-name()="MultilingualText" and @CompositionName="Title"]')
            if ($title) {
                $t = Get-MultilingualText -node $title
                if ($t) { return $t }
            }
            return ''
        }
        $n = $n.ParentNode
        $depth++
    }
    return ''
}

# Analizza l'XML SimaticML di UN blocco, restituisce righe VS e APP.
function Get-MatchesFromBlockXml {
    param([xml]$xml, [string]$blockName)

    $vsRows  = @()
    $appRows = @()

    # Tutti i nodi <Access> ovunque nel blocco
    $accesses = $xml.SelectNodes('//*[local-name()="Access"]')
    if (-not $accesses) { return @{ vs = $vsRows; app = $appRows } }

    # Regex coerenti con quelle del parser PDF dell'HTML
    $vsRe  = [regex]'(?i)\[([^\]]+)\]\s*\.\s*vs_?pos\[(\d+)\]'
    $appRe = [regex]'(?i)additionalPiecePresence([1-8])\b'

    $lineCounter = 0
    foreach ($acc in $accesses) {
        # consideriamo solo gli access "di primo livello" (non gli indici annidati)
        if ($acc.ParentNode -and $acc.ParentNode.LocalName -eq 'Component') { continue }

        $text = Convert-AccessToText -access $acc
        if (-not $text) { continue }
        $lineCounter++

        $seg = Get-SegmentTitle -access $acc
        $op  = Get-OperationFromContext -access $acc

        # VS_Pos
        foreach ($m in $vsRe.Matches($text)) {
            $vsRows += [ordered]@{
                asse       = ($m.Groups[1].Value.Trim() -replace '^"|"$','')
                indice     = [int]$m.Groups[2].Value
                blocco     = $blockName
                segmento   = $seg
                operazione = $op
                commento   = ''
                testo      = $text
                linea      = $lineCounter
            }
        }
        # additionalPiecePresence
        foreach ($m in $appRe.Matches($text)) {
            $appRows += [ordered]@{
                numero     = [int]$m.Groups[1].Value
                blocco     = $blockName
                segmento   = $seg
                operazione = $op
                commento   = ''
                testo      = $text
                linea      = $lineCounter
            }
        }
    }

    return @{ vs = $vsRows; app = $appRows }
}

# ============================================================================
#  EXPORT — costruisce il bundle completo
# ============================================================================

function Export-ProjectBundle {
    param([int]$ProcId)

    if ($Mock) { return Get-MockBundle }

    $procs = [Siemens.Engineering.TiaPortal]::GetProcesses()
    $target = $procs | Where-Object { $_.Id -eq $ProcId } | Select-Object -First 1
    if (-not $target) { throw "Nessun processo TIA Portal con pid $ProcId (forse e' stato chiuso?)." }

    Write-Host "[..] Connessione al processo TIA pid=$ProcId (conferma il popup in TIA Portal)..." -ForegroundColor Yellow
    $portal = $target.Attach()
    try {
        $project = $portal.Projects | Select-Object -First 1
        if (-not $project) { throw "Il processo TIA selezionato non ha un progetto aperto." }
        $projName = $project.Name

        $plcs = Get-PlcSoftwares -project $project
        if ($plcs.Count -eq 0) { throw "Nessun PLC trovato nel progetto." }

        $tmp = Join-Path $env:TEMP ("tia-bridge-" + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $tmp -Force | Out-Null

        $allVs = @(); $allApp = @()
        $blockCount = 0; $failCount = 0

        foreach ($plc in $plcs) {
            $blocks = @()
            Get-AllBlocks -group $plc.BlockGroup -Acc ([ref]$blocks)
            foreach ($blk in $blocks) {
                $blockCount++
                $safe = ($blk.Name -replace '[\\/:*?"<>|]','_')
                $file = Join-Path $tmp ("$safe.xml")
                try {
                    if (Test-Path $file) { Remove-Item $file -Force }
                    $blk.Export([System.IO.FileInfo]$file, [Siemens.Engineering.ExportOptions]::WithDefaults)
                    [xml]$bx = Get-Content -Raw -Path $file
                    $res = Get-MatchesFromBlockXml -xml $bx -blockName $blk.Name
                    $allVs  += $res.vs
                    $allApp += $res.app
                } catch {
                    $failCount++
                    Write-Host "    [skip] blocco '$($blk.Name)': $($_.Exception.Message)" -ForegroundColor DarkYellow
                }
            }
        }

        try { Remove-Item $tmp -Recurse -Force } catch {}

        Write-Host "[OK] Esportati $blockCount blocchi ($failCount saltati) - VS=$($allVs.Count) APP=$($allApp.Count)" -ForegroundColor Green

        return [ordered]@{
            tool          = 'tia-bridge'
            bundleVersion = 1
            exportedAt    = (Get-Date).ToString('s')
            project       = [ordered]@{ name = $projName; path = ($project.Path.FullName); tiaVersion = $TiaVersion }
            stats         = [ordered]@{ blocks = $blockCount; skipped = $failCount; vs = $allVs.Count; app = $allApp.Count }
            vs            = @($allVs)
            app           = @($allApp)
        }
    }
    finally {
        # Nota: disporre l'handle NON chiude TIA Portal dell'utente, rilascia solo la connessione.
        try { $portal.Dispose() } catch {}
    }
}

function Get-RawBlockXml {
    param([int]$ProcId, [string]$BlockName)
    if ($Mock) { return "<Mock><Block Name='$BlockName'/></Mock>" }

    $procs  = [Siemens.Engineering.TiaPortal]::GetProcesses()
    $target = $procs | Where-Object { $_.Id -eq $ProcId } | Select-Object -First 1
    if (-not $target) { throw "Nessun processo TIA con pid $ProcId." }
    $portal = $target.Attach()
    try {
        $project = $portal.Projects | Select-Object -First 1
        $plcs = Get-PlcSoftwares -project $project
        foreach ($plc in $plcs) {
            $blocks = @()
            Get-AllBlocks -group $plc.BlockGroup -Acc ([ref]$blocks)
            $blk = $blocks | Where-Object { $_.Name -eq $BlockName } | Select-Object -First 1
            if ($blk) {
                $tmp = Join-Path $env:TEMP ("tia-raw-" + [guid]::NewGuid().ToString('N') + '.xml')
                $blk.Export([System.IO.FileInfo]$tmp, [Siemens.Engineering.ExportOptions]::WithDefaults)
                $content = Get-Content -Raw -Path $tmp
                Remove-Item $tmp -Force
                return $content
            }
        }
        throw "Blocco '$BlockName' non trovato."
    }
    finally { try { $portal.Dispose() } catch {} }
}

function Get-MockBundle {
    return [ordered]@{
        tool          = 'tia-bridge'
        bundleVersion = 1
        exportedAt    = (Get-Date).ToString('s')
        project       = [ordered]@{ name = 'Linea_Imballaggio'; path = 'D:\TIA\Linea_Imballaggio\Linea_Imballaggio.ap18'; tiaVersion = 'V18' }
        stats         = [ordered]@{ blocks = 3; skipped = 0; vs = 3; app = 2 }
        vs            = @(
            [ordered]@{ asse='Asse111'; indice=1; blocco='FC100_Movimenti'; segmento='Network 2: Posizionamento'; operazione='='; commento='Posizione di prelievo'; testo='axes[Asse111].vsPos[1]'; linea=12 },
            [ordered]@{ asse='Asse105'; indice=3; blocco='FC100_Movimenti'; segmento='Network 4: Deposito';      operazione='S'; commento='';                     testo='axes[Asse105].vsPos[3]'; linea=28 },
            [ordered]@{ asse='Gripper'; indice=2; blocco='FB20_Pinza';      segmento='Network 1';                operazione='='; commento='Apertura pinza';        testo='axes[Gripper].vsPos[2]'; linea=5 }
        )
        app           = @(
            [ordered]@{ numero=2; blocco='FB20_Pinza';      segmento='Network 3'; operazione='='; commento='Pezzo extra rilevato'; testo='"DB_IO".additionalPiecePresence2'; linea=33 },
            [ordered]@{ numero=5; blocco='FC100_Movimenti'; segmento='Network 6'; operazione='';  commento='';                      testo='"DB_IO".additionalPiecePresence5'; linea=51 }
        )
    }
}

# ============================================================================
#  HTTP — server TCP minimale (no admin / no urlacl)
# ============================================================================

function Write-HttpResponse {
    param($stream, [string]$status, [string]$contentType, [string]$body)
    $bodyBytes = [System.Text.Encoding]::UTF8.GetBytes($body)
    $head = "HTTP/1.1 $status`r`n" +
            "Content-Type: $contentType; charset=utf-8`r`n" +
            "Content-Length: $($bodyBytes.Length)`r`n" +
            "Access-Control-Allow-Origin: *`r`n" +
            "Access-Control-Allow-Methods: GET, OPTIONS`r`n" +
            "Access-Control-Allow-Headers: *`r`n" +
            "Cache-Control: no-store`r`n" +
            "Connection: close`r`n`r`n"
    $headBytes = [System.Text.Encoding]::ASCII.GetBytes($head)
    $stream.Write($headBytes, 0, $headBytes.Length)
    $stream.Write($bodyBytes, 0, $bodyBytes.Length)
    $stream.Flush()
}

function Write-Json {
    param($stream, $obj, [string]$status = '200 OK')
    $json = $obj | ConvertTo-Json -Depth 12
    Write-HttpResponse -stream $stream -status $status -contentType 'application/json' -body $json
}

function ConvertFrom-Query {
    param([string]$query)   # es. "pid=123&block=FC100"
    $h = @{}
    if ($query.StartsWith('?')) { $query = $query.Substring(1) }
    if (-not $query) { return $h }
    foreach ($pair in $query.Split('&')) {
        $kv = $pair.Split('=', 2)
        $k = [System.Uri]::UnescapeDataString($kv[0])
        $v = if ($kv.Count -gt 1) { [System.Uri]::UnescapeDataString($kv[1]) } else { '' }
        $h[$k] = $v
    }
    return $h
}

function Start-Bridge {
    $listener = New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Loopback, $Port)
    $listener.Start()
    Write-Host ""
    Write-Host "============================================================" -ForegroundColor Cyan
    Write-Host "  TIA Var Analyzer - Bridge Openness v$BridgeVersion" -ForegroundColor Cyan
    Write-Host "  In ascolto su:  http://127.0.0.1:$Port" -ForegroundColor Cyan
    if ($Mock) { Write-Host "  MODALITA' MOCK (dati finti, Openness non caricata)" -ForegroundColor Magenta }
    else       { Write-Host "  TIA Portal:    $TiaVersion" -ForegroundColor Cyan }
    Write-Host "  Premi CTRL+C per fermare il bridge." -ForegroundColor DarkGray
    Write-Host "============================================================" -ForegroundColor Cyan
    Write-Host ""

    while ($true) {
        $client = $listener.AcceptTcpClient()
        $stream = $client.GetStream()
        try {
            $reader = New-Object System.IO.StreamReader($stream, [System.Text.Encoding]::ASCII)
            $requestLine = $reader.ReadLine()
            if (-not $requestLine) { continue }
            # consuma gli header
            while ($true) { $h = $reader.ReadLine(); if ($null -eq $h -or $h -eq '') { break } }

            $parts  = $requestLine.Split(' ')
            $method = $parts[0]
            $rawUrl = if ($parts.Count -gt 1) { $parts[1] } else { '/' }

            $uri   = [System.Uri]("http://127.0.0.1$rawUrl")
            $path  = $uri.AbsolutePath
            $qs    = ConvertFrom-Query -query $uri.Query

            Write-Host ("  {0}  {1}" -f $method, $rawUrl) -ForegroundColor DarkGray

            if ($method -eq 'OPTIONS') {
                Write-HttpResponse -stream $stream -status '204 No Content' -contentType 'text/plain' -body ''
                continue
            }

            switch ($path) {
                '/ping' {
                    Write-Json -stream $stream -obj ([ordered]@{
                        ok = $true; tool = 'tia-bridge'; version = $BridgeVersion
                        mock = [bool]$Mock; tiaVersion = $TiaVersion
                    })
                }
                '/projects' {
                    try {
                        $projects = @(Get-OpenProjects)
                        Write-Json -stream $stream -obj ([ordered]@{ ok = $true; projects = $projects })
                    } catch {
                        Write-Json -stream $stream -status '500 Internal Server Error' -obj ([ordered]@{ ok = $false; error = $_.Exception.Message })
                    }
                }
                '/export' {
                    try {
                        $procId = [int]$qs['pid']
                        $bundle = Export-ProjectBundle -ProcId $procId
                        Write-Json -stream $stream -obj $bundle
                    } catch {
                        Write-Json -stream $stream -status '500 Internal Server Error' -obj ([ordered]@{ ok = $false; error = $_.Exception.Message })
                    }
                }
                '/raw' {
                    try {
                        $procId = [int]$qs['pid']
                        $xml = Get-RawBlockXml -ProcId $procId -BlockName $qs['block']
                        Write-HttpResponse -stream $stream -status '200 OK' -contentType 'application/xml' -body $xml
                    } catch {
                        Write-Json -stream $stream -status '500 Internal Server Error' -obj ([ordered]@{ ok = $false; error = $_.Exception.Message })
                    }
                }
                default {
                    Write-Json -stream $stream -status '404 Not Found' -obj ([ordered]@{ ok = $false; error = "endpoint sconosciuto: $path" })
                }
            }
        }
        catch {
            Write-Host "  [errore richiesta] $($_.Exception.Message)" -ForegroundColor Red
        }
        finally {
            try { $stream.Close() } catch {}
            try { $client.Close() } catch {}
        }
    }
}

# ============================================================================
#  MAIN
# ============================================================================

try {
    if (-not $Mock) {
        Initialize-Openness -Version $TiaVersion
    } else {
        Write-Host "[i] Avvio in modalita' MOCK: Openness non verra' caricata." -ForegroundColor Magenta
    }
    Start-Bridge
}
catch {
    Write-Host ""
    Write-Host "ERRORE FATALE: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host $_.ScriptStackTrace -ForegroundColor DarkRed
    exit 1
}
