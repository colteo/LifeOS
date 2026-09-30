<#
.SYNOPSIS
    Manual acceptance test for per-user Finance ownership (ADR-006).

.DESCRIPTION
    Signs in two users through the Development sign-in endpoint and verifies that
    user B can neither see nor reference user A's accounts, categories and transactions.

    Run ONLY against a local Development API whose database was created from the
    multi-user InitialCreate baseline. Each run uses new random subjects, so it can be
    repeated without resetting data.

    Prerequisites (User Secrets of LifeOS.Api):
      Authentication:LifeOS:SigningKey          Base64 of >= 32 random bytes
      Authentication:DevelopmentSignIn:Enabled  true
    and the API running, e.g.:
      dotnet run --project src/dotnet/LifeOS.Api/LifeOS.Api.csproj --launch-profile http

    Works with Windows PowerShell 5.1 and PowerShell 7.

.EXAMPLE
    ./tests/manual/Test-FinanceOwnership.ps1
    ./tests/manual/Test-FinanceOwnership.ps1 -BaseUrl http://localhost:5050
#>
[CmdletBinding()]
param(
    [string]$BaseUrl = 'http://localhost:5050'
)

$ErrorActionPreference = 'Stop'
$script:Failures = 0

function Invoke-LifeOS {
    param(
        [Parameter(Mandatory)][string]$Method,
        [Parameter(Mandatory)][string]$Path,
        [string]$AccessToken,
        [object]$Body
    )

    $headers = @{ Accept = 'application/json' }
    if ($AccessToken) { $headers['Authorization'] = "Bearer $AccessToken" }

    $parameters = @{
        Uri             = "$BaseUrl$Path"
        Method          = $Method
        Headers         = $headers
        UseBasicParsing = $true
    }

    if ($null -ne $Body) {
        $parameters['ContentType'] = 'application/json; charset=utf-8'
        $parameters['Body'] = [System.Text.Encoding]::UTF8.GetBytes(($Body | ConvertTo-Json -Depth 5))
    }

    try {
        $response = Invoke-WebRequest @parameters
        $status = [int]$response.StatusCode
        $content = $response.Content
    }
    catch {
        if ($null -eq $_.Exception.Response) { throw }
        $status = [int]$_.Exception.Response.StatusCode
        $content = $null
    }

    $json = $null
    if ($content) { $json = $content | ConvertFrom-Json }

    return [pscustomobject]@{ Status = $status; Json = $json }
}

function Assert-Step {
    param([string]$Name, [bool]$Condition, [string]$Detail = '')

    if ($Condition) {
        Write-Host "PASS  $Name" -ForegroundColor Green
    }
    else {
        $script:Failures++
        Write-Host "FAIL  $Name $Detail" -ForegroundColor Red
    }
}

function Get-Ids($Items) {
    # An empty JSON array can come back as $null; normalize to an empty list of ids.
    if ($null -eq $Items) { return @() }
    return @($Items | ForEach-Object { $_.id })
}

function Sign-In([string]$Subject) {
    $result = Invoke-LifeOS -Method POST -Path '/api/auth/dev/sign-in' -Body @{ subject = $Subject; displayName = $Subject }
    if ($result.Status -ne 200) {
        throw "Development sign-in for '$Subject' failed with HTTP $($result.Status). Is the API running with Authentication:DevelopmentSignIn:Enabled=true?"
    }
    return $result.Json.accessToken
}

