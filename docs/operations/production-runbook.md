# LifeOS Production runbook

The exact procedure for releasing LifeOS to Production:

- **Part A**: the first Production release (creates every resource).
- **Part B**: every later release.
- **Part C**: rollback.

Backups and restores have their own document: [Backup and restore](backup-restore.md).

Production runs on **Render Free** (the API, a Docker web service) and **Neon Free** (PostgreSQL).
The Google Cloud project is used **only** for Google OAuth. Nothing in Production is paid, and no
payment method is registered anywhere.

Everything here is executed by hand, in **Windows PowerShell 5.1**, from the repository root
(`C:\lifeos`) unless a step says otherwise. Commands are written so they can later move into CI.

Steps marked **VERIFY AT EXECUTION** depend on provider consoles or behaviour that could not be
tested without creating resources: read the provider's current documentation and screens when you
run them.

Provider terms change: **verify the current Render and Neon Free-tier terms before each significant
infrastructure change.** This runbook keeps LifeOS inside them; it cannot guarantee them.

---

## 0. Stop conditions

Stop, do not continue, and investigate if any of these happens:

| # | Stop if | Where it is checked |
|---|---|---|
| S1 | Neon PostgreSQL major is **below 15** | A3.3 |
| S2 | Neon `lc_ctype` is not a UTF-8 locale, or `lower('ÀÉÎ')` is not `àéî` | A3.3 |
| S3 | `has-pending-model-changes` reports changes you did not expect | A1.2, B3 |
| S4 | The migration script fails, or contains anything you did not expect | A4, B6 |
| S5 | A Render deploy fails to start or restarts in a loop (except the planned first failure in A7.2) | A7, A8, B9 |
| S6 | Render logs contain a secret, token, password or connection string | A8.3, A9, B10 |
| S7 | The Google redirect URI differs from the URL the app uses | A6, A8.2 |
| S8 | A Google account that is **not** allowlisted can sign in | A9, A12 |
| S9 | The Release APK points anywhere except the HTTPS Render URL | A11 |
| S10 | The APK is not signed with the permanent release key (`CN=Android Debug` or an unknown certificate) | A11 |
| S11 | A restore drill fails | [Backup and restore](backup-restore.md) |
| S12 | There is no fresh backup before a schema migration (later releases) | B5 |
| S13 | **A payment method is added to Render** | always |
| S14 | The service's instance type is not **Free**, a paid disk, service or database is selected, or Render requires an upgrade to deploy | A7, always |
| S15 | Auto-Deploy is **on** | A7, B9 |
| S16 | The OAuth proof shows an `http://` redirect URI | A8.2 |

---

## 1. Placeholders and fixed names

Placeholders (never replace them inside this repository):

| Placeholder | Meaning |
|---|---|
| `<GCP_PROJECT_ID>` | Google Cloud project used for OAuth only (currently `lifeos-production-510310`) |
| `<ACTUAL_RENDER_DOMAIN>` | The service's domain as Render assigns it, e.g. `lifeos-api.onrender.com` |
| `<NEON_HOST>` | Neon **direct** endpoint host (no `-pooler` in the name) |
| `<NEON_DATABASE>` | `neondb` |
| `<LIFEOS_DB_USER>` | `lifeos` (the dedicated role) |
| `<PRODUCTION_GOOGLE_EMAIL>` | The Production user's Google account |
| `<GOOGLE_CLIENT_ID>` | Production Google Web OAuth client id |
| `<KEYSTORE_PATH>` | Permanent Android keystore, outside the repository |

Fixed names:

| Thing | Name |
|---|---|
| Render web service | `lifeos-api` (Free instance type) |
| Source | GitHub repository, branch `main`, root `Dockerfile` |
| Android ApplicationId | `it.colazzo.lifeos` (Debug: `it.colazzo.lifeos.dev`) |
| Android auth callback | `lifeos://auth` (Debug: `lifeos-dev://auth`) |
| Keystore alias | `lifeos-release` |

Session variable used by the commands (set it once the domain is known, A7.2):

```powershell
$ServiceUrl = "https://<ACTUAL_RENDER_DOMAIN>"
```

The URL Render actually assigns is authoritative; if `lifeos-api` is taken, Render adds a suffix.

---

## 2. Production configuration

### 2.1 Render environment variables

Set in the Render dashboard (service → **Environment**). Values are entered there directly: never
committed, never written to a `render.yaml` (LifeOS v1 has none), never stored in a file.

