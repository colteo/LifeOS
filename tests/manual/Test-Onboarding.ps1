<#
.SYNOPSIS
    Manual acceptance test for first-run onboarding (ADR-006).

.DESCRIPTION
    Signs in a new user through the Development sign-in endpoint and walks the onboarding flow:
    finance profile (default currency + starter categories), first account, completion.
    Also checks idempotent retries and that a second user gets independent starter categories.

    Run ONLY against a local Development API whose database has the
    AddCategorySiblingUniqueness migration applied. Each run uses new random subjects, so it can
    be repeated without resetting data.

    Prerequisites (User Secrets of LifeOS.Api):
      Authentication:LifeOS:SigningKey          Base64 of >= 32 random bytes
      Authentication:DevelopmentSignIn:Enabled  true
    and the API running, e.g.:
      dotnet run --project src/dotnet/LifeOS.Api/LifeOS.Api.csproj --launch-profile http

    Works with Windows PowerShell 5.1 and PowerShell 7.

.EXAMPLE
    ./tests/manual/Test-Onboarding.ps1
    ./tests/manual/Test-Onboarding.ps1 -BaseUrl http://localhost:5050
#>
[CmdletBinding()]
param(
    [string]$BaseUrl = 'http://localhost:5050'
)

$ErrorActionPreference = 'Stop'
$script:Failures = 0
$StarterCategoryCount = 15

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

function Get-Items($Json) {
    # An empty JSON array can come back as $null; normalize to an array.
    if ($null -eq $Json) { return @() }
    return @($Json)
}

function Sign-In([string]$Subject) {
    $result = Invoke-LifeOS -Method POST -Path '/api/auth/dev/sign-in' -Body @{ subject = $Subject; displayName = $Subject }
    if ($result.Status -ne 200) {
        throw "Development sign-in for '$Subject' failed with HTTP $($result.Status). Is the API running with Authentication:DevelopmentSignIn:Enabled=true?"
    }
    return $result.Json.accessToken
}

function Get-Me([string]$AccessToken) {
    return (Invoke-LifeOS -Method GET -Path '/api/me' -AccessToken $AccessToken).Json
}

function Get-Categories([string]$AccessToken) {
    return Get-Items (Invoke-LifeOS -Method GET -Path '/api/categories' -AccessToken $AccessToken).Json
}

$runId = [guid]::NewGuid().ToString('N').Substring(0, 8)
Write-Host "LifeOS onboarding acceptance test (run $runId) against $BaseUrl" -ForegroundColor Cyan

# 1-2. New user starts onboarding.
$token = Sign-In "onboarding-a-$runId"
$me = Get-Me $token
Assert-Step 'New user is PendingFinanceProfile' ($me.onboardingStatus -eq 'PendingFinanceProfile') "(got $($me.onboardingStatus))"
Assert-Step 'New user has no default currency' ($null -eq $me.defaultCurrency)
Assert-Step 'New user has no categories' ((Get-Categories $token).Count -eq 0)

# Anonymous onboarding is rejected.
$anonymous = Invoke-LifeOS -Method POST -Path '/api/onboarding/finance-profile' -Body @{ defaultCurrency = 'EUR' }
Assert-Step 'Anonymous finance-profile is rejected (401)' ($anonymous.Status -eq 401) "(got $($anonymous.Status))"

# 3-4. Finance profile.
$setUp = Invoke-LifeOS -Method POST -Path '/api/onboarding/finance-profile' -AccessToken $token -Body @{ defaultCurrency = 'eur' }
Assert-Step 'Finance profile set up (200)' ($setUp.Status -eq 200) "(got $($setUp.Status))"

$me = Get-Me $token
Assert-Step 'Status is PendingFirstAccount' ($me.onboardingStatus -eq 'PendingFirstAccount') "(got $($me.onboardingStatus))"
Assert-Step 'Default currency is EUR' ($me.defaultCurrency -eq 'EUR') "(got $($me.defaultCurrency))"

