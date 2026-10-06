# LifeOS Production runbook

The exact procedure for releasing LifeOS to Production:

- **Part A**: the first Production release (creates every resource).
- **Part B**: every later release.
- **Part C**: rollback.
- **Part D**: adding the AI service `lifeos-ai` to an existing Production (PROD-AI-001, once).
- **Part E**: push notifications with Firebase Cloud Messaging (AUTO-001, once).
- **Part F**: the Weekly Review and the Stage 1 automation tick job (AUTO-002, once).
- **Part G**: Finance reminders and quiet hours (AUTO-003A, once; the Stage 2 tick is a separate decision).

Backups and restores have their own document: [Backup and restore](backup-restore.md).

Production runs on **Render Free** and **Neon Free** (PostgreSQL), in two separate Render accounts:

```text
Android ──HTTPS──► Render account A: lifeos-api (.NET, Docker) ──► Neon PostgreSQL
                         │
                         └──HTTPS + Bearer service key──► Render account B: lifeos-ai (Python, Docker) ──► Groq
```

The app talks only to `lifeos-api`; only `lifeos-api` talks to `lifeos-ai`. The Google Cloud
project is used for Google OAuth and, once Part E is done, for Firebase Cloud Messaging: Firebase is
added to the **same** project (AUTO-001 PD-1), never a second one. Nothing in Production is paid, and
no payment method is registered anywhere.

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
| S17 | `lifeos-ai` answers an anonymous or wrong-key request with anything but `401`, or a `/health/live` body other than `{"status":"ok"}` | D7, D12 |
| S18 | `lifeos-ai` logs contain meal text, a key, or a provider request/response body | D9, D16 |
| S19 | A keepalive job targets anything except a `/health/live` URL, or carries credentials | D14 |
| S20 | Production Firebase setup asks for a Google Cloud project other than `lifeos-production-510310`, a billing account or a paid plan | E2 |
| S21 | Logs contain a push token, the FCM service-account key or the automation tick key | E7, E9 |
| S22 | A push notification shows personal data (amounts, names, emails, meal text) | E8 |
| S23 | `google-services.Release.json` contains a `private_key` (it must be client configuration only) | E3 |
| S24 | The automation tick answers anything but `401` without the key, or the tick job is anything but `POST /api/internal/automation/tick` with the `X-LifeOS-Automation-Key` header (no body, no query string) | F3, F5 |
| S25 | A keepalive job carries the tick key, or the tick job replaces a keepalive job | F5 |

---

## 1. Placeholders and fixed names

Placeholders (never replace them inside this repository):

| Placeholder | Meaning |
|---|---|
| `<GCP_PROJECT_ID>` | Google Cloud project used for OAuth only (currently `lifeos-production-510310`) |
| `<ACTUAL_RENDER_DOMAIN>` | The API service's domain as Render assigns it, e.g. `lifeos-api.onrender.com` |
| `<ACTUAL_AI_RENDER_DOMAIN>` | The AI service's domain as Render assigns it, e.g. `lifeos-ai.onrender.com` (account B) |
| `<NEON_HOST>` | Neon **direct** endpoint host (no `-pooler` in the name) |
| `<NEON_DATABASE>` | `neondb` |
| `<LIFEOS_DB_USER>` | `lifeos` (the dedicated role) |
| `<PRODUCTION_GOOGLE_EMAIL>` | The Production user's Google account |
| `<GOOGLE_CLIENT_ID>` | Production Google Web OAuth client id |
| `<KEYSTORE_PATH>` | Permanent Android keystore, outside the repository |

Fixed names:

| Thing | Name |
|---|---|
| Render account A | holds only `lifeos-api` |
| Render web service (account A) | `lifeos-api` (Free instance type) |
| Source | GitHub repository, branch `main`, root `Dockerfile` |
| Render account B | holds only `lifeos-ai` |
| Render web service (account B) | `lifeos-ai` (Free instance type) |
| Source | same GitHub repository, branch `main`, root directory `src/python/lifeos-ai` (its `Dockerfile`) |
| Android ApplicationId | `it.colazzo.lifeos` (Debug: `it.colazzo.lifeos.dev`) |
| Android auth callback | `lifeos://auth` (Debug: `lifeos-dev://auth`) |
| Keystore alias | `lifeos-release` |

Session variable used by the commands (set it once the domain is known, A7.2):

```powershell
$ServiceUrl = "https://<ACTUAL_RENDER_DOMAIN>"
$AiUrl      = "https://<ACTUAL_AI_RENDER_DOMAIN>"     # Part D, once lifeos-ai exists
```

The URL Render actually assigns is authoritative; if `lifeos-api` (or `lifeos-ai`) is taken, Render
adds a suffix.

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
| `NutritionAi__BaseUrl` | Not secret | `https://<ACTUAL_AI_RENDER_DOMAIN>/` (Part D) |
| `NutritionAi__ServiceKey` | **Secret** | the shared service key (section 3.5, Part D) |
| `NutritionAi__TimeoutSeconds` | Not secret | `120` (section 2.6, Part D) |

| `Notifications__Fcm__ProjectId` | Not secret | `<GCP_PROJECT_ID>` (Part E) |
| `Notifications__Fcm__ServiceAccountJson` | **Secret** | Base64 of the FCM service-account JSON key (Part E) |
| `Automation__TickKey` | **Secret** | at least 32 random visible ASCII characters (Part F, F2) |

The three `NutritionAi__*` variables are added by Part D. Without `NutritionAi__BaseUrl` AI estimation
is disabled and the rest of LifeOS works. With it, the API **refuses to start** unless
`NutritionAi__ServiceKey` holds at least 32 visible ASCII characters, and the URL must be `https`.

The two `Notifications__Fcm__*` variables are added by Part E, and only **together**:

- neither set → push disabled (no sender, the delivery queue is never processed), LifeOS starts
  normally;
- only one set, a malformed project id, a value that is not Base64, or JSON that is not a Google
  **service-account** key → the API **refuses to start**. A partly configured provider is never
  silently disabled.