| Variable | Class | Value |
|---|---|---|
| `ConnectionStrings__PostgreSQL` | **Secret** | Npgsql connection string (section 2.2 A) |
| `Authentication__LifeOS__SigningKey` | **Secret** | new random 64-byte key, Base64 (A7.3) |
| `Authentication__Google__ClientSecret` | **Secret** | from the Production OAuth client (A6) |
| `Authentication__Google__ClientId` | Not secret (it appears in every Google sign-in URL) | `<GOOGLE_CLIENT_ID>` |
| `Authentication__Google__AllowedEmails__0` | Personal data, not a secret | `<PRODUCTION_GOOGLE_EMAIL>` |
| `Logging__Console__FormatterName` | Not secret | `json` |
| `PORT` | Not secret | `8080` (the image listens on 8080; Render routes to `PORT`) |

The allowed email is a plain variable: it is not an authentication secret (the Google sign-in itself
is the authentication), and only you can read the service's environment. It is never written into
the repository.

Never set:

- `ASPNETCORE_ENVIRONMENT` (the image already runs `Production`);
- `Authentication__DevelopmentSignIn__Enabled` (the API refuses to start with it outside Development);
- `ASPNETCORE_FORWARDEDHEADERS_ENABLED` (forwarded headers are configured in code);
- `ASPNETCORE_HTTPS_PORT` (Render terminates TLS; a harmless "Failed to determine the https port
  for redirect" warning is expected in the logs).

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
| `PGUSER` | `<LIFEOS_DB_USER>` (or the Neon owner role during A3) |
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
- **Direct endpoint, not the pooled (PgBouncer) one**: Render Free runs one instance with at most 5
  connections, so a server-side pooler adds behaviour (transaction pooling, session state) without
  benefit, and EF Core transactions keep plain PostgreSQL semantics.

No Render Postgres is used.

### 2.3 Render Free service shape

| Setting | Value | Why |
|---|---|---|
| Instance type | **Free** (0.1 CPU, 512 MB) | zero cost; LifeOS uses about 60–180 MB |
| Instances | one (Free never scales) | required, see below |
| Runtime | Docker, root `Dockerfile`, branch `main` | the P3 image: .NET runtime only, non-root, port 8080 |
| Region | Frankfurt if offered, otherwise the closest EU region | next to Neon (AWS Frankfurt) |
| `PORT` | `8080` | the image's port; the app does not read `PORT` |
| Auto-Deploy | **Off** | every Production deploy is deliberate (Part B) |
| Health check path | **empty** (Render's default TCP probe) | LifeOS endpoints answer 401 anonymously; no health endpoint, no database probe |
| Disk | none (Free has an ephemeral filesystem) | nothing local is kept |
| Pre-deploy command, background workers, cron jobs | none | migrations are explicit (A4) |

Render Free limitations, deliberately accepted for LifeOS v1:

- no SLA; Render intends Free instances for hobby, testing and personal projects;
- one instance with 0.1 CPU and 512 MB RAM;
- the service **spins down after 15 minutes without inbound traffic**; the next request wakes it,
  which can take about a minute (container start, .NET start-up, first database connection while
  Neon may also be waking). The app waits up to **90 seconds** per request, then shows its
  "Unable to reach LifeOS" state with Retry;
- the filesystem is ephemeral; Render may restart Free services at any time;
- no shell or SSH on Free;
- monthly Free usage limits (instance hours, outbound bandwidth, build pipeline minutes).

They are accepted because LifeOS has one personal user, Neon is the system of record, nothing is
stored locally, refresh sessions live in PostgreSQL, and zero cost matters more than latency.

**One instance.** Two v1 choices rely on it: one-time sign-in codes (`AuthorizationCodeStore`) live
in memory, and ASP.NET Data Protection keys (Google sign-in state and correlation cookies) are
generated inside the container. Render Free never runs more than one instance.

**Auth state across restarts** (spin-down, deploy, or a Render restart):

| State | After a restart |
|---|---|
| A Google sign-in in progress | may fail (its state and one-time code are lost); sign in again |
| LifeOS access tokens | still valid (the signing key comes from the environment) |
| Refresh sessions | kept (PostgreSQL); the app restores its session normally |

No persistent Data Protection storage is used in v1.

**Public access versus LifeOS authentication.** The `onrender.com` endpoint is public: the system
browser must reach `/api/auth/google/start` and Google must reach `/signin-google`. That does
**not** make LifeOS endpoints anonymous: every endpoint except sign-in, token, refresh and logout
requires a LifeOS access token. Requests without one get `401` from LifeOS.

**Forwarded headers.** Render's edge proxy terminates TLS and forwards plain HTTP with
`X-Forwarded-Proto` / `X-Forwarded-For`. The API trusts those headers from any proxy, because a
Render Free web service is reachable only through Render's public edge proxy (Free web services
cannot receive private-network traffic). Keep the service alone in the Render workspace. If LifeOS
ever moves away from Render Free, or private networking is used, review that trust configuration
first (see ADR-006, P2 amendment).

### 2.4 No payment method — Production invariant

**Never add a payment method to the Render workspace** (S13). Without one, Render cannot charge:
if the included outbound bandwidth is exceeded, Render **suspends** the Free services for the rest
of the month; if the build pipeline allowance is exceeded, Render **disables new builds** for the
rest of the month (the running service keeps its last build); if the Free instance hours run out,
Free services are suspended until the next month. Each of these means LifeOS is unavailable, not
billed.

Stop (S14) if the plan is not Free, if a paid disk, service or database is proposed, or if Render
says an upgrade is required to deploy. Neon Free also runs without a payment method; the Google
Cloud project needs no billing for OAuth (A2).

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

### 3.2 Handing a secret to the Render dashboard

Secrets go into Render's dashboard through the clipboard, never through a file or the console.
`Set-Clipboard` copies the exact value (no added newline; verified on Windows PowerShell 5.1).

Before starting, turn **off** Windows clipboard history (Settings → System → Clipboard), or clear it
afterwards (Win+V → Clear all), so the value is not kept or synced.

```powershell
function Copy-SecretToClipboard([string] $Value) {
    Set-Clipboard -Value $Value
    Read-Host "Copied. Paste it into Render, save, then press Enter to clear the clipboard" | Out-Null
    Set-Clipboard -Value $null
}
```

The function lives only in your session (paste it; it is not a repository script). It never prints
the value.

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

- **Do not pipe a secret into a native program** (`"value" | some.exe`): Windows PowerShell 5.1
  adds a UTF-8 byte-order mark and a trailing CRLF (verified locally: `"abc"` arrived as
  `EF BB BF 61 62 63 0D 0A`).
- Use single-quoted here-strings (`@' ... '@`) for SQL and JSON so `$` is not expanded. The closing
  `'@` must start at column 0.
- Clear environment variables with `$env:NAME = $null`.
- Native commands do not stop the script on failure: check `$LASTEXITCODE` after each important one.
- Use `curl.exe`, not `curl` (an alias of `Invoke-WebRequest` in Windows PowerShell).

---

# Part A — First Production release

Order matters. Summary:

1. Prerequisites and a verified release commit
2. Google Cloud project: OAuth only
3. Neon project, checks, dedicated role
4. Database migration (Neon is empty until now)
5. Render account and GitHub connection — no payment method
6. Google OAuth consent screen (the client comes in A7.2, once the Render domain is known)
7. Render web service: create, first deploy fails by design, OAuth client, environment, deploy
8. OAuth proof — no APK before it passes
9. Real Google sign-in and allowlist checks
10. Android release keystore
11. Signed APK
12. Install and Production acceptance
13. First backup and restore drill

The Render domain is known only once the service exists, and the API refuses to start without
Google credentials. So the service is created **without** the Google variables: its first deploy
fails at start-up (nothing is served), the real OAuth client is then created for the actual domain,
the two Google variables are added, and the service is deployed. No placeholder credentials are
ever used.

## A1. Prerequisites

### A1.1 Tools and accounts

- Docker Desktop (Linux containers), .NET 10 SDK, Microsoft OpenJDK 21, Android SDK (see
  [Android setup](../development/android-setup.md)).
- Access to the GitHub repository that holds LifeOS.
- A password manager for: the Neon owner password, the `lifeos` role password, the keystore
  password.

### A1.2 Verified release commit

```powershell
if (git status --porcelain) { throw "The working tree is not clean." }
git log -1 --oneline

dotnet test tests/dotnet/LifeOS.UnitTests
dotnet test tests/dotnet/LifeOS.IntegrationTests
dotnet test tests/dotnet/LifeOS.ArchitectureTests

# The PostgreSQL suite on the Neon major (17 unless A3 chose another):
$env:LIFEOS_POSTGRES_IMAGE = "postgres:17"
dotnet test tests/dotnet/LifeOS.IntegrationTests --filter "FullyQualifiedName~PostgreSql"
$env:LIFEOS_POSTGRES_IMAGE = $null

dotnet tool run dotnet-ef migrations has-pending-model-changes `
  --project src/dotnet/LifeOS.Infrastructure/LifeOS.Infrastructure.csproj `
  --startup-project src/dotnet/LifeOS.Api/LifeOS.Api.csproj

# Optional: the image Render will build, built locally first.
docker build -t lifeos-api:release-check .
```

Expected: all tests pass and `No changes have been made to the model since the last migration.`
(otherwise **S3**). The release commit must be pushed to `main`, because Render builds from GitHub.
Record it:

```powershell
$Sha = (git rev-parse HEAD).Trim()          # full SHA, used to pick the commit in Render
$ShortSha = $Sha.Substring(0, 12)
git tag v1.0.0     # optional
```

## A2. Google Cloud project (OAuth only)

The existing project `<GCP_PROJECT_ID>` is used **only** for the Google Auth Platform (consent
screen and the Production Web OAuth client). Do not configure Cloud Run, Artifact Registry, Secret
Manager or any service account for LifeOS.

Billing (**VERIFY AT EXECUTION**, console *Billing*):

- if no billing account is linked, leave it unlinked;
- if one was linked only for the abandoned Cloud Run plan, unlink it, then confirm that the
  Google Auth Platform pages and the OAuth client remain available. Do not delete the project.

## A3. Neon

### A3.1 Project and database (console, **VERIFY AT EXECUTION**)

1. Create a Neon project `lifeos` on the Free plan, **without a payment method**:
   - region **AWS Europe Central 1 (Frankfurt)**;
   - PostgreSQL **17** (any offered major from 15 upward works; 17.11 and 18.6 are tested, so
     prefer 17 or 18);
   - keep scale-to-zero / autosuspend enabled and the smallest compute; never set a minimum
     compute that keeps it running.
2. The project's default database is `neondb` (owner: the project's default owner role, e.g.
   `neondb_owner`). LifeOS uses it.