$categories = Get-Categories $token
Assert-Step "User has $StarterCategoryCount starter categories" ($categories.Count -eq $StarterCategoryCount) "(got $($categories.Count))"
Assert-Step 'Starter categories are top-level' (@($categories | Where-Object { $null -ne $_.parentCategoryId }).Count -eq 0)
Assert-Step 'Both Altro categories exist' (@($categories | Where-Object { $_.name -eq 'Altro' }).Count -eq 2)

# 5. Same request again is idempotent.
$retry = Invoke-LifeOS -Method POST -Path '/api/onboarding/finance-profile' -AccessToken $token -Body @{ defaultCurrency = 'EUR' }
Assert-Step 'Repeating finance profile with EUR succeeds (200)' ($retry.Status -eq 200) "(got $($retry.Status))"
Assert-Step "Still $StarterCategoryCount categories" ((Get-Categories $token).Count -eq $StarterCategoryCount)

$otherCurrency = Invoke-LifeOS -Method POST -Path '/api/onboarding/finance-profile' -AccessToken $token -Body @{ defaultCurrency = 'USD' }
Assert-Step 'Changing the currency is rejected (400)' ($otherCurrency.Status -eq 400) "(got $($otherCurrency.Status))"

# 6. Completion requires a first account.
$tooEarly = Invoke-LifeOS -Method POST -Path '/api/onboarding/complete' -AccessToken $token
Assert-Step 'Completion before an account is rejected (400)' ($tooEarly.Status -eq 400) "(got $($tooEarly.Status))"

# 7. First account.
$account = Invoke-LifeOS -Method POST -Path '/api/accounts' -AccessToken $token -Body @{ name = "Checking $runId"; type = 'BankAccount'; currency = 'EUR' }
Assert-Step 'First BankAccount in EUR created (201)' ($account.Status -eq 201) "(got $($account.Status))"

# 8-9. Completion.
$complete = Invoke-LifeOS -Method POST -Path '/api/onboarding/complete' -AccessToken $token
Assert-Step 'Onboarding completed (200)' ($complete.Status -eq 200) "(got $($complete.Status))"
$me = Get-Me $token
Assert-Step 'Status is Completed' ($me.onboardingStatus -eq 'Completed') "(got $($me.onboardingStatus))"

# 10. Completion again is idempotent.
$completeAgain = Invoke-LifeOS -Method POST -Path '/api/onboarding/complete' -AccessToken $token
Assert-Step 'Repeating completion succeeds (200)' ($completeAgain.Status -eq 200) "(got $($completeAgain.Status))"

# 11. A second user gets independent starter categories.
$tokenB = Sign-In "onboarding-b-$runId"
Assert-Step 'Second user is PendingFinanceProfile' ((Get-Me $tokenB).onboardingStatus -eq 'PendingFinanceProfile')
$setUpB = Invoke-LifeOS -Method POST -Path '/api/onboarding/finance-profile' -AccessToken $tokenB -Body @{ defaultCurrency = 'CHF' }
Assert-Step 'Second user finance profile set up (200)' ($setUpB.Status -eq 200) "(got $($setUpB.Status))"

$idsA = @($categories | ForEach-Object { $_.id })
$idsB = @(Get-Categories $tokenB | ForEach-Object { $_.id })
Assert-Step "Second user has $StarterCategoryCount own categories" ($idsB.Count -eq $StarterCategoryCount) "(got $($idsB.Count))"
Assert-Step 'No category id is shared between the users' (@($idsB | Where-Object { $idsA -contains $_ }).Count -eq 0)
Assert-Step "First user's default currency is unchanged" ((Get-Me $token).defaultCurrency -eq 'EUR')

Write-Host ''
if ($script:Failures -gt 0) {
    Write-Host "$($script:Failures) check(s) FAILED." -ForegroundColor Red
    exit 1
}

Write-Host 'All checks passed.' -ForegroundColor Green
exit 0