The same rules apply in every environment. The key and its decoded content are never logged.
`Automation__TickKey` (the AUTO-001 scheduler key) is **not** set in Production until Part F
(AUTO-002) activates the tick job. Without it the tick endpoint is not mapped and no automation runs;
with it the API refuses to start if the key is shorter than 32 visible ASCII characters or the image
has no IANA time zone data.

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
| Health check path | **`/health/live`** | anonymous `{"status":"ok"}` from the process alone; never touches the database (PROD-AI-001; before it, the field was empty and Render used its TCP probe) |
| Disk | none (Free has an ephemeral filesystem) | nothing local is kept |
| Pre-deploy command, background workers, cron jobs | none | migrations are explicit (A4) |

Render Free limitations, deliberately accepted for LifeOS v1:

- no SLA; Render intends Free instances for hobby, testing and personal projects;
- one instance with 0.1 CPU and 512 MB RAM;
- the service **spins down after 15 minutes without inbound traffic**; the next request wakes it,
  which can take about a minute (container start, .NET start-up, first database connection while
  Neon may also be waking). The app waits up to **90 seconds** per request (**210 seconds** for the
  AI-backed Nutrition calls, section 2.6), then shows its "Unable to reach LifeOS" state with Retry.
  During the day the external keepalive (section 2.7) keeps the service awake;
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
**not** make LifeOS endpoints anonymous: every endpoint except sign-in, token, refresh, logout and
the liveness probe `/health/live` requires a LifeOS access token. Requests without one get `401`
from LifeOS. `/health/database` is not mapped in Production.

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

The invariant holds for **both** Render accounts (A: `lifeos-api`, B: `lifeos-ai`).

### 2.5 AI service `lifeos-ai` (Render account B)

A second Render Free web service, in its **own** Render account, built from the same repository.
It is stateless: no database, no disk, no user data beyond the meal text of one estimate.

| Setting | Value |
|---|---|
| Account | **B** (never account A) |
| Name | `lifeos-ai` |
| Runtime | Docker; root directory `src/python/lifeos-ai` (its `Dockerfile` and `.dockerignore`) |
| Branch | `main` |
| Region | Frankfurt (same region as `lifeos-api`) if offered, otherwise the closest EU region |
| Instance type | **Free** |
| Auto-Deploy | **Off** |
| Health check path | `/health/live` |
| Disk, database, background worker, cron job, pre-deploy command | none |

Environment (account B, service → **Environment**):

| Variable | Class | Value |
|---|---|---|
| `GROQ_API_KEY` | **Secret** | the Groq API key |
| `LIFEOS_AI_SERVICE_KEY` | **Secret** | the shared service key (section 3.5); the **same** value as `NutritionAi__ServiceKey` in account A |
| `LIFEOS_AI_NUTRITION_MODEL` | Not secret, optional | `openai/gpt-oss-20b` (the default when absent) |

Never set `PORT` for `lifeos-ai`: Render supplies it and the image listens on it.

The service is publicly addressable at `https://<ACTUAL_AI_RENDER_DOMAIN>`. Only `GET /health/live`
answers anonymously (`{"status":"ok"}`). Every other path, including the detailed `/health` and
`POST /v1/nutrition/estimate-meal`, requires `Authorization: Bearer <service key>` and otherwise
answers `401 {"error":{"code":"unauthorized"}}`. The service refuses to start without a
`LIFEOS_AI_SERVICE_KEY` of at least 32 characters; without `GROQ_API_KEY` it starts and every
estimate is `503 provider_unavailable`.

The same Render Free limitations apply (section 2.3): it sleeps after 15 idle minutes, and a sleeping
service wakes on the next request.

### 2.6 Timeout chain

Every hop has a finite bound; nothing retries automatically except the existing, bounded Python→Groq
attempts.

| Hop | Bound | Set by |
|---|---|---|
| App → `lifeos-api`, every call | 90 s | the app (`ApiTimeouts.Default`) |
| App → `lifeos-api`, Estimate / Analyze day / lazy close only | 210 s | the app (`ApiTimeouts.NutritionAi`) |
| `lifeos-api` → `lifeos-ai`, per estimate | 120 s | `NutritionAi__TimeoutSeconds=120` (account A; default 60) |
| `lifeos-ai` → Groq | 15 s per attempt, ≤ 3 attempts, waits 1 s / 2 s (`Retry-After` ≤ 4 s): ≤ about 53 s | code (ADR-011) |

Why these values: the worst realistic Estimate meets two sequential cold starts — `lifeos-api` wakes
(about a minute), then calls a sleeping `lifeos-ai`, which wakes and calls Groq. 120 s covers the AI
service waking plus Groq's bounded worst case; 210 s covers the API waking plus the full 120 s, so the
app normally receives the API's clean "unavailable" answer (Retry) rather than its own timeout.
Finance, Gym, sign-in and the other Nutrition calls keep 90 s.

Analyze day and lazy close estimate up to 20 meals one after another, each bounded by 120 s, and stop
at the first unavailable one. If such a run outlasts the app's 210 s, nothing is lost: the meals
already estimated are stored and the others stay pending for the next run.

### 2.7 Keepalive (external scheduler)

Both Render Free services are kept awake during waking hours by an **external** HTTP scheduler
(cron-job.org or equivalent). LifeOS itself schedules nothing.

| Job | URL | Method | Schedule (time zone **Europe/Rome**) |
|---|---|---|---|
| `lifeos-api keepalive` | `https://<ACTUAL_RENDER_DOMAIN>/health/live` | GET | every 10 minutes, 07:00–22:50 (`*/10 7-22 * * *`) |
| `lifeos-ai keepalive` | `https://<ACTUAL_AI_RENDER_DOMAIN>/health/live` | GET | every 10 minutes, 07:00–22:50 (`*/10 7-22 * * *`) |

- No headers, no body, no credentials (S19). `/health/live` touches neither PostgreSQL nor Groq.
  Never use an authenticated or product endpoint (Estimate, Analyze, `/api/...`) as a keepalive.