3. In **Connect**, turn connection pooling **off** and note the **direct** host (`<NEON_HOST>`,
   without `-pooler`). Store the owner role's password in the password manager.

Do not copy any local data into Production. Production starts empty.

### A3.2 Connect as the owner

Use section 3.3 with `PGUSER` = the owner role and `PGDATABASE` = `neondb`.

### A3.3 Checks (stop conditions S1, S2)

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

### A3.4 Dedicated `lifeos` role

One role is used for migrations and at runtime. It is created **with SQL**, so it gets only the
privileges granted here. Roles created in the Neon console are members of `neon_superuser`
(**VERIFY AT EXECUTION** in the Neon documentation), which LifeOS must not run as.

Choose the role password in the password manager: **letters and digits only, at least 32
characters** (no quoting problems in either connection-string syntax).

In `psql`, connected as the owner to database `neondb`:

```sql
CREATE ROLE lifeos LOGIN;
\password lifeos
GRANT CONNECT ON DATABASE neondb TO lifeos;
GRANT USAGE, CREATE ON SCHEMA public TO lifeos;
```

`\password` prompts for the password and sends only its SCRAM hash, so the password is not in the
SQL text, the server log or any file. If Neon rejects it for its strength, choose a longer one.

`lifeos` is not a superuser and cannot create databases or roles. It owns every object the
migrations create. Verify:

```sql
SELECT rolname, rolsuper, rolcreatedb, rolcreaterole FROM pg_roles WHERE rolname = 'lifeos';
```

## A4. Database migration

Production migrations are explicit; the API never migrates at startup, and Render has no
pre-deploy command for LifeOS.

```powershell
New-Item -ItemType Directory -Force artifacts | Out-Null      # artifacts/ is git-ignored
$MigrationScript = "artifacts/migrate-$ShortSha.sql"
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
  psql --single-transaction -v ON_ERROR_STOP=1 -f "/work/migrate-$ShortSha.sql"
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
the grants of A3.4: the script applied, re-applied as a no-op, and the API ran as that role.

## A5. Render account and GitHub (**VERIFY AT EXECUTION** for screen labels)

1. Create a free Render account and workspace. **Do not add a payment method** (S13), now or later.
2. Connect GitHub through Render's GitHub app and grant access to **only the LifeOS repository**
   (*Only select repositories*) where possible.
3. Keep the workspace for LifeOS only: no other services (section 2.3, forwarded headers).

## A6. Google OAuth consent screen (console, **VERIFY AT EXECUTION** for labels)

In the Google Cloud console for `<GCP_PROJECT_ID>`, *Google Auth Platform* (formerly *APIs &
Services → OAuth consent screen / Credentials*):

1. **Branding / consent screen:** app name `LifeOS`, user support and developer contact email.
2. **Audience:** user type **External**, publishing status **Testing**, test users: **only**
   `<PRODUCTION_GOOGLE_EMAIL>`.
3. **Data access / scopes:** `openid`, `email`, `profile` only.

The Web client is created in A7.2, once the actual Render domain is known.

No Android OAuth client and no SHA-1 fingerprint: Google only ever redirects to the API.
The Development client (`http://localhost:5050/signin-google`) stays separate; never reuse its
secret. Testing mode limits sign-in to the listed test user; the server-side allowlist is a second,
independent check. LifeOS keeps no Google tokens, so Testing-mode token expiry does not affect
LifeOS sessions.

