# LifeOS AI-007 - Avvio sviluppo su Pixel 9 (PowerShell 5.1+)
# Copiare in C:\lifeos-worktrees\assistant-chat\tools\dev\start-lifeos-dev.ps1
# Invocare con: powershell -ExecutionPolicy Bypass -File .\tools\dev\start-lifeos-dev.ps1
[CmdletBinding()]
param(
    [string]$Repo = 'C:\lifeos-worktrees\assistant-chat',
    [string]$DeviceId = '46050DLAQ002Q0'
)
$ErrorActionPreference = 'Stop'

function Test-LocalPort([int]$Port) {
    $client = New-Object System.Net.Sockets.TcpClient
    try {
        $result = $client.BeginConnect('127.0.0.1', $Port, $null, $null)
        if (-not $result.AsyncWaitHandle.WaitOne(650)) { return $false }
        $client.EndConnect($result)
        return $true
    } catch { return $false }
    finally { $client.Close() }
}
function Wait-LocalPort([int]$Port, [int]$Seconds = 90) {
    for ($i = 0; $i -lt $Seconds; $i++) {
        if (Test-LocalPort $Port) { return }
        Start-Sleep -Seconds 1
    }
    throw "Porta locale $Port non pronta. Controlla la finestra del servizio appena avviato."
}

if (-not (Test-Path "$Repo\src\dotnet\LifeOS.Api\LifeOS.Api.csproj")) {
    throw "Repository LifeOS non trovato: $Repo"
}
$adb = "$env:LOCALAPPDATA\Android\Sdk\platform-tools\adb.exe"
$py = "$Repo\src\python\lifeos-ai\.venv\Scripts\python.exe"
if (-not (Test-Path $adb)) { throw "ADB non trovato: $adb" }
if (-not (Test-Path $py)) { throw "Virtualenv Python non trovato: $py" }

Write-Host '=== Database locale ==='
$pg = docker ps --filter 'name=^/lifeos-postgres$' --format '{{.Names}}'
if ($LASTEXITCODE -ne 0) { throw 'Docker non risponde: avvia Docker Desktop.' }
if ($pg -ne 'lifeos-postgres') {
    Push-Location $Repo
    try { docker compose up -d postgres; if ($LASTEXITCODE -ne 0) { throw 'Avvio PostgreSQL fallito' } }
    finally { Pop-Location }
}

$localKey = 'local-assistant-device-test-key-2026-10-27'
Write-Host '=== Python AI ==='
if (Test-LocalPort 8000) {
    try { $health = Invoke-RestMethod 'http://127.0.0.1:8000/health/live' -TimeoutSec 5 }
    catch { throw 'Porta 8000 occupata da un servizio che non risponde come LifeOS AI.' }
    Write-Host 'Servizio Python gia attivo; utilizzo quello esistente.'
} else {
    $secure = Read-Host 'Chiave OpenAI Assistant (non viene visualizzata)' -AsSecureString
    $env:LIFEOS_AI_ASSISTANT_OPENAI_API_KEY = [System.Net.NetworkCredential]::new('', $secure).Password
    Remove-Variable secure
    if (-not $env:LIFEOS_AI_ASSISTANT_OPENAI_API_KEY) { throw 'Chiave OpenAI mancante.' }
    $env:LIFEOS_AI_ASSISTANT_PROVIDER = 'openai'
    $env:LIFEOS_AI_ASSISTANT_OPENAI_MODEL = 'gpt-6-luna'
    $env:LIFEOS_AI_ASSISTANT_OPENAI_REGION = 'global'
    $env:LIFEOS_AI_ASSISTANT_OPENAI_REASONING_EFFORT = 'low'
    # Valori gia usati nel test locale: adeguare le tariffe alle condizioni reali.
    $env:LIFEOS_AI_ASSISTANT_OPENAI_DAILY_BUDGET_USD = '1'
    $env:LIFEOS_AI_ASSISTANT_OPENAI_MONTHLY_BUDGET_USD = '5'
    $env:LIFEOS_AI_ASSISTANT_OPENAI_INPUT_PRICE_PER_MTOK_USD = '0.125'
    $env:LIFEOS_AI_ASSISTANT_OPENAI_OUTPUT_PRICE_PER_MTOK_USD = '0.625'
    $env:LIFEOS_AI_SERVICE_KEY = $localKey
    try {
        Start-Process -FilePath $py -WorkingDirectory "$Repo\src\python\lifeos-ai" `
            -ArgumentList '-m uvicorn lifeos_ai.app:create_app --factory --host 127.0.0.1 --port 8000' | Out-Null
    } finally {
        Remove-Item Env:\LIFEOS_AI_ASSISTANT_OPENAI_API_KEY -ErrorAction SilentlyContinue
    }
    Wait-LocalPort 8000
    $null = Invoke-RestMethod 'http://127.0.0.1:8000/health/live' -TimeoutSec 10
    Write-Host 'Python AI avviato.'
}

Write-Host '=== API .NET ==='
if (Test-LocalPort 5050) {
    Write-Warning 'API gia attiva sulla porta 5050: impossibile verificare da qui che AssistantAi sia abilitato. Se non lo fosse, interrompi la vecchia API e riesegui lo script.'
} else {
    $env:AssistantAi__Enabled = 'true'
    $env:AssistantAi__TimeoutSeconds = '75'
    $env:NutritionAi__BaseUrl = 'http://127.0.0.1:8000/'
    $env:NutritionAi__ServiceKey = $localKey
    $env:ANDROID_HOME = "$env:LOCALAPPDATA\Android\Sdk"
    try {
        Start-Process -FilePath 'dotnet.exe' -WorkingDirectory $Repo `
            -ArgumentList 'run --project src/dotnet/LifeOS.Api/LifeOS.Api.csproj --launch-profile http' | Out-Null
    } finally {
        Remove-Item Env:\NutritionAi__ServiceKey -ErrorAction SilentlyContinue
    }
    Wait-LocalPort 5050 120
    Write-Host 'LifeOS.Api avviata.'
}

Write-Host '=== Pixel 9 / ADB ==='
& $adb devices
if ($LASTEXITCODE -ne 0) { throw 'ADB non risponde.' }
$state = (& $adb -s $DeviceId get-state 2>&1 | Out-String).Trim()
if ($state -ne 'device') { throw "Pixel 9 non disponibile ($DeviceId): collega USB e autorizza il debug sul telefono." }
& $adb -s $DeviceId reverse tcp:5050 tcp:5050
if ($LASTEXITCODE -ne 0) { throw 'adb reverse non riuscito.' }
& $adb -s $DeviceId reverse --list
& $adb -s $DeviceId shell monkey -p it.colazzo.lifeos.dev -c android.intent.category.LAUNCHER 1
if ($LASTEXITCODE -ne 0) { throw 'LifeOS Dev non avviata. Verifica che it.colazzo.lifeos.dev sia installata.' }
Write-Host 'LifeOS Dev aperta sul Pixel 9. Vai in More > Assistant.' -ForegroundColor Green
