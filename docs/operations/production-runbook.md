# LifeOS Production runbook

The exact procedure for releasing LifeOS to Production:

- **Part A**: the first Production release (creates every resource).
- **Part B**: every later release.
- **Part C**: rollback.

Backups and restores have their own document: [Backup and restore](backup-restore.md).

Everything here is executed by hand, in **Windows PowerShell 5.1**, from the repository root
(`C:\lifeos`) unless a step says otherwise. Commands are written so they can later move into CI.

Commands marked **VERIFY AT EXECUTION** depend on provider behaviour or consoles that could not be
tested without creating cloud resources. Their flags were checked against `gcloud --help`
(Google Cloud SDK 587.0.0) where possible, but read the output and the provider's current
documentation when you run them.

Provider pricing and free-tier terms change: **verify current pricing before creating any
resource.** Nothing in this runbook guarantees zero cost; it only keeps idle cost low.

---

## 0. Stop conditions

Stop, do not continue, and investigate if any of these happens:

| # | Stop if | Where it is checked |
|---|---|---|
| S1 | Neon PostgreSQL major is **below 15** | A4.3 |
| S2 | Neon `lc_ctype` is not a UTF-8 locale, or `lower('ÀÉÎ')` is not `àéî` | A4.3 |
| S3 | `has-pending-model-changes` reports changes you did not expect | A1.2, B3 |
| S4 | The migration script fails, or contains anything you did not expect | A5, B6 |
| S5 | A Cloud Run revision fails to start, or restarts in a loop | A8, B9 |
| S6 | Cloud Run logs contain a secret, token, password or connection string | A8.4, A10, B9 |
| S7 | The Google redirect URI differs from the URL the app uses | A6, A9 |
| S8 | A Google account that is **not** allowlisted can sign in | A10 |
| S9 | The Release APK points anywhere except the HTTPS Cloud Run URL | A12 |
| S10 | The APK is not signed with the permanent release key (`CN=Android Debug` or an unknown certificate) | A12 |
| S11 | A restore drill fails | [Backup and restore](backup-restore.md) |
| S12 | There is no fresh backup before a schema migration (later releases) | B5 |

---

## 1. Placeholders and fixed names

Placeholders (never replace them inside this repository):

| Placeholder | Meaning |
|---|---|
| `<GCP_PROJECT_ID>` | Google Cloud project id |
| `<GCP_PROJECT_NUMBER>` | Google Cloud project number (digits) |
| `<BILLING_ACCOUNT_ID>` | Billing account (`XXXXXX-XXXXXX-XXXXXX`) |
| `<CLOUD_RUN_REGION>` | `europe-west3` (Frankfurt) unless availability says otherwise |
| `<CLOUD_RUN_URL>` | The Cloud Run service URL, e.g. `https://lifeos-api-<GCP_PROJECT_NUMBER>.europe-west3.run.app` |
| `<NEON_HOST>` | Neon **direct** endpoint host (no `-pooler` in the name) |
| `<NEON_DATABASE>` | `lifeos` |
| `<LIFEOS_DB_USER>` | `lifeos` (the dedicated role) |
| `<PRODUCTION_GOOGLE_EMAIL>` | The Production user's Google account |
| `<GOOGLE_CLIENT_ID>` | Production Google Web OAuth client id |
| `<KEYSTORE_PATH>` | Permanent Android keystore, outside the repository |

Fixed names:

| Thing | Name |
|---|---|
| Cloud Run service | `lifeos-api` |
| Runtime service account | `lifeos-api@<GCP_PROJECT_ID>.iam.gserviceaccount.com` |
| Artifact Registry repository | `lifeos` (Docker) |
| Image | `<CLOUD_RUN_REGION>-docker.pkg.dev/<GCP_PROJECT_ID>/lifeos/lifeos-api:<git-sha>` |
| Secrets | `lifeos-postgresql`, `lifeos-signing-key`, `lifeos-google-client-secret` |
| Android ApplicationId | `it.colazzo.lifeos` (Debug: `it.colazzo.lifeos.dev`) |
| Android auth callback | `lifeos://auth` (Debug: `lifeos-dev://auth`) |
| Keystore alias | `lifeos-release` |

Session variables used by the commands (set them at the start of each session):

```powershell
$Project       = "<GCP_PROJECT_ID>"
$ProjectNumber = "<GCP_PROJECT_NUMBER>"
$Region        = "europe-west3"
$Service       = "lifeos-api"
$RuntimeSa     = "lifeos-api@$Project.iam.gserviceaccount.com"
$Registry      = "$Region-docker.pkg.dev/$Project/lifeos"
$ServiceUrl    = "https://lifeos-api-$ProjectNumber.$Region.run.app"   # confirmed in A8.2
```

---

## 2. Production configuration

### 2.1 Classification

| Setting (environment variable) | Class | Stored in |
|---|---|---|
| `ConnectionStrings__PostgreSQL` | **Secret** | Secret Manager `lifeos-postgresql` |
| `Authentication__LifeOS__SigningKey` | **Secret** | Secret Manager `lifeos-signing-key` |
| `Authentication__Google__ClientSecret` | **Secret** | Secret Manager `lifeos-google-client-secret` |
| `Authentication__Google__ClientId` | Not secret (it appears in every Google sign-in URL) | Cloud Run environment variable |
| `Authentication__Google__AllowedEmails__0` | Personal data, not a secret | Cloud Run environment variable |
| `Logging__Console__FormatterName` = `json` | Not secret | Cloud Run environment variable |
| Cloud Run URL | Not secret | — |
| Android `LifeOSApiBaseUrl` | Not secret (embedded in the APK) | Build command |

The allowed email is a plain environment variable: it is not an authentication secret (the Google
sign-in itself is the authentication), and only people with access to the Google Cloud project can
read the service configuration. It is never written into the repository.

Never set:

- `Authentication__DevelopmentSignIn__Enabled` (the API refuses to start with it outside Development);
- `ASPNETCORE_ENVIRONMENT` (the image already defaults to `Production`);
- `ASPNETCORE_FORWARDEDHEADERS_ENABLED` (forwarded headers are configured in code);
- `ASPNETCORE_HTTPS_PORT` (Cloud Run terminates TLS; a harmless "Failed to determine the https
  port for redirect" warning is expected in the logs).

The API refuses to start in Production without the connection string, signing key, Google client id
and secret, and at least one allowed email.

### 2.2 Two connection-string syntaxes — never mix them

**A. Npgsql connection string** — used **only** by `LifeOS.Api` (`ConnectionStrings__PostgreSQL`):

```text
Host=<NEON_HOST>;Database=<NEON_DATABASE>;Username=<LIFEOS_DB_USER>;Password=<password>;SSL Mode=VerifyFull;GSS Encryption Mode=Disable;Pooling=true;Minimum Pool Size=0;Maximum Pool Size=5;Connection Idle Lifetime=60;Application Name=lifeos-api
```

**B. libpq parameters** — used by `psql`, `pg_dump` and `pg_restore`. They are passed as `PG*`
environment variables, never as an Npgsql string and never as a URL containing the password:

| Variable | Value |
|---|---|
| `PGHOST` | `<NEON_HOST>` |
| `PGDATABASE` | `<NEON_DATABASE>` |
| `PGUSER` | `<LIFEOS_DB_USER>` (or the Neon owner role during A4) |
| `PGPASSWORD` | read with `Read-Host -AsSecureString`, never typed into a command |
| `PGSSLMODE` | `verify-full` |
| `PGSSLROOTCERT` | `system` |

`SSL Mode=VerifyFull` (Npgsql) and `PGSSLMODE=verify-full` (libpq) both verify the certificate
chain **and** the host name. Never use `SSL Mode=Prefer`, `SSL Mode=Require` without verification,
`Trust Server Certificate`, or the Neon pooled host.

Why these Npgsql settings:

- `GSS Encryption Mode=Disable`: Npgsql otherwise probes for Kerberos and logs
  `Cannot load library libgssapi_krb5.so.2`; Neon does not use GSS.
- `Minimum Pool Size=0`, `Connection Idle Lifetime=60`: no connection stays open while LifeOS is
  idle, so Neon can scale to zero.
- `Maximum Pool Size=5`: one instance, one user; far below Neon's connection limits.
- No `EnableRetryOnFailure`: blind retries interact badly with the explicit transaction in account
  deletion. Retries may be reconsidered only after observing real Neon behaviour.
- **Direct endpoint, not the pooled (PgBouncer) one**: Cloud Run runs at most one instance with at
  most 5 connections, so a server-side pooler adds behaviour (transaction pooling, session state)
  without benefit, and EF Core transactions keep plain PostgreSQL semantics.

### 2.3 Cloud Run shape

| Setting | Value | Why |
|---|---|---|
| Port | 8080 | the image listens on `[::]:8080` |
| CPU / memory | 1 / 512 MiB | |
| Minimum instances | **0** | no idle cost |
| Maximum instances | **1** | required, see below |
| Billing | request-based (CPU only during requests, `--cpu-throttling`) | |
| Health checks | Cloud Run's default startup check only; **no database health check** | |
| Ingress / access | private at first (Invoker IAM check on); public by **disabling the Invoker IAM check** after the bootstrap gate (A9) | |

**Maximum instances must stay 1.** Two v1 design choices depend on a single instance:

- one-time sign-in codes (`AuthorizationCodeStore`) live in memory;
- ASP.NET Data Protection keys (Google sign-in state and correlation cookies) are generated inside
  the container and not shared.

A new revision or an instance restart during an in-progress Google sign-in can therefore make that
one sign-in fail; signing in again works. Cloud Run may briefly run an extra instance while it
switches revisions, with the same effect. **Do not scale above 1** without first moving those two
stores to shared storage.

**Public access versus LifeOS authentication.** Cloud Run can require Google Cloud IAM credentials
for every request. LifeOS cannot use that: the system browser must reach
`/api/auth/google/start`, Google must reach `/signin-google`, and the app authenticates with LifeOS
access tokens, not Google Cloud identity tokens. The service is therefore made public at the
**network** level by disabling Cloud Run's Invoker IAM check (`--no-invoker-iam-check`): anyone can
reach the endpoint without Google Cloud credentials. That does **not** make LifeOS endpoints anonymous:
every LifeOS endpoint still requires a valid LifeOS access token except the sign-in, token, refresh
and logout endpoints. Requests without a token get `401` from LifeOS itself.

**Forwarded headers.** The API trusts `X-Forwarded-Proto` / `X-Forwarded-For` from any proxy,
because a Cloud Run container is reachable only through Google's front end. If LifeOS ever runs
anywhere else, review that configuration first (see ADR-006, P2 amendment).

---

## 3. Safe handling patterns

Read these before Part A. Every secret step uses them.

### 3.1 Reading a secret into the session

`Read-Host -AsSecureString` input is not stored in the PowerShell history.

```powershell
$secure = Read-Host -AsSecureString "Value"
$plain  = (New-Object System.Net.NetworkCredential("", $secure)).Password
# ... use $plain ...
$plain = $null; $secure = $null
```

Never type a secret as a command argument, never `echo` it, and never put it in a script file.

### 3.2 Writing a Secret Manager version

**Do not pipe a value into `gcloud ... --data-file=-` from Windows PowerShell 5.1.** It adds a
UTF-8 byte-order mark and a trailing CRLF, which become part of the secret (verified locally: piping
`"abc"` into a native program delivered `EF BB BF 61 62 63 0D 0A`). A Google client secret or a
connection string stored like that is silently broken.

Instead write the exact value to a short-lived file in your user-only temp folder, upload it, and
delete it immediately:

```powershell
function Write-SecretVersion([string] $SecretName, [string] $Value) {
    $file = [IO.Path]::GetTempFileName()
    try {
        [IO.File]::WriteAllText($file, $Value)   # UTF-8, no BOM, no newline
        gcloud secrets versions add $SecretName --project $Project --data-file="$file"
        if ($LASTEXITCODE -ne 0) { throw "gcloud failed for $SecretName" }
    }
    finally {
        Remove-Item -LiteralPath $file -Force
    }
}
```

The function lives only in your session (paste it; it is not a repository script). It prints the
new version number, never the value.

### 3.3 PostgreSQL client in Docker

No native PostgreSQL installation is needed. The official `postgres` image has no CA certificates,
so `verify-full` cannot validate Neon's certificate with it as is. Build a local client image once
(nothing is written to the repository; `ca-certificates` adds the standard public roots):

```powershell
@'
FROM postgres:17
RUN apt-get update && apt-get install -y --no-install-recommends ca-certificates && rm -rf /var/lib/apt/lists/*
'@ | docker build -t lifeos-pgclient:17 -
```

Use the major that matches Neon (`postgres:17` above). `pg_dump` must be at least the server's
major. Rebuild the image occasionally to pick up client patches.

Connection settings travel as `PG*` environment variables: `-e PGPASSWORD` (name only) makes
Docker copy the value from your session, so it never appears in the command:

```powershell
$env:PGHOST        = "<NEON_HOST>"
$env:PGDATABASE    = "<NEON_DATABASE>"
$env:PGUSER        = "<LIFEOS_DB_USER>"
$env:PGSSLMODE     = "verify-full"
$env:PGSSLROOTCERT = "system"
$env:PGPASSWORD    = (New-Object System.Net.NetworkCredential("", (Read-Host -AsSecureString "Password for $env:PGUSER"))).Password

docker run --rm -it `
  -e PGHOST -e PGDATABASE -e PGUSER -e PGPASSWORD -e PGSSLMODE -e PGSSLROOTCERT `
  lifeos-pgclient:17 psql

# When finished:
$env:PGPASSWORD = $null; $env:PGHOST = $null; $env:PGDATABASE = $null; $env:PGUSER = $null
$env:PGSSLMODE = $null; $env:PGSSLROOTCERT = $null
```

While a client container runs, `docker inspect` on this machine can show its environment; the
container is removed when the command ends (`--rm`).

### 3.4 PowerShell pitfalls

- **Windows PowerShell 5.1 strips embedded double quotes from arguments to native programs.**
  `psql -c 'SELECT * FROM "__EFMigrationsHistory"'` arrives without the quotes and fails. Send SQL
  through standard input with a here-string instead:

  ```powershell
  @'
  SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY 1;
  '@ | docker run --rm -i -e PGHOST -e PGDATABASE -e PGUSER -e PGPASSWORD -e PGSSLMODE -e PGSSLROOTCERT lifeos-pgclient:17 psql -v ON_ERROR_STOP=1
  ```

- Use single-quoted here-strings (`@' ... '@`) for SQL and JSON so `$` is not expanded. The closing
  `'@` must start at column 0.
- Clear environment variables with `$env:NAME = $null`.
- Native commands do not stop the script on failure: check `$LASTEXITCODE` after each important one.
- Use `curl.exe`, not `curl` (an alias of `Invoke-WebRequest` in Windows PowerShell).

---

# Part A — First Production release

Order matters. Summary:

1. Prerequisites and a verified release commit
2. Google Cloud project, billing, APIs, budget alert
3. Artifact Registry, image build and push
4. Neon project, checks, dedicated role
5. Database migration
6. Google OAuth (consent screen and Production Web client)
7. Secrets, runtime service account, IAM
8. First deploy — **private**
9. Bootstrap gate, then public access
10. Real Google sign-in and allowlist checks
11. Android release keystore
12. Signed APK
13. Install and Production acceptance
14. First backup and restore drill

The Cloud Run URL is predictable (`https://<service>-<project-number>.<region>.run.app`), so the
Google OAuth client is created with its real redirect URI **before** the first deploy. No placeholder
Google credentials are ever used. The service stays private (Invoker IAM check enabled) until a
gate confirms the URL and the credentials; only then is the Invoker IAM check disabled (A9).

## A1. Prerequisites

### A1.1 Tools

- Docker Desktop (Linux containers), .NET 10 SDK, Microsoft OpenJDK 21, Android SDK (see
  [Android setup](../development/android-setup.md)).
- Google Cloud CLI. **VERIFY AT EXECUTION:** install with `winget install Google.CloudSDK` or the
  official installer, then:

  ```powershell
  gcloud version
  gcloud auth login
  ```

- A password manager for: the Neon owner password, the `lifeos` role password, the keystore
  password.

### A1.2 Verified release commit

```powershell
if (git status --porcelain) { throw "The working tree is not clean." }
git log -1 --oneline

dotnet test tests/dotnet/LifeOS.UnitTests
dotnet test tests/dotnet/LifeOS.IntegrationTests
dotnet test tests/dotnet/LifeOS.ArchitectureTests

# The PostgreSQL suite on the Neon major (17 unless A4 chose another):
$env:LIFEOS_POSTGRES_IMAGE = "postgres:17"
dotnet test tests/dotnet/LifeOS.IntegrationTests --filter "FullyQualifiedName~PostgreSql"
$env:LIFEOS_POSTGRES_IMAGE = $null

dotnet tool run dotnet-ef migrations has-pending-model-changes `
  --project src/dotnet/LifeOS.Infrastructure/LifeOS.Infrastructure.csproj `
  --startup-project src/dotnet/LifeOS.Api/LifeOS.Api.csproj
```

Expected: all tests pass and `No changes have been made to the model since the last migration.`
(otherwise **S3**). Record the commit:

```powershell
$Sha = (git rev-parse --short=12 HEAD).Trim()
git tag v1.0.0     # optional, local
```

## A2. Google Cloud project

**VERIFY AT EXECUTION** (project ids are global; billing may require the console).

```powershell
gcloud projects create $Project --name="LifeOS"
gcloud billing projects link $Project --billing-account="<BILLING_ACCOUNT_ID>"
gcloud config set project $Project
gcloud config set run/region $Region

gcloud services enable run.googleapis.com artifactregistry.googleapis.com secretmanager.googleapis.com iam.googleapis.com --project $Project

gcloud projects describe $Project --format="value(projectNumber)"   # = <GCP_PROJECT_NUMBER>
```

Set `$ProjectNumber` and `$ServiceUrl` (section 1) from the project number.

Budget alert (**VERIFY AT EXECUTION**, simplest in the console): *Billing → Budgets & alerts →
Create budget*, scope: project `<GCP_PROJECT_ID>`, a very small monthly amount in your billing
currency, alerts at 50 %, 90 % and 100 % of actual spend, email to the billing admins. A budget
only alerts; it does not stop spending.

## A3. Artifact Registry and the image

**VERIFY AT EXECUTION** for the `gcloud` commands.

```powershell
gcloud artifacts repositories create lifeos --repository-format=docker --location=$Region --description="LifeOS images" --project $Project
gcloud auth configure-docker "$Region-docker.pkg.dev"

$Image = "$Registry/lifeos-api:$Sha"
docker build --platform linux/amd64 -t $Image .
if ($LASTEXITCODE -ne 0) { throw "docker build failed" }
docker push $Image

gcloud artifacts docker images list $Registry --include-tags
```

Deploy by the immutable `:<git-sha>` tag only. A `latest` tag is optional and never used to deploy.

Clean-up policy (keeps storage small; **VERIFY AT EXECUTION**, run with `--dry-run` first):

```powershell
$policyFile = Join-Path $env:TEMP "lifeos-cleanup.json"
[IO.File]::WriteAllText($policyFile, @'
[
  { "name": "keep-recent", "action": { "type": "Keep" }, "mostRecentVersions": { "keepCount": 5 } },
  { "name": "delete-old",  "action": { "type": "Delete" }, "condition": { "olderThan": "30d" } }
]
'@)
gcloud artifacts repositories set-cleanup-policies lifeos --location=$Region --policy="$policyFile" --dry-run --project $Project
# After reviewing the dry run, repeat without --dry-run.
Remove-Item -LiteralPath $policyFile
```

## A4. Neon

### A4.1 Project and database (console, **VERIFY AT EXECUTION**)

1. Create a Neon project `lifeos`:
   - region **AWS Europe Central 1 (Frankfurt)** (closest to `europe-west3`);
   - PostgreSQL **17** (any offered major from 15 upward works; 17.11 and 18.6 are tested, so
     prefer 17 or 18);
   - keep scale-to-zero / autosuspend enabled and the smallest compute; never set a minimum
     compute that keeps it running.
2. Create a database `lifeos` (owner: the project's default owner role, e.g. `neondb_owner`).
3. In **Connect**, turn connection pooling **off** and note the **direct** host (`<NEON_HOST>`,
   without `-pooler`). Store the owner role's password in the password manager.

Do not copy any local data into Production. Production starts empty.

### A4.2 Connect as the owner

Use section 3.3 with `PGUSER` = the owner role and `PGDATABASE` = `lifeos`.

### A4.3 Checks (stop conditions S1, S2)

```sql
SHOW server_version;
SHOW lc_ctype;
SELECT current_setting('server_version_num')::int >= 150000 AS supported_major;
SELECT lower('ÀÉÎ') = 'àéî' AS unicode_lower;
```

Continue only if `supported_major` and `unicode_lower` are both `t` and `lc_ctype` is a UTF-8
locale (for example `C.UTF-8` or `en_US.UTF-8`). LifeOS needs no extensions.

If the major is not 17, rebuild `lifeos-pgclient` from that major and run the PostgreSQL test
suite with `LIFEOS_POSTGRES_IMAGE=postgres:<major>` before continuing.

### A4.4 Dedicated `lifeos` role

One role is used for migrations and at runtime. It is created **with SQL**, so it gets only the
privileges granted here. Roles created in the Neon console are members of `neon_superuser`
(**VERIFY AT EXECUTION** in the Neon documentation), which LifeOS must not run as.

Choose the role password in the password manager: **letters and digits only, at least 32
characters** (no quoting problems in either connection-string syntax).

In `psql`, connected as the owner to database `lifeos`:

```sql
CREATE ROLE lifeos LOGIN;
\password lifeos
GRANT CONNECT ON DATABASE lifeos TO lifeos;
GRANT USAGE, CREATE ON SCHEMA public TO lifeos;
```

`\password` prompts for the password and sends only its SCRAM hash, so the password is not in the
SQL text, the server log or any file. If Neon rejects it for its strength, choose a longer one.

`lifeos` is not a superuser and cannot create databases or roles. It owns every object the
migrations create. Verify:

```sql
SELECT rolname, rolsuper, rolcreatedb, rolcreaterole FROM pg_roles WHERE rolname = 'lifeos';
```

## A5. Database migration

Production migrations are explicit; the API never migrates at startup.

```powershell
New-Item -ItemType Directory -Force artifacts | Out-Null      # artifacts/ is git-ignored
$MigrationScript = "artifacts/migrate-$Sha.sql"
dotnet tool run dotnet-ef migrations script `
  --idempotent `
  --no-transactions `
  --project src/dotnet/LifeOS.Infrastructure/LifeOS.Infrastructure.csproj `
  --startup-project src/dotnet/LifeOS.Api/LifeOS.Api.csproj `
  --output $MigrationScript
```

(The startup project runs with its local Development configuration to build the model; the
script itself contains no connection information.)

Review the script (**S4**): it must contain only `CREATE TABLE`, `ALTER TABLE`, `CREATE INDEX`,
the `ux_categories_user_sibling_name` index and `__EFMigrationsHistory` rows for the expected
migrations; no `DROP` on a first release, no `CREATE EXTENSION`, no data changes you did not
expect.

Apply it as `lifeos` (section 3.3 variables with `PGUSER=lifeos`), in one transaction that stops at
the first error:

```powershell
docker run --rm `
  -e PGHOST -e PGDATABASE -e PGUSER -e PGPASSWORD -e PGSSLMODE -e PGSSLROOTCERT `
  -v "${PWD}\artifacts:/work:ro" `
  lifeos-pgclient:17 `
  psql --single-transaction -v ON_ERROR_STOP=1 -f "/work/migrate-$Sha.sql"
if ($LASTEXITCODE -ne 0) { throw "Migration failed (nothing was applied)." }
```

Verify:

```powershell
@'
SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY 1;
SELECT tablename, tableowner FROM pg_tables WHERE schemaname = 'public' ORDER BY 1;
'@ | docker run --rm -i -e PGHOST -e PGDATABASE -e PGUSER -e PGPASSWORD -e PGSSLMODE -e PGSSLROOTCERT lifeos-pgclient:17 psql -v ON_ERROR_STOP=1
```

Expected: every migration of the release (currently `..._InitialCreate`,
`..._AddCategorySiblingUniqueness`, `..._AddOpeningBalances`), and every table owned by `lifeos`.
The script is idempotent: running it again changes nothing.

This workflow was rehearsed locally on PostgreSQL 17.11 with a non-superuser role holding exactly
the grants of A4.4: the script applied, re-applied as a no-op, and the API ran as that role.

## A6. Google OAuth (console, **VERIFY AT EXECUTION** for labels)

In the Google Cloud console for `<GCP_PROJECT_ID>`, *Google Auth Platform* (formerly *APIs &
Services → OAuth consent screen / Credentials*):

1. **Branding / consent screen:** app name `LifeOS`, user support and developer contact email.
2. **Audience:** user type **External**, publishing status **Testing**, test users: **only**
   `<PRODUCTION_GOOGLE_EMAIL>`.
3. **Data access / scopes:** `openid`, `email`, `profile` only.
4. **Clients → Create client:**
   - type **Web application**, name `LifeOS API – Production`;
   - authorized JavaScript origins: **none**;
   - authorized redirect URI: **exactly** `<CLOUD_RUN_URL>/signin-google`, i.e.
     `https://lifeos-api-<GCP_PROJECT_NUMBER>.europe-west3.run.app/signin-google`.
5. Copy the client id (`<GOOGLE_CLIENT_ID>`); store the client secret directly with A7.1 (do not
   save it in a file).

No Android OAuth client and no SHA-1 fingerprint: Google only ever redirects to the API.
The Development client (`http://localhost:5050/signin-google`) stays separate; never reuse its
secret. Testing mode limits sign-in to the listed test user; the server-side allowlist (A7.3) is a
second, independent check. LifeOS keeps no Google tokens, so Testing-mode token expiry does not
affect LifeOS sessions.

Redirect-URI changes can take a few minutes to take effect.

## A7. Secrets, service account and IAM

**VERIFY AT EXECUTION** for every `gcloud` command. Paste the `Write-SecretVersion` function
(section 3.2) first.

### A7.1 Secrets

Create the three secrets (empty) with automatic replication (Google chooses the locations; LifeOS
has no secret data-residency requirement, and no customer-managed keys are used):

```powershell
foreach ($name in "lifeos-postgresql", "lifeos-signing-key", "lifeos-google-client-secret") {
    gcloud secrets create $name --replication-policy=automatic --project $Project
}
```

**Connection string** (Npgsql syntax, section 2.2 A):

```powershell
$dbPassword = (New-Object System.Net.NetworkCredential("", (Read-Host -AsSecureString "lifeos role password"))).Password
if ($dbPassword -notmatch '^[A-Za-z0-9]{32,}$') { $dbPassword = $null; throw "Use 32+ letters and digits only." }
$connectionString = "Host=<NEON_HOST>;Database=lifeos;Username=lifeos;Password=$dbPassword;SSL Mode=VerifyFull;GSS Encryption Mode=Disable;Pooling=true;Minimum Pool Size=0;Maximum Pool Size=5;Connection Idle Lifetime=60;Application Name=lifeos-api"
Write-SecretVersion "lifeos-postgresql" $connectionString
$dbPassword = $null; $connectionString = $null
```

**LifeOS signing key** — new for Production, never the Development key, never displayed:

```powershell
$bytes = New-Object byte[] 64
$rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
$rng.GetBytes($bytes); $rng.Dispose()
Write-SecretVersion "lifeos-signing-key" ([Convert]::ToBase64String($bytes))
[Array]::Clear($bytes, 0, $bytes.Length)
```

**Google client secret** (from A6):

```powershell
$clientSecret = (New-Object System.Net.NetworkCredential("", (Read-Host -AsSecureString "Google client secret"))).Password
Write-SecretVersion "lifeos-google-client-secret" $clientSecret.Trim()
$clientSecret = $null
```

Each command prints `Created version [1]`. Cloud Run references these exact versions (`:1`);
a rotation adds a version and redeploys with the new number.

### A7.2 Runtime service account and IAM

```powershell
gcloud iam service-accounts create lifeos-api --display-name="LifeOS API runtime" --project $Project

foreach ($name in "lifeos-postgresql", "lifeos-signing-key", "lifeos-google-client-secret") {
    gcloud secrets add-iam-policy-binding $name --member="serviceAccount:$RuntimeSa" --role="roles/secretmanager.secretAccessor" --project $Project
}
```

That is the only permission the runtime needs: accessor on these three secrets, not on the
project. No Owner, Editor or project-wide Secret Manager role. Cloud Run collects container logs
and pulls images from the project's Artifact Registry through its own service agent.

The person deploying needs permission to deploy Cloud Run and to act as `lifeos-api`
(the project owner has both).

### A7.3 Non-secret settings

```powershell
$ClientId     = "<GOOGLE_CLIENT_ID>"
$AllowedEmail = "<PRODUCTION_GOOGLE_EMAIL>"
```

## A8. First deploy — private

```powershell
gcloud run deploy $Service `
  --image=$Image `
  --region=$Region `
  --project=$Project `
  --service-account=$RuntimeSa `
  --port=8080 `
  --cpu=1 `
  --memory=512Mi `
  --min-instances=0 `
  --max-instances=1 `
  --cpu-throttling `
  --invoker-iam-check `
  --no-allow-unauthenticated `
  --set-env-vars="Authentication__Google__ClientId=$ClientId,Authentication__Google__AllowedEmails__0=$AllowedEmail,Logging__Console__FormatterName=json" `
  --set-secrets="ConnectionStrings__PostgreSQL=lifeos-postgresql:1,Authentication__LifeOS__SigningKey=lifeos-signing-key:1,Authentication__Google__ClientSecret=lifeos-google-client-secret:1"
```

`--invoker-iam-check` keeps the service private: every request needs Google Cloud credentials
with `roles/run.invoker`, and `--no-allow-unauthenticated` grants no public invoker binding.

**VERIFY AT EXECUTION:** the flags exist in gcloud 587; Cloud Run's behaviour is not testable
locally.

### A8.1 Startup (S5)

If the revision fails, read the logs (A8.4). A missing or invalid setting fails startup with a
clear `InvalidOperationException` naming the setting (never its value). In containers an unhandled
startup exception exits with code 139 because `dotnet` is PID 1; that is expected and is reported
as a failed start.

### A8.2 Confirm the URL (S7)

```powershell
gcloud run services describe $Service --region=$Region --project=$Project --format="value(status.url)"
gcloud run services describe $Service --region=$Region --project=$Project --format="yaml(metadata.annotations)"
```

The service has one or two `run.app` URLs (the annotation `run.googleapis.com/urls` lists them).
`$ServiceUrl` must be one of them (normally the `<service>-<project-number>` form). The **same**
URL must be used for the Google redirect URI (A6) and the APK (A12): Google sign-in builds its
redirect URI from the host the browser used. If the deterministic URL is not listed, set
`$ServiceUrl` to the reported URL and change the OAuth client's redirect URI to
`$ServiceUrl/signin-google` before A9 (the client id and secret stay the same).

### A8.3 Private smoke test

Without Google Cloud credentials the service refuses at the IAM layer:

```powershell
curl.exe -s -o NUL -w "%{http_code}`n" "$ServiceUrl/api/me"          # 403 (Cloud Run IAM)
```

Through an authenticated local proxy (**VERIFY AT EXECUTION**), run in a second terminal:

```powershell
gcloud run services proxy $Service --region=$Region --project=$Project --port=8081
```

then:

```powershell
curl.exe -s -o NUL -w "%{http_code}`n" http://localhost:8081/api/me    # 401 (LifeOS itself)
```

`403` comes from Cloud Run, `401` from LifeOS: after A9 only the `401` remains.

### A8.4 Logs (S5, S6)

```powershell
gcloud run services logs read $Service --region=$Region --project=$Project --limit=100
```

Expected: `Now listening on: http://[::]:8080`, `Hosting environment: Production`, the two
Data Protection warnings and the https-port warning. Not acceptable: stack traces at startup,
repeated restarts, SQL command text, tokens, passwords, connection strings.

## A9. Bootstrap gate, then public access

Open the service only when **all** of these are true:

```powershell
gcloud run services describe $Service --region=$Region --project=$Project --format=yaml > "$env:TEMP\lifeos-service.yaml"
Select-String -Path "$env:TEMP\lifeos-service.yaml" -Pattern "Authentication__|ConnectionStrings__|Logging__|DevelopmentSignIn|ASPNETCORE_ENVIRONMENT|maxScale|minScale|invoker-iam|url:" | ForEach-Object { $_.Line.Trim() }
Remove-Item -LiteralPath "$env:TEMP\lifeos-service.yaml"
```

- [ ] `Authentication__Google__ClientId` is the real `<GOOGLE_CLIENT_ID>` from A6.
- [ ] `Authentication__Google__ClientSecret`, `Authentication__LifeOS__SigningKey` and
      `ConnectionStrings__PostgreSQL` are secret references (`lifeos-...`, version `1`), not values.
- [ ] `Authentication__Google__AllowedEmails__0` is set.
- [ ] No `DevelopmentSignIn` and no `ASPNETCORE_ENVIRONMENT` entries.
- [ ] Maximum instances 1, minimum 0.
- [ ] The OAuth client's only redirect URI is `$ServiceUrl/signin-google` (A8.2).
- [ ] The latest revision is Ready and its logs are clean (A8.4).
- [ ] The service is still private: `$ServiceUrl/api/me` without credentials returns `403` (A8.3).

Then disable the Invoker IAM check (**VERIFY AT EXECUTION**):

```powershell
gcloud run services update $Service --region=$Region --project=$Project --no-invoker-iam-check
curl.exe -s -o NUL -w "%{http_code}`n" "$ServiceUrl/api/me"          # now 401 (LifeOS)
```

The Cloud Run endpoint is now publicly callable: Cloud Run no longer checks Google Cloud
credentials. LifeOS's own authentication still protects every endpoint except sign-in, token,
refresh and logout, which is why `/api/me` answers `401` instead of `403`. The
`run.googleapis.com/invoker-iam-disabled` annotation in `gcloud run services describe` shows the
setting.

To make the service private again (e.g. during disaster recovery), run the same command with
`--invoker-iam-check`.

Alternative (fallback only, if disabling the check is not possible in your project): grant the
invoker role to everyone. In an organization with a domain-restricted-sharing policy this binding
may be refused.

```powershell
gcloud run services add-iam-policy-binding $Service --region=$Region --project=$Project --member="allUsers" --role="roles/run.invoker"
# Undo: gcloud run services remove-iam-policy-binding with the same arguments.
```

## A10. Real Google sign-in checks (S7, S8)

Until the APK exists, the start of the flow can be checked from a desktop browser:

1. Open `$ServiceUrl/api/auth/google/start?code_challenge=E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM&redirect_uri=lifeos://auth`
   (a fixed, harmless test challenge). Google's sign-in page must open with **no**
   `redirect_uri_mismatch` error.
2. Sign in with `<PRODUCTION_GOOGLE_EMAIL>`: the browser is sent to `lifeos://auth?code=...`
   (the desktop cannot open it; that is expected — the code expires unused after 60 seconds).
   This creates the LifeOS user (not yet onboarded); the phone sign-in in A13 resolves the same user.
3. Repeat with a different Google account: Google must refuse it (Testing mode, not a test user).
   The server allowlist is the second barrier; it is proven by the automated tests and checked
   again on the phone (A13, item 6).
4. Logs (A8.4): no tokens, codes, emails of refused accounts, or secrets.

Development sign-in does not exist in Production. An anonymous request to
`/api/auth/dev/sign-in` returns `401` (the fallback policy answers every unmapped anonymous path
with 401), so a 401 there proves nothing either way. The proof is the automated test
`AuthEndpointsHttpTests.DevSignIn_NonDevelopmentWithFlagOff_IsNotMapped` (404 with a valid token)
plus the A9 check that no `DevelopmentSignIn` setting exists.

## A11. Android release keystore

Create it once, **outside the repository**, after the API works. It is a permanent LifeOS asset:

> **Losing the keystore or its password means no future update can be installed over the
> installed app.** It would have to be uninstalled (losing nothing on the server, but breaking the
> update path).

```powershell
$env:JAVA_HOME = "C:\Program Files\Microsoft\jdk-21.0.12.101-hotspot"   # your JDK 21
$KeystorePath = "<KEYSTORE_PATH>"     # e.g. a folder outside C:\lifeos

& "$env:JAVA_HOME\bin\keytool.exe" -genkeypair -v `
  -keystore $KeystorePath `
  -storetype PKCS12 `
  -alias lifeos-release `
  -keyalg RSA `
  -keysize 4096 `
  -validity 10000
```

`keytool` prompts for the password and the certificate name fields; never pass `-storepass` or
`-keypass` on the command line. In a PKCS12 keystore the key password is the store password.

Store:

1. the keystore file outside the repository;
2. an encrypted offline backup (e.g. on an encrypted USB drive);
3. the password in the password manager.

Record its fingerprint and checksum (not secret):

```powershell
& "$env:JAVA_HOME\bin\keytool.exe" -list -v -keystore $KeystorePath -alias lifeos-release | Select-String "SHA256:"
Get-FileHash $KeystorePath -Algorithm SHA256
```

Never commit the keystore, its password or an exported private key (`*.keystore` and `*.apk` are
git-ignored). No Google SHA-1 registration is needed.

## A12. Signed APK

```powershell
$env:JAVA_HOME    = "C:\Program Files\Microsoft\jdk-21.0.12.101-hotspot"
$env:ANDROID_HOME = "$env:LOCALAPPDATA\Android\Sdk"
$env:LIFEOS_KEYSTORE_PASSWORD = (New-Object System.Net.NetworkCredential("", (Read-Host -AsSecureString "Keystore password"))).Password

dotnet publish src/dotnet/LifeOS.App/LifeOS.App.csproj `
  -f net10.0-android `
  -c Release `
  -p:LifeOSApiBaseUrl="$ServiceUrl/" `
  -p:AndroidPackageFormat=apk `
  -p:AndroidKeyStore=true `
  -p:AndroidSigningKeyStore="$KeystorePath" `
  -p:AndroidSigningKeyAlias=lifeos-release `
  -p:AndroidSigningStorePass=env:LIFEOS_KEYSTORE_PASSWORD `
  -p:AndroidSigningKeyPass=env:LIFEOS_KEYSTORE_PASSWORD `
  -o artifacts/apk/1.0.0

$env:LIFEOS_KEYSTORE_PASSWORD = $null
```

The `env:` prefix makes the build read the password from the environment variable, so it is never
on the command line. The Release build refuses a missing, non-HTTPS, localhost, loopback or emulator
URL (LIFEOS001–003). **VERIFY AT EXECUTION:** the signed file is
`artifacts/apk/1.0.0/it.colazzo.lifeos-Signed.apk`.

Verify before installing (S9, S10):

```powershell
$Apk = "artifacts\apk\1.0.0\it.colazzo.lifeos-Signed.apk"
$BuildTools = Get-ChildItem "$env:ANDROID_HOME\build-tools" | Sort-Object Name | Select-Object -Last 1

& "$($BuildTools.FullName)\aapt2.exe" dump badging $Apk | Select-Object -First 1
& "$($BuildTools.FullName)\apksigner.bat" verify --print-certs $Apk | Select-String "certificate DN|SHA-256 digest"
```

- `package: name='it.colazzo.lifeos' versionCode='1' versionName='1.0.0'`.
- The signer SHA-256 equals the keystore fingerprint from A11. **`CN=Android Debug` means the
  signing properties were not applied: stop (S10).**
- The URL is the one passed in `LifeOSApiBaseUrl` (the build echoes no warning; it embeds exactly
  that value).

Archive the release outside the repository (or under the git-ignored `artifacts/`):

```powershell
$ReleaseDir = "<private-folder>\LifeOS\releases\1.0.0"
New-Item -ItemType Directory -Force $ReleaseDir | Out-Null
Copy-Item $Apk "$ReleaseDir\LifeOS-1.0.0.apk"
(Get-FileHash "$ReleaseDir\LifeOS-1.0.0.apk" -Algorithm SHA256).Hash | Set-Content -Encoding ascii "$ReleaseDir\LifeOS-1.0.0.sha256.txt"
```

## A13. Install and Production acceptance

```powershell
& "$env:ANDROID_HOME\platform-tools\adb.exe" install "$ReleaseDir\LifeOS-1.0.0.apk"
```

(or copy the APK to the phone and install it there). The Debug app (`it.colazzo.lifeos.dev`) can
stay installed; the schemes differ, so no "Open with" chooser appears. Production needs no
`adb reverse` and no USB connection. Never use development sign-in.

Acceptance on the physical phone:

| # | Check | Expected |
|---|---|---|
| 1 | Install the signed APK | installs as "LifeOS", next to the Debug app |
| 2 | Launch | sign-in screen |
| 3 | Continue with Google | system browser opens Google |
| 4 | Sign in with `<PRODUCTION_GOOGLE_EMAIL>` | returns directly to the Production app |
| 5 | | no app chooser |
| 6 | Sign out, sign in with another Google account | refused, no LifeOS user is created |
| 7 | Onboarding | default currency, starter categories, first account, optional current balance |
| 8 | Home | loads |
| 9 | Portfolio | loads, balances correct |
| 10 | Create an Expense | saved, balances update |
| 11 | Create an Income | saved |
| 12 | Create a Transfer | saved, both accounts update |
| 13 | Analytics | totals match the transactions |
| 14 | Edit a transaction | saved |
| 15 | Delete a transaction | removed, balances update |
| 16 | Accounts management | rename, delete rules behave |
| 17 | Categories management | create, rename, delete rules behave |
| 18 | Sign out | sign-in screen |
| 19 | Sign in again | Home |
| 20 | | all data still there |
| 21 | Force-stop, reopen | |
| 22 | | session restores without signing in |
| 23 | Leave idle > 15 minutes, use again | works (slower first request while Cloud Run and Neon wake up) |
| 24 | Repeat a few steps on mobile data, Wi-Fi off | works |
| 25 | Cloud Run logs after the session (A8.4) | no errors, no secrets, no SQL text |

## A14. First backup

Immediately after acceptance, take the first backup and run the restore drill:
[Backup and restore](backup-restore.md). Do not wait for days.

---

# Part B — Normal future release

| # | Step | Command / reference |
|---|---|---|
| B1 | Working tree clean, release commit chosen | `git status --porcelain` empty; `$Sha = (git rev-parse --short=12 HEAD).Trim()` |
| B2 | All tests pass | A1.2 |
| B3 | PostgreSQL suite on the Neon major passes; `has-pending-model-changes` clean (S3) | A1.2 |
| B4 | Decide what changed: API, schema, App | `git diff --stat <previous-release>..HEAD` |
| B5 | **Fresh Production backup** (always when the schema changes, S12) | [Backup and restore](backup-restore.md) |
| B6 | Generate and review the migration script (if the schema changed) | A5 |
| B7 | Apply the migration | A5 |
| B8 | Build and push the image with the immutable tag | A3 (`docker build`, `docker push`) |
| B9 | Deploy the new revision | below |
| B10 | API smoke: `/api/me` → 401; logs clean (S5, S6) | A8.4 |
| B11 | If the App changed: increment `ApplicationVersion` (and `ApplicationDisplayVersion`), build the signed APK with the **same** keystore, verify, archive, install as an update (`adb install -r`) | A12, A13 |
| B12 | Phone smoke: sign in, Home, create and delete one transaction, Analytics | A13 subset |
| B13 | Backup again if the schema or data changed materially | [Backup and restore](backup-restore.md) |

Deploying a new image keeps the existing configuration:

```powershell
gcloud run deploy $Service --image="$Registry/lifeos-api:$Sha" --region=$Region --project=$Project
```

Migrations are applied **before** the new API revision and must stay compatible with the running
revision until it is replaced (add columns before using them; remove them only in a later release).

Rotating a secret: add a version with `Write-SecretVersion`, then
`gcloud run services update $Service --region=$Region --project=$Project --update-secrets="<ENV_NAME>=<secret>:<new-version>"`,
check the logs, then disable the old version (`gcloud secrets versions disable <old> --secret=<secret>`).
Rotating `lifeos-signing-key` invalidates existing access tokens only; refresh tokens are stored
server-side, so the app obtains a new access token on its next refresh without a new sign-in.

---

# Part C — Rollback

**API.** Route traffic back to the previous revision, provided the database schema is still
compatible with it:

```powershell
gcloud run revisions list --service=$Service --region=$Region --project=$Project
gcloud run services update-traffic $Service --region=$Region --project=$Project --to-revisions="<previous-revision>=100"
```

A later `gcloud run deploy` sends traffic to the new revision again.

**Database.** EF down-migrations are **not** the rollback strategy. If a migration damaged data or
the schema:

1. stop writes where practical (e.g. route traffic to a revision that is not used, or remove
   public access temporarily);
2. either restore the last good backup ([Backup and restore](backup-restore.md), disaster
   recovery), or deploy a corrective forward migration.

**APK.** Android does not install a lower `versionCode` over a higher one. To roll back the app:

1. check out the last good App source;
2. increment `ApplicationVersion` above the bad release;
3. build and sign with the **same** keystore (A12);
4. install it as a normal update.

Never create a new signing key to work around an update problem.