## A7. Render web service

### A7.1 Create the service (**VERIFY AT EXECUTION** for screen labels)

*New → Web Service*, source: the LifeOS GitHub repository.

| Field | Value |
|---|---|
| Name | `lifeos-api` |
| Language / runtime | **Docker** |
| Branch | `main` |
| Region | Frankfurt (EU Central) if offered, otherwise the closest EU region |
| Root directory | empty (repository root) |
| Dockerfile path | `./Dockerfile` (Docker build context: the repository root) |
| Instance type | **Free** (S14) |
| Health check path | **empty** (default TCP probe) |
| Auto-Deploy | **Off** (S15; under *Advanced* or the service *Settings*) |
| Disk, pre-deploy command | none |
| Environment variables | see below — **without** the two Google variables |

Environment variables at creation (section 2.1): `PORT=8080`, `Logging__Console__FormatterName=json`,
`Authentication__Google__AllowedEmails__0`, and the two secrets below. Paste the
`Copy-SecretToClipboard` function (section 3.2) first.

**Connection string** (Npgsql syntax, section 2.2 A):

```powershell
$dbPassword = (New-Object System.Net.NetworkCredential("", (Read-Host -AsSecureString "lifeos role password"))).Password
if ($dbPassword -notmatch '^[A-Za-z0-9]{32,}$') { $dbPassword = $null; throw "Use 32+ letters and digits only." }
Copy-SecretToClipboard "Host=<NEON_HOST>;Database=neondb;Username=lifeos;Password=$dbPassword;SSL Mode=VerifyFull;GSS Encryption Mode=Disable;Pooling=true;Minimum Pool Size=0;Maximum Pool Size=5;Connection Idle Lifetime=60;Application Name=lifeos-api"
$dbPassword = $null
```

Paste it as the value of `ConnectionStrings__PostgreSQL`.

**LifeOS signing key** — new for Production, never the Development key, never displayed:

```powershell
$bytes = New-Object byte[] 64
$rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
$rng.GetBytes($bytes); $rng.Dispose()
Copy-SecretToClipboard ([Convert]::ToBase64String($bytes))
[Array]::Clear($bytes, 0, $bytes.Length)
```

Paste it as the value of `Authentication__LifeOS__SigningKey`. Render keeps the only copy; a lost
key is simply replaced (Part B, rotating a secret).

Check before creating: instance type **Free**, Auto-Deploy **Off**, no disk, no payment prompt
(S13–S15). Create the service.

### A7.2 First deploy fails by design; create the OAuth client

Creating the service starts a first deploy. Without Google credentials the API stops at start-up
with an `InvalidOperationException` naming `Authentication:Google:ClientId` (never a value); in
containers that exits with code 139 because `dotnet` is PID 1. Nothing is served. This failure is
expected (not S5) and also proves the Production fail-fast.

Read the domain Render assigned (service page, top) and set:

```powershell
$ServiceUrl = "https://<ACTUAL_RENDER_DOMAIN>"
```

In *Google Auth Platform → Clients → Create client*:

- type **Web application**, name `LifeOS API – Production`;
- authorized JavaScript origins: **none**;
- authorized redirect URI: **exactly** `https://<ACTUAL_RENDER_DOMAIN>/signin-google`.

