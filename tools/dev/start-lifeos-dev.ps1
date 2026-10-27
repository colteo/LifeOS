
# LifeOS - Avvio sviluppo su Pixel 9 (Windows PowerShell 5.1+)
# Avvio normale:
# powershell -ExecutionPolicy Bypass -File .\tools\dev\start-lifeos-dev.ps1
#
# Build + install + avvio:
# powershell -ExecutionPolicy Bypass -File .\tools\dev\start-lifeos-dev.ps1 -BuildAndInstall

[CmdletBinding()]
param(
    [string]$Repo = 'C:\lifeos-worktrees\assistant-chat',
    [string]$DeviceId = '46050DLAQ002Q0',
    [switch]$BuildAndInstall
)

$ErrorActionPreference = 'Stop'

function Test-LocalPort([int]$Port) {
    $client = New-Object System.Net.Sockets.TcpClient
    try {
        $result = $client.BeginConnect(
            '127.0.0.1', $Port, $null, $null
        )
        if (-not $result.AsyncWaitHandle.WaitOne(650)) {
            return $false
        }
        $client.EndConnect($result)
        return $true
    }
    catch {
        return $false
    }
    finally {
        $client.Close()
    }
}

# Chiave condivisa Python <-> API.
# Salvata fuori dal repository.
function Get-LocalServiceKey {
    $dir = Join-Path $env:LOCALAPPDATA 'LifeOS'
    $file = Join-Path $dir 'dev-ai-service-key.txt'

    if (Test-Path -LiteralPath $file) {
        $saved = (Get-Content -LiteralPath $file -Raw).Trim()
        if ($saved.Length -ge 32) {
            return $saved
        }
    }

    $bytes = New-Object byte[] 32
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()

    try {
        $rng.GetBytes($bytes)
    }
    finally {
        $rng.Dispose()
    }

    $key = -join ($bytes | ForEach-Object {
        $_.ToString('x2')
    })

    New-Item -ItemType Directory -Force -Path $dir |
        Out-Null

    Set-Content -LiteralPath $file `
        -Value $key `
        -Encoding ASCII `
        -NoNewline

    return $key
}

function Wait-LocalPort(
    [int]$Port,
    [int]$Seconds = 90
) {
    for ($i = 0; $i -lt $Seconds; $i++) {
        if (Test-LocalPort $Port) {
            return
        }

        Start-Sleep -Seconds 1
    }

    throw "Porta locale $Port non pronta."
}

function Assert-PixelConnected {
    & $script:adb -s $script:DeviceId get-state 2>$null |
        Out-Null

    if ($LASTEXITCODE -ne 0) {
        throw "Pixel 9 non disponibile ($script:DeviceId)."
    }
}

# ---------------------------------------------------------
# Verifica repository e strumenti
# ---------------------------------------------------------

if (-not (Test-Path -LiteralPath (
    Join-Path $Repo 'src\dotnet\LifeOS.Api\LifeOS.Api.csproj'
))) {
    throw "Repository LifeOS non trovato: $Repo"
}

$Repo = (Resolve-Path -LiteralPath $Repo).Path

$adb = Join-Path $env:LOCALAPPDATA `
    'Android\Sdk\platform-tools\adb.exe'

$py = Join-Path $Repo `
    'src\python\lifeos-ai\.venv\Scripts\python.exe'

if (-not (Test-Path -LiteralPath $adb)) {
    throw "ADB non trovato: $adb"
}

if (-not (Test-Path -LiteralPath $py)) {
    throw "Virtualenv Python non trovato: $py"
}

# ---------------------------------------------------------
# BUILD E INSTALLAZIONE ANDROID (OPZIONALE)
# ---------------------------------------------------------

if ($BuildAndInstall) {

    Write-Host '=== Build e installazione LifeOS Dev ==='

    Assert-PixelConnected

    $appProject = Join-Path $Repo `
        'src\dotnet\LifeOS.App\LifeOS.App.csproj'

    if (-not (Test-Path -LiteralPath $appProject)) {
        throw "Progetto Android non trovato: $appProject"
    }

    $sdk = Join-Path $env:LOCALAPPDATA 'Android\Sdk'

    $java = $env:JAVA_HOME

    if (-not $java) {
        $java = 'C:\Program Files\Microsoft\jdk-21.0.12.101-hotspot'
    }

    if (-not (Test-Path -LiteralPath $sdk)) {
        throw "Android SDK non trovato: $sdk"
    }

    if (-not (Test-Path -LiteralPath $java)) {
        throw "Java SDK non trovato: $java"
    }

    if (-not (Get-Command dotnet.exe -ErrorAction SilentlyContinue)) {
        throw 'dotnet.exe non trovato nel PATH.'
    }

    $env:ANDROID_HOME = $sdk
    $env:JAVA_HOME = $java

    $apk = Join-Path $Repo `
        'src\dotnet\LifeOS.App\bin\Debug\net10.0-android\android-arm64\publish\it.colazzo.lifeos.dev-Signed.apk'

    # Elimina un eventuale APK vecchio prima della build.
    if (Test-Path -LiteralPath $apk) {
        Remove-Item -LiteralPath $apk -Force
    }

    Push-Location $Repo

    try {
        & dotnet.exe publish $appProject `
            -f net10.0-android `
            -c Debug `
            -r android-arm64 `
            '-p:AndroidPackageFormats=apk' `
            '-p:EmbedAssembliesIntoApk=true' `
            "-p:AndroidSdkDirectory=$sdk" `
            "-p:JavaSdkDirectory=$java"

        if ($LASTEXITCODE -ne 0) {
            throw "Publish Android fallito: $LASTEXITCODE"
        }
    }
    finally {
        Pop-Location
    }

    if (-not (Test-Path -LiteralPath $apk -PathType Leaf)) {
        throw "APK Dev non prodotto dalla build: $apk"
    }

    $apkInfo = Get-Item -LiteralPath $apk

    if ($apkInfo.Length -le 0) {
        throw 'APK Dev vuoto.'
    }

    # Controlla il package reale dentro l'APK.
    $aapt = Get-ChildItem `
        -LiteralPath (Join-Path $sdk 'build-tools') `
        -Filter 'aapt.exe' `
        -Recurse `
        -File `
        -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending |
        Select-Object -First 1

    if (-not $aapt) {
        throw 'aapt.exe non trovato nell Android SDK.'
    }

    $badging = & $aapt.FullName dump badging $apk 2>&1

    if ($LASTEXITCODE -ne 0) {
        throw 'Impossibile leggere il package APK.'
    }

    $packageLine = $badging |
        Where-Object { $_ -match '^package: ' } |
        Select-Object -First 1

    if (
        -not $packageLine -or
        $packageLine -notmatch "^package: name='it\.colazzo\.lifeos\.dev'(?=\s)"
    ) {
        throw "STOP: package APK inatteso: $packageLine"
    }

    Assert-PixelConnected

    # Aggiorna senza cancellare dati e login.
    & $adb -s $DeviceId install -r --no-incremental $apk

    if ($LASTEXITCODE -ne 0) {
        throw "Installazione APK Dev fallita: $LASTEXITCODE"
    }

    Write-Host `
        'APK LifeOS Dev aggiornato. Dati locali conservati.' `
        -ForegroundColor Green
}

# ---------------------------------------------------------
# DATABASE LOCALE
# ---------------------------------------------------------

Write-Host '=== Database locale ==='

$pg = docker ps `
    --filter 'name=^/lifeos-postgres$' `
    --format '{{.Names}}'

if ($LASTEXITCODE -ne 0) {
    throw 'Docker non risponde: avvia Docker Desktop.'
}

if ($pg -ne 'lifeos-postgres') {
    Push-Location $Repo

    try {
        docker compose up -d postgres

        if ($LASTEXITCODE -ne 0) {
            throw 'Avvio PostgreSQL fallito.'
        }
    }
    finally {
        Pop-Location
    }
}

# ---------------------------------------------------------
# PYTHON AI
# ---------------------------------------------------------

$localKey = Get-LocalServiceKey
$pythonReused = $false

Write-Host '=== Python AI ==='

if (Test-LocalPort 8000) {

    try {
        $health = Invoke-RestMethod `
            'http://127.0.0.1:8000/health/live' `
            -TimeoutSec 5
    }
    catch {
        throw 'Porta 8000 occupata da un servizio non riconosciuto.'
    }

    Write-Host 'Servizio Python gia attivo.'
    $pythonReused = $true

    if ($BuildAndInstall) {
        Write-Warning (
            'Python AI gia attivo: potrebbe utilizzare ' +
            'codice precedente. Per AI-008 riavvia Python e API.'
        )
    }
}
else {

    $secure = Read-Host `
        'Chiave OpenAI Assistant (non viene visualizzata)' `
        -AsSecureString

    $env:LIFEOS_AI_ASSISTANT_OPENAI_API_KEY = `
        [System.Net.NetworkCredential]::new('', $secure).Password

    Remove-Variable secure

    if (-not $env:LIFEOS_AI_ASSISTANT_OPENAI_API_KEY) {
        throw 'Chiave OpenAI mancante.'
    }

    $env:LIFEOS_AI_ASSISTANT_PROVIDER = 'openai'
    $env:LIFEOS_AI_ASSISTANT_OPENAI_MODEL = 'gpt-6-luna'
    $env:LIFEOS_AI_ASSISTANT_OPENAI_REGION = 'global'
    $env:LIFEOS_AI_ASSISTANT_OPENAI_REASONING_EFFORT = 'low'

    # Valori del precedente ambiente locale.
    $env:LIFEOS_AI_ASSISTANT_OPENAI_DAILY_BUDGET_USD = '1'
    $env:LIFEOS_AI_ASSISTANT_OPENAI_MONTHLY_BUDGET_USD = '5'
    $env:LIFEOS_AI_ASSISTANT_OPENAI_INPUT_PRICE_PER_MTOK_USD = '0.125'
    $env:LIFEOS_AI_ASSISTANT_OPENAI_OUTPUT_PRICE_PER_MTOK_USD = '0.625'

    $env:LIFEOS_AI_SERVICE_KEY = $localKey

    try {
        Start-Process `
            -FilePath $py `
            -WorkingDirectory (Join-Path $Repo 'src\python\lifeos-ai') `
            -ArgumentList '-m uvicorn lifeos_ai.app:create_app --factory --host 127.0.0.1 --port 8000' |
            Out-Null
    }
    finally {
        Remove-Item Env:\LIFEOS_AI_ASSISTANT_OPENAI_API_KEY `
            -ErrorAction SilentlyContinue

        Remove-Item Env:\LIFEOS_AI_SERVICE_KEY `
            -ErrorAction SilentlyContinue
    }

    Wait-LocalPort 8000

    $null = Invoke-RestMethod `
        'http://127.0.0.1:8000/health/live' `
        -TimeoutSec 10

    Write-Host 'Python AI avviato.'
}

# ---------------------------------------------------------
# API .NET
# ---------------------------------------------------------

Write-Host '=== API .NET ==='

if (Test-LocalPort 5050) {

    Write-Warning (
        'API gia attiva sulla porta 5050. ' +
        'Non e possibile verificare che usi il codice aggiornato.'
    )
}
else {

    if ($pythonReused) {
        Write-Warning (
            'Python AI era gia attivo. ' +
            'Se Assistant non funziona, riavvia entrambi i servizi.'
        )
    }

    $env:AssistantAi__Enabled = 'true'
    $env:AssistantAi__TimeoutSeconds = '75'

    $env:NutritionAi__BaseUrl = 'http://127.0.0.1:8000/'
    $env:NutritionAi__ServiceKey = $localKey

    $env:ANDROID_HOME = Join-Path `
        $env:LOCALAPPDATA 'Android\Sdk'

    try {
        Start-Process `
            -FilePath 'dotnet.exe' `
            -WorkingDirectory $Repo `
            -ArgumentList 'run --project src/dotnet/LifeOS.Api/LifeOS.Api.csproj --launch-profile http' |
            Out-Null
    }
    finally {
        Remove-Item Env:\NutritionAi__ServiceKey `
            -ErrorAction SilentlyContinue
    }

    Wait-LocalPort 5050 120

    Write-Host 'LifeOS.Api avviata.'
}

# ---------------------------------------------------------
# PIXEL 9 / ADB
# ---------------------------------------------------------

Write-Host '=== Pixel 9 / ADB ==='

& $adb devices

if ($LASTEXITCODE -ne 0) {
    throw 'ADB non risponde.'
}

Assert-PixelConnected

& $adb -s $DeviceId reverse tcp:5050 tcp:5050

if ($LASTEXITCODE -ne 0) {
    throw 'adb reverse non riuscito.'
}

& $adb -s $DeviceId reverse --list

& $adb -s $DeviceId shell monkey `
    -p it.colazzo.lifeos.dev `
    -c android.intent.category.LAUNCHER 1

if ($LASTEXITCODE -ne 0) {
    throw 'LifeOS Dev non avviata.'
}

Write-Host `
    'LifeOS Dev aperta sul Pixel 9. Vai in More > Assistant.' `
    -ForegroundColor Green