$runId = [guid]::NewGuid().ToString('N').Substring(0, 8)
$now = [DateTimeOffset]::UtcNow
$occurredAtUtc = $now.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")
$range = "fromUtc=$($now.AddDays(-1).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"))&toUtc=$($now.AddDays(1).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"))"

Write-Host "LifeOS Finance ownership acceptance test (run $runId) against $BaseUrl" -ForegroundColor Cyan

# ---- Anonymous access ----
$anonymous = Invoke-LifeOS -Method GET -Path '/api/accounts'
Assert-Step 'Anonymous GET /api/accounts is rejected (401)' ($anonymous.Status -eq 401) "(got $($anonymous.Status))"

# ---- User A ----
$tokenA = Sign-In "acceptance-a-$runId"

$accountA = Invoke-LifeOS -Method POST -Path '/api/accounts' -AccessToken $tokenA -Body @{ name = "Account A $runId"; type = 'BankAccount'; currency = 'EUR' }
Assert-Step 'A creates Account A (201)' ($accountA.Status -eq 201) "(got $($accountA.Status))"

$categoryA = Invoke-LifeOS -Method POST -Path '/api/categories' -AccessToken $tokenA -Body @{ name = "Category A $runId"; type = 'Expense'; parentCategoryId = $null }
Assert-Step 'A creates Category A (201)' ($categoryA.Status -eq 201) "(got $($categoryA.Status))"

$expenseA = Invoke-LifeOS -Method POST -Path '/api/transactions' -AccessToken $tokenA -Body @{
    type = 'Expense'; amount = 12.5; accountId = $accountA.Json.id; sourceAccountId = $null; destinationAccountId = $null
    categoryId = $categoryA.Json.id; occurredAtUtc = $occurredAtUtc; note = "Acceptance $runId"
}
Assert-Step "A creates an Expense with A's account and category (201)" ($expenseA.Status -eq 201) "(got $($expenseA.Status))"

$accountsOfA = Invoke-LifeOS -Method GET -Path '/api/accounts' -AccessToken $tokenA
Assert-Step 'A sees Account A' ((Get-Ids $accountsOfA.Json) -contains $accountA.Json.id)

$transactionsOfA = Invoke-LifeOS -Method GET -Path "/api/transactions?$range" -AccessToken $tokenA
Assert-Step "A sees A's transaction" ((Get-Ids $transactionsOfA.Json) -contains $expenseA.Json.id)

# ---- User B ----
$tokenB = Sign-In "acceptance-b-$runId"

$accountsOfB = Invoke-LifeOS -Method GET -Path '/api/accounts' -AccessToken $tokenB
Assert-Step 'B does not see Account A' (-not ((Get-Ids $accountsOfB.Json) -contains $accountA.Json.id))

$categoriesOfB = Invoke-LifeOS -Method GET -Path '/api/categories' -AccessToken $tokenB
Assert-Step 'B does not see Category A' (-not ((Get-Ids $categoriesOfB.Json) -contains $categoryA.Json.id))

$transactionsOfB = Invoke-LifeOS -Method GET -Path "/api/transactions?$range" -AccessToken $tokenB
Assert-Step "B does not see A's transaction" (-not ((Get-Ids $transactionsOfB.Json) -contains $expenseA.Json.id))

# B's own resources, so each 404 below isolates exactly one foreign reference.
$accountB = Invoke-LifeOS -Method POST -Path '/api/accounts' -AccessToken $tokenB -Body @{ name = "Account B $runId"; type = 'BankAccount'; currency = 'EUR' }
$categoryB = Invoke-LifeOS -Method POST -Path '/api/categories' -AccessToken $tokenB -Body @{ name = "Category B $runId"; type = 'Expense'; parentCategoryId = $null }
Assert-Step 'B creates its own account and category (201)' (($accountB.Status -eq 201) -and ($categoryB.Status -eq 201))

$useAccountA = Invoke-LifeOS -Method POST -Path '/api/transactions' -AccessToken $tokenB -Body @{
    type = 'Expense'; amount = 1; accountId = $accountA.Json.id; sourceAccountId = $null; destinationAccountId = $null
    categoryId = $categoryB.Json.id; occurredAtUtc = $occurredAtUtc; note = $null
}
Assert-Step "B using Account A in an Expense gets 404" ($useAccountA.Status -eq 404) "(got $($useAccountA.Status))"

$useCategoryA = Invoke-LifeOS -Method POST -Path '/api/transactions' -AccessToken $tokenB -Body @{
    type = 'Expense'; amount = 1; accountId = $accountB.Json.id; sourceAccountId = $null; destinationAccountId = $null
    categoryId = $categoryA.Json.id; occurredAtUtc = $occurredAtUtc; note = $null
}
Assert-Step "B using Category A in an Expense gets 404" ($useCategoryA.Status -eq 404) "(got $($useCategoryA.Status))"

$transferFromA = Invoke-LifeOS -Method POST -Path '/api/transactions' -AccessToken $tokenB -Body @{
    type = 'Transfer'; amount = 1; accountId = $null; sourceAccountId = $accountA.Json.id; destinationAccountId = $accountB.Json.id
    categoryId = $null; occurredAtUtc = $occurredAtUtc; note = $null
}
Assert-Step "B transferring from Account A gets 404" ($transferFromA.Status -eq 404) "(got $($transferFromA.Status))"

$subcategoryOfA = Invoke-LifeOS -Method POST -Path '/api/categories' -AccessToken $tokenB -Body @{ name = "Sub $runId"; type = 'Expense'; parentCategoryId = $categoryA.Json.id }
Assert-Step "B using Category A as a parent gets 404" ($subcategoryOfA.Status -eq 404) "(got $($subcategoryOfA.Status))"

# ---- A's data is unchanged ----
$transactionsOfAAfter = Invoke-LifeOS -Method GET -Path "/api/transactions?$range" -AccessToken $tokenA
Assert-Step "A still has exactly its one transaction" (@(Get-Ids $transactionsOfAAfter.Json).Count -eq 1)

Write-Host ''
if ($script:Failures -gt 0) {
    Write-Host "$($script:Failures) check(s) FAILED." -ForegroundColor Red
    exit 1
}

Write-Host 'All checks passed.' -ForegroundColor Green
exit 0