Redirect-URI changes can take a few minutes to take effect.

Add the two Google variables in Render (*Environment*):

- `Authentication__Google__ClientId` = `<GOOGLE_CLIENT_ID>`;
- `Authentication__Google__ClientSecret`, via the clipboard:

  ```powershell
  $clientSecret = (New-Object System.Net.NetworkCredential("", (Read-Host -AsSecureString "Google client secret"))).Password
  Copy-SecretToClipboard $clientSecret.Trim()
  $clientSecret = $null
  ```

Save the variables without deploying if Render offers that choice, then check that the
environment holds exactly the seven variables of section 2.1 and none of the forbidden ones.

### A7.3 Deploy the release commit

*Manual Deploy → Deploy a specific commit* → `$Sha` (or *Deploy latest commit* if `main` is
exactly `$Sha`). **VERIFY AT EXECUTION.** Wait until the deploy is **Live**.

The logs (service → **Logs**) must show `Now listening on: http://[::]:8080`,
`Hosting environment: Production`, the two Data Protection warnings and the https-port warning.
Not acceptable: stack traces at startup, repeated restarts, SQL command text, tokens, passwords,
connection strings (S5, S6).

## A8. OAuth proof — before any APK

### A8.1 API reachable

```powershell
curl.exe -s -o NUL -w "%{http_code}`n" "$ServiceUrl/api/me"      # 401 (LifeOS itself)
```

The first request after a spin-down can take about a minute.

### A8.2 Google redirect (S7, S16)

```powershell
curl.exe -s -D - -o NUL `
  "$ServiceUrl/api/auth/google/start?code_challenge=E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM&redirect_uri=lifeos://auth"
```

Expected:

- `HTTP/1.1 302` (or `HTTP/2 302`);
- `Location: https://accounts.google.com/...` whose `redirect_uri` parameter is exactly
  `https%3A%2F%2F<ACTUAL_RENDER_DOMAIN>%2Fsignin-google` (that is,
  `https://<ACTUAL_RENDER_DOMAIN>/signin-google`);
- a `Set-Cookie: .AspNetCore.Correlation...` header containing `secure`.

**If the `redirect_uri` starts with `http://`, stop (S16):** the forwarded headers are not being
applied. Do not build the APK until this proof passes.

The code challenge is a fixed, harmless test value; `redirect_uri=lifeos://auth` is the Production
app's callback, the only one the Production API accepts.

### A8.3 Logs

Check the Render logs again after the proof: no tokens, codes, secrets or connection strings (S6).

## A9. Real Google sign-in checks (S7, S8)

From a desktop browser:

1. Open `$ServiceUrl/api/auth/google/start?code_challenge=E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM&redirect_uri=lifeos://auth`.
   Google's sign-in page must open with **no** `redirect_uri_mismatch` error.
2. Sign in with `<PRODUCTION_GOOGLE_EMAIL>`: the browser is sent to `lifeos://auth?code=...`
   (the desktop cannot open it; that is expected — the code expires unused after 60 seconds).
   This creates the LifeOS user (not yet onboarded); the phone sign-in in A12 resolves the same user.
3. Repeat with a different Google account: Google must refuse it (Testing mode, not a test user).
   The server allowlist is the second barrier; it is proven by the automated tests and checked
   again on the phone (A12, item 6).
4. Logs: no tokens, codes, emails of refused accounts, or secrets.

Development sign-in does not exist in Production. An anonymous request to
`/api/auth/dev/sign-in` returns `401` (the fallback policy answers every unmapped anonymous path
with 401), so a 401 there proves nothing either way. The proof is the automated test
`AuthEndpointsHttpTests.DevSignIn_NonDevelopmentWithFlagOff_IsNotMapped` (404 with a valid token)
plus the A7.2 check that no `DevelopmentSignIn` variable exists.

## A10. Android release keystore

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

## A11. Signed APK

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
- The signer SHA-256 equals the keystore fingerprint from A10. **`CN=Android Debug` means the
  signing properties were not applied: stop (S10).**
- The URL is the one passed in `LifeOSApiBaseUrl` (the build embeds exactly that value).

Archive the release outside the repository (or under the git-ignored `artifacts/`), with a record
of what was released:

```powershell
$ReleaseDir = "<private-folder>\LifeOS\releases\1.0.0"
New-Item -ItemType Directory -Force $ReleaseDir | Out-Null
Copy-Item $Apk "$ReleaseDir\LifeOS-1.0.0.apk"
(Get-FileHash "$ReleaseDir\LifeOS-1.0.0.apk" -Algorithm SHA256).Hash | Set-Content -Encoding ascii "$ReleaseDir\LifeOS-1.0.0.sha256.txt"
@"
API commit: $Sha
API URL:    $ServiceUrl
APK:        LifeOS-1.0.0.apk (versionCode 1)
Date:       $(Get-Date -Format 'yyyy-MM-dd HH:mm')
"@ | Set-Content -Encoding ascii "$ReleaseDir\release.txt"
```

## A12. Install and Production acceptance

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
| 23 | Leave idle > 15 minutes, use again | works; the first request can take about a minute (Render and Neon wake up). If it times out after 90 s, Retry works |
| 24 | Repeat a few steps on mobile data, Wi-Fi off | works |
| 25 | Render logs after the session | no errors, no secrets, no SQL text |

## A13. First backup

Immediately after acceptance, take the first backup and run the restore drill:
[Backup and restore](backup-restore.md). Do not wait for days.

---

# Part B — Normal future release

| # | Step | Command / reference |
|---|---|---|
| B1 | Working tree clean, release commit chosen and pushed to `main` | `git status --porcelain` empty; `$Sha = (git rev-parse HEAD).Trim()` |
| B2 | All tests pass | A1.2 |
| B3 | PostgreSQL suite on the Neon major passes; `has-pending-model-changes` clean (S3) | A1.2 |
| B4 | Decide what changed: API, schema, App | `git diff --stat <previous-release>..HEAD` |
| B5 | **Fresh Production backup** (always when the schema changes, S12) | [Backup and restore](backup-restore.md) |
| B6 | Generate and review the migration script (if the schema changed) | A4 |
| B7 | Apply the migration | A4 |
| B8 | Check the Render workspace: no payment method, instance type Free, Auto-Deploy Off (S13–S15) | dashboard |
| B9 | Deploy the release commit deliberately | *Manual Deploy → Deploy a specific commit* → `$Sha` (A7.3); wait for **Live** |
| B10 | API smoke: `/api/me` → 401; Google redirect proof; logs clean (S5, S6, S16) | A8 |
| B11 | If the App changed: increment `ApplicationVersion` (and `ApplicationDisplayVersion`), build the signed APK with the **same** keystore, verify, archive with `release.txt`, install as an update (`adb install -r`) | A11, A12 |
| B12 | Phone smoke: sign in, Home, create and delete one transaction, Analytics | A12 subset |
| B13 | Backup again if the schema or data changed materially | [Backup and restore](backup-restore.md) |

A push to `main` never deploys Production by itself (Auto-Deploy is off). Migrations are applied
**before** the deploy and must stay compatible with the running build until it is replaced (add
columns before using them; remove them only in a later release).

Changing an environment variable restarts the service with the new value (**VERIFY AT EXECUTION**
whether Render asks to deploy). Rotating a secret: generate or obtain the new value, paste it with
`Copy-SecretToClipboard`, save, check the logs. Rotating `Authentication__LifeOS__SigningKey`
invalidates existing access tokens only; refresh tokens are stored server-side, so the app obtains a
new access token on its next refresh without a new sign-in.

---

# Part C — Rollback

**API.** Render keeps recent deploys of the service. Roll back to the last good one (service →
**Events** / deploy history → *Rollback*), provided the database schema is still compatible with
it. **VERIFY AT EXECUTION** how many deploys Free keeps and whether environment changes are part of
a rollback. Alternatively deploy the previous release commit (*Deploy a specific commit*). Each
release's `release.txt` (A11) records which API commit and APK belong together.

**Database.** EF down-migrations are **not** the rollback strategy. If a migration damaged data or
the schema:

1. stop writes: **suspend** the Render web service from its dashboard (*Settings → Suspend*);
2. either restore the last good backup ([Backup and restore](backup-restore.md), disaster
   recovery), or deploy a corrective forward migration;
3. resume the service (or deploy) when it is safe.

**APK.** Android does not install a lower `versionCode` over a higher one. To roll back the app:

1. check out the last good App source;
2. increment `ApplicationVersion` above the bad release;
3. build and sign with the **same** keystore (A11);
4. install it as a normal update.

Never create a new signing key to work around an update problem.