- Expected day: the 07:00 ping wakes each service (that first ping may be slow or time out on the
  scheduler's side; it still wakes the service, and 07:10 succeeds); pings every 10 minutes keep it
  awake; the last ping is at 22:50; with no further traffic Render spins it down about 15 minutes
  later (around 23:05). Overnight nothing pings: opening LifeOS at night accepts the cold start, and
  the services sleep again after 15 idle minutes.
- Expected month: about 16 awake hours per day per service (07:00 to about 23:05), so about 500
  instance hours in a 31-day month, plus any night-time use. Each service is in its own Render account,
  so each uses its own monthly Free allowance (750 instance hours per workspace at the time of
  writing — **VERIFY AT EXECUTION**). Outbound bandwidth for 96 tiny responses per day per service is
  negligible.
- The scheduler account holds no LifeOS secret. Its own login is personal and never written into the
  repository.

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

### 3.5 The shared AI service key

One secret authenticates `lifeos-api` to `lifeos-ai`. It is the **same value** in
`LIFEOS_AI_SERVICE_KEY` (account B, `lifeos-ai`) and `NutritionAi__ServiceKey` (account A,
`lifeos-api`). It is never printed, never written to a file and never kept anywhere but those two
Render environments; if it is lost, a new one is generated and set in both (Part B, rotating a
secret).

Generate it in the PowerShell session that will run Part D, and keep that session open until D10:

```powershell
$bytes = New-Object byte[] 64
$rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
$rng.GetBytes($bytes); $rng.Dispose()
$ServiceKey = [Convert]::ToBase64String($bytes)     # 88 characters, 512 random bits; not displayed
[Array]::Clear($bytes, 0, $bytes.Length)
```

Hand it to each Render dashboard with `Copy-SecretToClipboard $ServiceKey` (section 3.2). When both
services have it (D10):

```powershell
$ServiceKey = $null
```

**Calling `lifeos-ai` without exposing the key.** `curl.exe -H "Authorization: Bearer ..."` would
put the key on a command line and in the history. Use this session-only helper (paste it; it is not a
repository script). The history records `$ServiceKey`, never its value, and nothing is printed except
the status and body:

```powershell
function Invoke-Probe {
    param([string] $Uri, [string] $Method = "GET", [string] $BearerKey, [string] $Json, [int] $TimeoutSec = 180)
    $request = @{ Uri = $Uri; Method = $Method; UseBasicParsing = $true; TimeoutSec = $TimeoutSec; Headers = @{} }
    if ($BearerKey) { $request.Headers.Authorization = "Bearer $BearerKey" }
    if ($Json) { $request.Body = [System.Text.Encoding]::UTF8.GetBytes($Json); $request.ContentType = "application/json" }
    try {
        $response = Invoke-WebRequest @request
        [pscustomobject]@{ Status = [int]$response.StatusCode; Body = $response.Content }
    } catch [System.Net.WebException] {
        if (-not $_.Exception.Response) { throw }
        [pscustomobject]@{ Status = [int]$_.Exception.Response.StatusCode; Body = "$($_.ErrorDetails.Message)" }
    }
}
```

`$_.Exception.Response` is set for HTTP error statuses (401, 503, ...), and Windows PowerShell 5.1
puts their body in `$_.ErrorDetails.Message`; a transport failure or a timeout is rethrown. (Verified
on Windows PowerShell 5.1 against the local `lifeos-ai` image.)

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
| Health check path | `/health/live` |
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
environment holds exactly the seven variables of section 2.1 (not yet the `NutritionAi__*` ones,
which Part D adds) and none of the forbidden ones.

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
curl.exe -s -w " %{http_code}`n" "$ServiceUrl/health/live"      # {"status":"ok"} 200
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
| 23 | Leave idle > 15 minutes (outside keepalive hours, section 2.7), use again | works; the first request can take about a minute (Render and Neon wake up). If it times out after 90 s, Retry works |
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
| B4 | Decide what changed: API, schema, App, AI service (`src/python/lifeos-ai`) | `git diff --stat <previous-release>..HEAD` |
| B4a | If the AI service changed: Python validation and image build (D1); deploy `lifeos-ai` (account B) **before** the API, then repeat D6–D9 | D1, D5–D9 |
| B5 | **Fresh Production backup** (always when the schema changes, S12) | [Backup and restore](backup-restore.md) |
| B6 | Generate and review the migration script (if the schema changed) | A4 |
| B7 | Apply the migration | A4 |
| B8 | Check **both** Render accounts: no payment method, instance type Free, Auto-Deploy Off (S13–S15) | dashboard |
| B9 | Deploy the release commit deliberately | *Manual Deploy → Deploy a specific commit* → `$Sha` (A7.3); wait for **Live** |
| B10 | API smoke: `/health/live` → 200, `/api/me` → 401; Google redirect proof; logs clean (S5, S6, S16) | A8 |
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

Rotating the AI service key: generate a new one (section 3.5), set `LIFEOS_AI_SERVICE_KEY` in account
B, then `NutritionAi__ServiceKey` in account A, and repeat D7–D8 and one phone Estimate. Between the
two saves estimates are briefly unavailable (`401` from `lifeos-ai` is reported as "unavailable");
nothing is lost. Rotate it if it may have been exposed.

---

# Part C — Rollback

**API.** Render keeps recent deploys of the service. Roll back to the last good one (service →
**Events** / deploy history → *Rollback*), provided the database schema is still compatible with
it. **VERIFY AT EXECUTION** how many deploys Free keeps and whether environment changes are part of
a rollback. Alternatively deploy the previous release commit (*Deploy a specific commit*). Each
release's `release.txt` (A11) records which API commit and APK belong together.

**AI service.** `lifeos-ai` (account B) rolls back the same way, independently of the API: it has no
schema and no state. To switch AI off entirely, delete `NutritionAi__BaseUrl` from `lifeos-api`
(account A; delete `NutritionAi__ServiceKey` too) and let it restart: estimates report "unavailable",
everything else, including the meal journal, keeps working. Suspending `lifeos-ai` has the same
effect without touching account A.

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

---

# Part D — AI service deployment (PROD-AI-001)

Adds `lifeos-ai` to the running Production of Part A, once. Later AI-service releases follow Part B
(B4a). Everything is manual: no Render API, no automation. Account A holds `lifeos-api`, account B
holds `lifeos-ai`; each step says which one.

Order (the AI service first, so the API is never configured against a service that does not answer):

| # | Step | Account |
|---|---|---|
| D1 | Verified release commit on `main` (Python and .NET validation, both images build) | — |
| D2 | No migration in PROD-AI-001; if the release also carries one: fresh backup, then B5–B7 | — |
| D3 | Generate the shared service key (section 3.5) | — |
| D4 | Render account B, GitHub connection, create `lifeos-ai` with `GROQ_API_KEY` and `LIFEOS_AI_SERVICE_KEY` | B |
| D5 | Deploy `lifeos-ai`; wait for **Live** | B |
| D6 | `GET /health/live` → 200 | B |
| D7 | Anonymous and wrong-key estimate → 401 | B |
| D8 | Authenticated synthetic estimate → 200 | B |
| D9 | `lifeos-ai` logs: no meal text, key or provider payload | B |
| D10 | Configure `lifeos-api`: `NutritionAi__BaseUrl`, `NutritionAi__ServiceKey`, `NutritionAi__TimeoutSeconds`, health check path | A |
| D11 | Deploy `lifeos-api`; wait for **Live** | A |
| D12 | API `/health/live` → 200, `/api/me` → 401, Google redirect proof | A |
| D13 | New signed APK (the app changed), install as an update | — |
| D14 | Create the two keepalive jobs | scheduler |
| D15 | Verify keepalive history and Render status | scheduler, A, B |
| D16 | Inspect both services' logs for secrets | A, B |
| D17 | Final Production acceptance | — |

Paste the session helpers first: `Copy-SecretToClipboard` (section 3.2) and `Invoke-Probe`
(section 3.5).

## D1. Verified release commit

On the release commit (the PR merged into `main`, working tree clean), run A1.2, plus:

```powershell
Push-Location src/python/lifeos-ai
uv sync --locked
uv run python -m pytest
uv run ruff check .
uv run ruff format --check .
docker build -t lifeos-ai:release-check .
Pop-Location
if ($LASTEXITCODE -ne 0) { throw "AI service validation failed." }
```

All must pass (no network and no credentials are needed). Record `$Sha` as in A1.2.

## D2. Migration and backup

PROD-AI-001 has **no** migration: `has-pending-model-changes` (A1.2) must report no changes, and
the deployed migration history stays as it is. If the release you deploy also contains a migration,
take the fresh backup and apply it first (B5–B7, S12).

## D3. Service key

Generate `$ServiceKey` (section 3.5) in this PowerShell session. Do not close the session before D10.
If it is closed, generate a new key and use the new value everywhere.

## D4. Render account B and the `lifeos-ai` service (**VERIFY AT EXECUTION** for screen labels)

1. Create a second free Render account (a different login from account A). **No payment method**
   (S13), now or later. Keep its workspace for `lifeos-ai` only.
2. Connect GitHub through Render's GitHub app with access to **only the LifeOS repository** where
   possible. (Account A's connection is separate and stays as it is.)
3. *New → Web Service*, source: the LifeOS repository:

| Field | Value |
|---|---|
| Name | `lifeos-ai` |
| Language / runtime | **Docker** |
| Branch | `main` |
| Region | the same region as `lifeos-api` (Frankfurt if offered) |
| Root directory | `src/python/lifeos-ai` |
| Dockerfile path / Docker build context | the root directory's `Dockerfile` and the root directory itself (Render resolves both relative to the root directory; the result must be `src/python/lifeos-ai/Dockerfile` with context `src/python/lifeos-ai`) |
| Instance type | **Free** (S14) |
| Health check path | `/health/live` |
| Auto-Deploy | **Off** (S15) |
| Disk, pre-deploy command, background worker, cron job | none |

Environment variables (section 2.5), entered before creating the service:

- `GROQ_API_KEY`, via the clipboard:

  ```powershell
  $groqKey = (New-Object System.Net.NetworkCredential("", (Read-Host -AsSecureString "Groq API key"))).Password
  Copy-SecretToClipboard $groqKey.Trim()
  $groqKey = $null
  ```

- `LIFEOS_AI_SERVICE_KEY`:

  ```powershell
  Copy-SecretToClipboard $ServiceKey
  ```

- optional `LIFEOS_AI_NUTRITION_MODEL` = `openai/gpt-oss-20b`.

Do not add `PORT`. Check: account B, instance type **Free**, Auto-Deploy **Off**, no disk, no payment
prompt (S13–S15). Create the service.

## D5. Deploy `lifeos-ai`

Creating the service starts its first deploy (from the latest commit of `main`; if that is not
`$Sha`, use *Manual Deploy → Deploy a specific commit* → `$Sha`). Wait until it is **Live**.

The logs must show `Uvicorn running on http://0.0.0.0:<port>` and `Application startup complete.`
A `ServiceKeyError` means `LIFEOS_AI_SERVICE_KEY` is missing or shorter than 32 characters: fix the
variable, do not continue (S5).

Read the assigned domain (service page, top) and set:

```powershell
$AiUrl = "https://<ACTUAL_AI_RENDER_DOMAIN>"
```

## D6. Liveness

```powershell
curl.exe -s -w " %{http_code}`n" "$AiUrl/health/live"
```

Expected: `{"status":"ok"} 200` and nothing else in the body (S17). The first request after a
spin-down can take about a minute.

## D7. Authentication proofs (S17)

```powershell
$Meal = '{"description":"Pollo con le patate","meal_type":"Lunch"}'     # synthetic

Invoke-Probe "$AiUrl/v1/nutrition/estimate-meal" -Method POST -Json $Meal                        # 401
Invoke-Probe "$AiUrl/v1/nutrition/estimate-meal" -Method POST -Json $Meal -BearerKey "wrong-key" # 401
Invoke-Probe "$AiUrl/health"                                                                     # 401
```

Each must be `Status 401` with body `{"error":{"code":"unauthorized"}}`. Anything else: stop (S17).

## D8. Authenticated synthetic estimate (one Groq call)

```powershell
Invoke-Probe "$AiUrl/v1/nutrition/estimate-meal" -Method POST -Json $Meal -BearerKey $ServiceKey
Invoke-Probe "$AiUrl/health" -BearerKey $ServiceKey
```

Expected: the estimate is `Status 200` with `calories_kcal`, `protein_grams`, `carbs_grams`,
`fat_grams` and `assumptions`; the detailed health is `Status 200` with `"configured":true`. A `503
provider_unavailable` means the Groq key is missing or rejected: fix `GROQ_API_KEY`, redeploy, repeat.

## D9. `lifeos-ai` logs (S6, S18)

Service → **Logs**: only start-up lines and access lines such as
`"POST /v1/nutrition/estimate-meal HTTP/1.1" 200 OK`. Not acceptable: the meal text, any key or
`Authorization` value, a Groq request or response body, stack traces at start-up, restart loops.

## D10. Configure `lifeos-api` (account A)

In account A, `lifeos-api` → **Environment**, add (section 2.1):

- `NutritionAi__BaseUrl` = `https://<ACTUAL_AI_RENDER_DOMAIN>/` (plain value; the actual AI domain,
  `https`, trailing slash);
- `NutritionAi__ServiceKey`:

  ```powershell
  Copy-SecretToClipboard $ServiceKey
  ```

- `NutritionAi__TimeoutSeconds` = `120` (section 2.6).

Save the variables without deploying if Render offers that choice. In **Settings**, set **Health
check path** to `/health/live` (it was empty: TCP probe). Then:

```powershell
$ServiceKey = $null
```

The Android app is not involved: it keeps the API URL only, and never receives the AI URL, the
service key or the Groq key.

## D11. Deploy `lifeos-api`

*Manual Deploy → Deploy a specific commit* → `$Sha` (A7.3). Wait until it is **Live**. An
`InvalidOperationException` naming `NutritionAi:ServiceKey` or `NutritionAi:BaseUrl` (never a value)
means the variables are incomplete or the URL is not `https`: fix them (S5).

## D12. API checks

```powershell
curl.exe -s -w " %{http_code}`n" "$ServiceUrl/health/live"          # {"status":"ok"} 200
curl.exe -s -o NUL -w "%{http_code}`n" "$ServiceUrl/api/me"          # 401
curl.exe -s -o NUL -w "%{http_code}`n" "$ServiceUrl/health/database" # 401 (not mapped in Production)
```

Then the Google redirect proof (A8.2, S7, S16).

## D13. App update

PROD-AI-001 changes the app (the 210 s timeout of the AI-backed Nutrition calls, section 2.6). Build,
verify and archive a new signed APK with the **same** keystore and the unchanged API URL, and install
it as an update (B11; `adb install -r`). Without it the phone keeps working with 90 s for every call:
a double cold start may then end in Retry.

Check that the APK carries no AI configuration (it never should):

```powershell
$Extract = "artifacts\apk-check"
Remove-Item -Recurse -Force $Extract -ErrorAction SilentlyContinue
Copy-Item $Apk "$env:TEMP\lifeos-check.zip"
Expand-Archive "$env:TEMP\lifeos-check.zip" $Extract
$AiDomain = ([Uri]$AiUrl).Host
Get-ChildItem -Recurse -File $Extract | Select-String -SimpleMatch -Pattern $AiDomain, "LIFEOS_AI_SERVICE_KEY", "NutritionAi", "GROQ" -List | Select-Object -ExpandProperty Path
Remove-Item -Recurse -Force $Extract; Remove-Item "$env:TEMP\lifeos-check.zip"
```

Expected: no output. (`NutritionAi` also matches nothing: the app has no such setting.)

## D14. Keepalive jobs (cron-job.org or equivalent; **VERIFY AT EXECUTION** for labels)

Create a free account on the external scheduler (personal login; never written into the repository).
Set the account or job time zone to **Europe/Rome**. Create two jobs (section 2.7):

| Field | Job 1 | Job 2 |
|---|---|---|
| Title | `lifeos-api keepalive` | `lifeos-ai keepalive` |
| URL | `https://<ACTUAL_RENDER_DOMAIN>/health/live` | `https://<ACTUAL_AI_RENDER_DOMAIN>/health/live` |
| Method | GET | GET |
| Schedule | custom: every 10 minutes, hours 07–22 (`*/10 7-22 * * *`), every day | same |
| Time zone | Europe/Rome | Europe/Rome |
| Headers, body, authentication | none (S19) | none (S19) |
| Failure notification | optional (e-mail to yourself) | optional |

The first run of the day lands on a sleeping service and may be reported as failed or timed out by
the scheduler; that is expected. If the scheduler offers to disable a job after repeated failures,
keep that threshold above a few consecutive runs, or turn it off.

## D15. Keepalive verification

On the first full day:

- the scheduler's history shows each job running every 10 minutes from 07:00 to 22:50, with `200`
  from 07:10 at the latest, and no run between 23:00 and 06:59;
- Render (accounts A and B, service **Events** / **Logs**) shows the services spinning up around
  07:00 and no spin-down until after 22:50 (**VERIFY AT EXECUTION** how Render displays it);
- `lifeos-api` logs contain no database activity caused by the pings, and `lifeos-ai` logs contain
  only `GET /health/live` lines for them (no Groq calls).

After a week, check each account's Free usage (instance hours, bandwidth) against section 2.7.

## D16. Log inspection (S6, S18)

Read the recent logs of both services (accounts A and B): no service key, Groq key, `Authorization`
value, access or refresh token, connection string, meal text or provider payload.

## D17. Final Production acceptance

**AI service, direct** (D6–D9):

| # | Check | Expected |
|---|---|---|
| 1 | `/health/live` anonymous | `200 {"status":"ok"}` |
| 2 | Estimate anonymous | `401` |
| 3 | Estimate with a wrong bearer token | `401` |
| 4 | Estimate with the service key, synthetic meal | `200`, a valid estimate |
| 5 | Logs | no meal text, secret or provider payload |

**LifeOS API and phone**:

| # | Check | Expected |
|---|---|---|
| 6 | `$ServiceUrl/health/live` | `200 {"status":"ok"}` |
| 7 | Google sign-in on the phone | works as before |
| 8 | Finance: Home, Portfolio, create and delete a transaction | works |
| 9 | Gym: open a program, start and discard a session | works |
| 10 | Nutrition: add, edit, delete a meal with `lifeos-ai` **suspended** (account B, *Suspend*) | works; Estimate reports "unavailable"; nothing is lost |
| 11 | Resume `lifeos-ai`; Estimate a meal (warm) | a proposal; confirm it; the meal shows nutrition |
| 12 | Analyze day | the day's remaining meals are analyzed |
| 13 | Lazy close (a past day with an unanalyzed meal, then open Home) | the past meal gets nutrition |
| 14 | Provider failure: temporarily set an invalid `GROQ_API_KEY` in account B, Estimate | "unavailable", no data changed; then restore the real key (clipboard) and redeploy |
| 15 | Cold AI: outside keepalive hours, leave `lifeos-ai` idle > 15 minutes (API awake), Estimate | eventually a proposal, or a clean Retry state within 210 s |
| 16 | The installed app | API URL unchanged; no AI URL or key in the package (D13) |
| 17 | Render logs of both services after the session | no errors, secrets, SQL text or meal text |

Record the result with the release (`release.txt`, A11): commit `$Sha`, the API and AI URLs, the
date.

---

# Part E — Push notifications (AUTO-001)

Adds Firebase Cloud Messaging (FCM HTTP v1, authenticated with `Google.Apis.Auth`) to an existing
Production. Firebase lives in the **existing** Production Google Cloud project `<GCP_PROJECT_ID>`
(`lifeos-production-510310`, number 959434311075, the project of the Production OAuth client; PD-1),
on the free Spark plan: no billing account, no payment method (S13, S20). AUTO-001 ships **no**
business automation, so after Part E the only notification is the test notification. The automation
tick job is **not** created here (it starts with AUTO-002, Part F).

Production scope only:

| | Production (this part) |
|---|---|
| Google Cloud / Firebase project | `lifeos-production-510310` (959434311075) |
| Android app | `it.colazzo.lifeos` (Release build) |
| App config | `src/dotnet/LifeOS.App/Platforms/Android/google-services.Release.json` |
| Server sender | service account `lifeos-api-fcm` |

The Debug app (`it.colazzo.lifeos.dev`, project `lifeos-510205`, `google-services.Debug.json`)
belongs to the development environment and is **not** part of the Production rollout: no Part E step
creates, changes or depends on it, and the Production API cannot send to Debug installs.

Steps marked **VERIFY AT EXECUTION** depend on the Firebase and Google Cloud consoles.

## E1. Release commit and schema

1. The release commit contains AUTO-001 WP1–WP3B. B1–B4 as for any release.
2. The schema changes (`AddAutomationFoundation`, `AddAutomationExecutions`,
   `AddNotificationDeliveries`): **fresh backup** (B5, S12), then generate, review and apply the
   migration script (B6–B7, A4). Expected: `users.time_zone_id` plus `automation_executions`,
   `device_registrations`, `notification_deliveries`, and nothing else (S4).

## E2. Firebase in the existing project (**VERIFY AT EXECUTION**)

1. In the Firebase console, *Add project* → **choose the existing Google Cloud project**
   `<GCP_PROJECT_ID>` (`lifeos-production-510310`); do not create a new one and do not use the
   development project `lifeos-510205` (S20).
2. Plan: **Spark** (free). Decline Google Analytics (not needed).
3. Make sure the **Firebase Cloud Messaging API (V1)** is enabled for the project (Project settings →
   *Cloud Messaging*). The legacy API and server keys are not used.

## E3. Android app and `google-services.Release.json`

1. Register the Android app `it.colazzo.lifeos` (Release) in `lifeos-production-510310`.
2. Download its `google-services.json` and save it as
   `src/dotnet/LifeOS.App/Platforms/Android/google-services.Release.json`. Its
   `project_info.project_id` must be `lifeos-production-510310` and its `project_number`
   `959434311075`.
3. Check that it is client configuration only: `project_info` and `client` entries with an
   `api_key`, and **no** `private_key` (S23).
4. Release builds include `google-services.Release.json` automatically when the file exists; Debug
   builds never use it (they use `google-services.Debug.json` from the development project). Without
   it the Release app builds and runs with push unavailable.
5. The file is tracked in Git: it is client configuration, not a server secret. Restrict the
   Production Android API key to `it.colazzo.lifeos` and its release signing-certificate SHA-1 in
   Google Cloud (**VERIFY AT EXECUTION**).

## E4. Service account for the API (**VERIFY AT EXECUTION**)

1. In Google Cloud (`<GCP_PROJECT_ID>`) create the service account `lifeos-api-fcm`.
2. Grant only what sending requires: *Firebase Cloud Messaging API Admin*
   (`roles/firebasecloudmessaging.admin`), or a narrower role if the console offers one.
3. Create a **JSON key**. If an organization policy blocks key creation, stop and decide (AUTO-001
   open question 3 notes this); do not work around it.

## E5. Hand the key to Render

```powershell
# The key file was just downloaded; it never enters the repository.
$keyPath = Read-Host "Path of the downloaded JSON key"
$encoded = [Convert]::ToBase64String([IO.File]::ReadAllBytes($keyPath))
Copy-SecretToClipboard $encoded      # section 3.2: paste as Notifications__Fcm__ServiceAccountJson
Remove-Variable encoded
Remove-Item $keyPath                 # delete the local copy once Render has it
```

In the Render dashboard of `lifeos-api` (account A), set:

- `Notifications__Fcm__ServiceAccountJson` (**Secret**) to the pasted value;
- `Notifications__Fcm__ProjectId` to `<GCP_PROJECT_ID>`.

Set both in the same save: one without the other stops the API from starting (by design).

## E6. Deploy order

1. E1: backup and migration.
2. Deploy the release commit (B9). Check the logs (E7).
3. API smoke (B10), plus `POST /api/notifications/test` without a token → `401`.
4. Build the signed Release APK with `google-services.Release.json` in place (A11). Increment
   `ApplicationVersion`, install it as an update (B11).
5. Phone acceptance (E8).

## E7. Logs (S6, S21)

- Startup: no error mentioning `Notifications:Fcm`.
- After a test notification: at most `FCM send not accepted: HTTP <status>, FCM error <CODE>` lines.
- Never a token, a key, a JSON body or an email. Any of those → stop (S21).

## E8. Phone acceptance (physical device)

1. Install or update the app; sign in.
2. Android 13+: the notification permission prompt appears once after sign-in; allow it.
3. The app obtains an FCM token and registers it (`PUT /api/devices/{installationId}` → `204`).
4. Database: one `device_registrations` row for your user and installation, `Active`, with a token.
5. `POST /api/notifications/test` with your access token → `200 {"devices":1,"sent":1,"failed":0}`.
6. App in the background: the notification "LifeOS / Test notification from LifeOS" arrives.
7. App in the foreground: the same notification is shown. Each test has its own key, so a second
   test is a second notification; only a resend of the same delivery replaces its notification.
8. Tapping it opens LifeOS (signed in), with no crash, whether the app was closed or running.
9. Token rotation where practical (e.g. clear app data, sign in again): a new token is registered.
10. Revoke the permission in Android settings, resume the app: the row becomes `Inactive`
    (`PermissionDenied`), token cleared.
11. Sign out: the row becomes `Inactive` (`SignedOut`), token cleared.
12. Sign in again: the row is `Active` again.
13. The time zone sync still works (`users.time_zone_id` matches the phone).
14. No notification ever shows personal data (S22).

## E9. Rotating the FCM key

1. Create a new JSON key for `lifeos-api-fcm` (E4).
2. Replace `Notifications__Fcm__ServiceAccountJson` with it (E5). Render restarts the service.
3. Send a test notification (E8 step 5).
4. Delete the **old** key in Google Cloud.

Rotate immediately if the key may have been exposed.

## E10. Turning push off

Delete **both** `Notifications__Fcm__*` variables and let the service restart. Push is disabled:
nothing is sent, and the test endpoint answers `503`. Device registration and the rest of LifeOS keep
working. Pending deliveries are not sent; they simply expire later.

---

# Part F — Weekly Review and the automation tick (AUTO-002)

Activates the first business automation: the Weekly Review, generated **Sunday 20:00 in each user's
time zone** and announced by the `WeeklyReviewReady` push. Design: [AUTO-002](../tasks/automation/AUTO-002.md);
schedule rationale: [AUTO-001 §18](../tasks/automation/AUTO-001.md#18-rendercron-joborg-production-flow) (Stage 1).

**Nothing in this part is done by the AUTO-002 branch.** It is executed by hand **after** the
release that contains AUTO-002 is merged and chosen for Production. Part E (push) should be done
first; without it reviews are still generated and readable in the app, only the push is not sent.

The automation tick is **not** the keepalive (AUTO-001 PD-8):

| | Keepalive (unchanged) | Automation tick (this part) |
|---|---|---|
| Job | `lifeos-api keepalive` (section 2.7, D14) | `lifeos-api automation tick` (new) |
| Request | `GET /health/live`, no headers | `POST /api/internal/automation/tick`, header `X-LifeOS-Automation-Key`, no body, no query string |
| Touches PostgreSQL | never | yes |
| Schedule | `*/10 7-22 * * *`, time zone Europe/Rome | `*/10 * * * 0,1`, time zone **UTC** (Sunday and Monday) |

The keepalive jobs are **not** edited, replaced or given the key (S19, S25).

| # | Step | Where |
|---|---|---|
| F1 | Release commit with AUTO-002; backup; migration `AddWeeklyReviews` | local, Neon |
| F2 | Generate the tick key | local session |
| F3 | Set `Automation__TickKey` and deploy | Render account A |
| F4 | Manual authenticated tick | local session |
| F5 | Create the Stage 1 tick job | scheduler |
| F6 | First Sunday verification | scheduler, Neon, phone |
| F7 | Log inspection (S6, S21) | Render account A |

## F1. Release commit and schema

1. B1–B4 as for any release.
2. **Fresh backup** (B5, S12), then generate, review and apply the migration script (B6–B7, A4).
   Expected for `AddWeeklyReviews`, and nothing else (S4):
   - `CREATE TABLE weekly_review_settings` (`user_id` PK, `enabled`, `updated_at_utc`; FK to `users`
     `ON DELETE CASCADE`);
   - `CREATE TABLE weekly_reviews` (`id`, `user_id`, `week_start_date`, `week_end_date`,
     `time_zone_id`, `generated_at_utc`, `data_version`, `snapshot jsonb`; checks
     `ck_weekly_reviews_week`, `ck_weekly_reviews_data_version`, `ck_weekly_reviews_snapshot`; FK to
     `users` `ON DELETE CASCADE`);
   - `CREATE UNIQUE INDEX ux_weekly_reviews_user_week_end ON weekly_reviews (user_id, week_end_date)`.

## F2. Generate the tick key

In the PowerShell session that will run F3–F5 (keep it open until F5):

```powershell
$bytes = New-Object byte[] 48
$rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
$rng.GetBytes($bytes); $rng.Dispose()
$TickKey = [Convert]::ToBase64String($bytes)     # 64 visible ASCII characters, 384 random bits; not displayed
[Array]::Clear($bytes, 0, $bytes.Length)
```

The API refuses to start with a key shorter than 32 visible ASCII characters or containing spaces.
The key exists only in Render (`Automation__TickKey`) and in the tick job's header: never in the
repository, a URL, a query string, the app, a file or a log.

## F3. Set `Automation__TickKey` and deploy

1. In the Render dashboard of `lifeos-api` (account A), add `Automation__TickKey` as a **Secret**
   with `Copy-SecretToClipboard $TickKey` (section 3.2).
2. Deploy the release commit (B9) if it is not live yet. Startup with the key also checks that the
   image resolves `Europe/Rome` (tzdata); a failure stops startup (S5).
3. B10 API smoke. Then, without the key, the tick must answer `401` with an empty body (S24):

```powershell
(Invoke-Probe -Uri "$ServiceUrl/api/internal/automation/tick" -Method POST).Status   # 401
```

## F4. Manual authenticated tick

A session-only helper (paste it; it is not a repository script). The history records `$TickKey`,
never its value; nothing is printed except the status and the count-only body:

```powershell
function Invoke-Tick {
    $request = @{ Uri = "$ServiceUrl/api/internal/automation/tick"; Method = "POST"; UseBasicParsing = $true
                  TimeoutSec = 90; Headers = @{ "X-LifeOS-Automation-Key" = $TickKey } }
    try {
        $response = Invoke-WebRequest @request
        [pscustomobject]@{ Status = [int]$response.StatusCode; Body = $response.Content }
    } catch [System.Net.WebException] {
        if (-not $_.Exception.Response) { throw }
        [pscustomobject]@{ Status = [int]$_.Exception.Response.StatusCode; Body = "$($_.ErrorDetails.Message)" }
    }
}

Invoke-Tick    # 200 {"deliveries":0,"executions":0,"more":false} outside the Sunday/Monday window
```

A second call within 60 seconds answers `{"skipped":true}` (single-instance guard). On a weekday no
review is due, so `executions` is `0`; that is the expected result.

## F5. Create the Stage 1 tick job (cron-job.org; **VERIFY AT EXECUTION** for labels)

| Field | Value |
|---|---|
| Title | `lifeos-api automation tick` |
| URL | `https://<ACTUAL_RENDER_DOMAIN>/api/internal/automation/tick` (no query string) |
| Method | `POST` |
| Request body | none |
| Headers | `X-LifeOS-Automation-Key: <the key>`; paste it with `Copy-SecretToClipboard $TickKey` |
| Authentication fields | none (never `Authorization: Bearer`) |
| Time zone | **UTC** |
| Schedule | every 10 minutes on Sunday and Monday: `*/10 * * * 0,1` |
| Timeout | the maximum the service allows (about 30 s); a cold start may time out, the next tick catches up |
| Response history | allowed: the body holds counts only |

Why this window (AUTO-001 §18): every Sunday 20:00 on Earth lies between Sunday 06:00 UTC (UTC+14)
and Monday 08:00 UTC (UTC−12); Europe/Rome is 18:00 UTC (summer) or 19:00 UTC (winter). The 24 h
lateness and the retries (10 and 30 minutes) fit inside Sunday 00:00 → Monday 23:50 UTC. Neon is
woken by ticks only on those two days.

Then clear the key from the session:

```powershell
$TickKey = $null
```

The keepalive jobs (D14) stay exactly as they are (S19, S25).

## F6. First Sunday verification

1. Scheduler history: Sunday ticks answer `200` with counts; occasional timeouts right after a long
   idle period are expected (cold start).
2. After Sunday 20:00 local time (18:00 or 19:00 UTC for Europe/Rome), the phone shows
   "LifeOS / Your weekly review is ready". It contains no amounts, names, meal text or email (S22).
3. Tapping it opens the Weekly Review page of that week, signed in, from a closed and from a running
   app.
4. Modules → Weekly Review lists the review; its Finance, Gym and Nutrition figures match the week.
5. Database (read-only; section 3.3):
   - one `weekly_reviews` row for the user and week (`week_end_date` = that Sunday, `data_version` 1);
   - one `automation_executions` row: `automation_type = 'WeeklyReview'`, `occurrence_key` = that
     Sunday, `status = 'Succeeded'`, `result_id` = the review id;
   - one `notification_deliveries` row per active device with `notification_type =
     'WeeklyReviewReady'`, `resource_type = 'weekly_review'`, `status = 'Sent'`.
6. Monday after the window: no second review, no second push.
7. Measure the cost of Stage 1 during the first weeks (Neon compute hours, Render instance hours;
   AUTO-001 open question 1).

## F7. Logs (S6, S21)

Tick lines may show counts, execution and delivery ids, types, statuses and error codes. Never the
tick key, a push token, an FCM body, an email or review content. Any of those → stop (S21).

## F8. Rotating the tick key

Generate a new key (F2), then replace `Automation__TickKey` in Render and the header of the tick job
in the scheduler together (worst case: one missed tick, caught up by the next one). Rotate
immediately if it may have been exposed.

## F9. Turning the automation off

1. Pause or delete the `lifeos-api automation tick` job.
2. Optionally delete `Automation__TickKey`: the tick endpoint is then not mapped (404) and no handler
   runs; the rest of LifeOS, including reading saved reviews, keeps working.

The user can also switch the automatic review off in the app (Modules → Weekly Review). Saved reviews
are kept in every case.

---

# Part G — Finance reminders (AUTO-003A)

Activates the Finance reminders (recurring occurrence awaiting confirmation, one-off planned expense
due), the per-type reminder preferences and quiet hours. Design: [AUTO-003A](../tasks/automation/AUTO-003A.md).

**Nothing in this part is done by the AUTO-003A branch.** It is executed by hand **after** the release
that contains AUTO-003A is merged and chosen for Production. Parts E and F come first: the reminders
run only where the tick runs (`Automation__TickKey` set) and are pushed only where FCM is configured.

## G1. Release commit and schema

1. B1–B4 as for any release.
2. **Fresh backup** (B5, S12), then generate, review and apply the migration script (B6–B7, A4).
   Expected for `AddNotificationPreferences`, and nothing else (S4):
   - `CREATE TABLE notification_preferences` (`user_id` PK, `recurring_transaction_reminders_enabled`,
     `planned_expense_reminders_enabled`, `quiet_hours_start time`, `quiet_hours_end time`,
     `updated_at_utc`; check `ck_notification_preferences_quiet_hours`; FK
     `FK_notification_preferences_users_user_id` to `users` `ON DELETE CASCADE`).
   No backfill: a user without a row has both reminders on and quiet hours 22:00–08:00.

## G2. Scheduler (unchanged by this part)

The Stage 1 job (`*/10 * * * 0,1`, UTC, Part F) is **not** edited. With Stage 1 only, a reminder is
sent only when its 09:00-local window (24 h) overlaps the Sunday/Monday UTC ticks. Daily reminders need
**Stage 2** (AUTO-001 §18): a tick every day, at least one tick inside every `[09:00 local, +24 h)`
window and after every quiet-hours end; recommended `*/10 * * * *` UTC, or 15–20 minutes once the
Stage 1 Neon/Render cost is measured. Moving to Stage 2 is its own decision and changes only the
schedule of the existing tick job (S24, S25 still apply; keepalives unchanged).

## G3. Verification (after a tick that covers a scheduled date)

1. App → Settings → Notifications shows both switches on and quiet hours 22:00–08:00; saving a change
   and reopening Settings shows it.
2. Database (read-only; section 3.3), for an item scheduled today:
   - one `automation_executions` row with `automation_type` `FinanceRecurringReminder`
     (`occurrence_key` `<rule id>:<yyyy-MM>`) or `FinancePlannedExpenseReminder`
     (`<expense id>:<yyyy-MM-dd>`), `status = 'Succeeded'`, `result_id` NULL;
   - one `notification_deliveries` row per active device, `notification_type`
     `RecurringTransactionReminder` / `PlannedExpenseReminder`, `resource_type` `finance_recurring` /
     `finance_planned_expense`; if created inside quiet hours it stays `Pending` until their end.
   - No Finance row changed (no transaction, no occurrence state, no planned-expense state).
3. Phone: the notification text is only "A recurring transaction needs your confirmation" or "A
   planned expense is due" (no amount, name or account); a tap opens Transactions → Planned.
4. Logs (S6, S21): counts, ids, types, statuses and codes only.
