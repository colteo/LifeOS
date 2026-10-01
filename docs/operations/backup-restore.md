# LifeOS backup and restore

Independent backups of the Production database, the restore drill that proves they work, and
disaster recovery. Neon's own history and restore features are useful, but LifeOS does not rely
only on them: the backups below are separate copies under your control.

Commands use Windows PowerShell 5.1 and the conventions of the
[Production runbook](production-runbook.md): the `lifeos-pgclient:17` client image (runbook 3.3),
`PG*` environment variables, and SQL sent through standard input (runbook 3.4).

> A backup is not trusted until a restore from it has succeeded.

---

## 1. When

| When | Why |
|---|---|
| Right after the first Production acceptance (runbook A13) | before trusting LifeOS with real data |
| Daily, once LifeOS is in real use | |
| Before every schema migration (runbook B5) | mandatory; no backup means stop (runbook S12) |
| After material data or schema changes (runbook B13) | |

Retention:

| Keep | Rule |
|---|---|
| 7 daily | the last 7 daily backups |
| 4 weekly | the first backup of each of the last 4 weeks |
| 12 monthly | the first backup of each of the last 12 months |

Delete older files by hand when you take a new backup. Backups taken before a migration follow the
same rule.

## 2. Where

Backups contain all your financial data. Keep them:

1. in a folder **outside the repository** (never under `C:\lifeos`, so nothing can be staged by
   accident), on a drive protected by BitLocker or equivalent full-disk encryption, e.g.
   `D:\LifeOS-Backups\db`;
2. **plus** a private cloud-synced copy that is encrypted before it leaves the machine (for
   example an encrypted archive or an encrypted vault folder; the password lives in the password
   manager).

Never commit a backup, never attach one to an issue or a chat, never store one unencrypted in a
shared or public folder.

## 3. Taking a backup

```powershell
$BackupDir = "D:\LifeOS-Backups\db"      # outside the repository
New-Item -ItemType Directory -Force $BackupDir | Out-Null

$env:PGHOST        = "<NEON_HOST>"
$env:PGDATABASE    = "neondb"
$env:PGUSER        = "lifeos"
$env:PGSSLMODE     = "verify-full"
$env:PGSSLROOTCERT = "system"
$env:PGPASSWORD    = (New-Object System.Net.NetworkCredential("", (Read-Host -AsSecureString "lifeos role password"))).Password

$DumpName = "lifeos-prod-$(Get-Date -Format 'yyyyMMdd-HHmm').dump"
docker run --rm `
  -e PGHOST -e PGDATABASE -e PGUSER -e PGPASSWORD -e PGSSLMODE -e PGSSLROOTCERT `
  -v "${BackupDir}:/backup" `
  lifeos-pgclient:17 `
  pg_dump --format=custom --no-owner --no-privileges --file="/backup/$DumpName"
$dumpExit = $LASTEXITCODE

$env:PGPASSWORD = $null; $env:PGHOST = $null; $env:PGDATABASE = $null; $env:PGUSER = $null
$env:PGSSLMODE = $null; $env:PGSSLROOTCERT = $null
if ($dumpExit -ne 0) { throw "pg_dump failed" }

(Get-FileHash "$BackupDir\$DumpName" -Algorithm SHA256).Hash | Set-Content -Encoding ascii "$BackupDir\$DumpName.sha256.txt"

# Quick structural check: the archive lists the LifeOS tables.
docker run --rm -v "${BackupDir}:/backup:ro" lifeos-pgclient:17 pg_restore --list "/backup/$DumpName" | Select-String "TABLE DATA"
```

- `--format=custom`: compressed, restorable selectively with `pg_restore`.
- `--no-owner --no-privileges`: the backup restores into any database, owned by whoever restores it.
- The client major must be at least the server's major; rebuild `lifeos-pgclient` if Neon is upgraded.
- Then copy the `.dump` and its `.sha256.txt` to the encrypted cloud copy (section 2).

`pg_dump` holds one consistent snapshot; it does not block the app.

## 4. Restore drill (disposable, local)

Run it after the first backup, after every meaningful schema change, and occasionally otherwise.
Nothing here touches Production.

### 4.1 Restore into a disposable server

A throwaway PostgreSQL of the same major, reachable only from this machine (`127.0.0.1`), with no
password:

```powershell
$BackupDir = "D:\LifeOS-Backups\db"
$DumpName  = "lifeos-prod-<YYYYMMDD-HHMM>.dump"

# Integrity first: the checksum must match the one recorded with the backup.
(Get-FileHash "$BackupDir\$DumpName" -Algorithm SHA256).Hash -eq (Get-Content "$BackupDir\$DumpName.sha256.txt").Trim()

docker run -d --name lifeos-restore-drill `
  -e POSTGRES_HOST_AUTH_METHOD=trust `
  -e POSTGRES_DB=lifeos_restore `
  -p 127.0.0.1:55432:5432 `
  -v "${BackupDir}:/backup:ro" `
  postgres:17
Start-Sleep -Seconds 5

docker exec lifeos-restore-drill pg_restore --username=postgres --dbname=lifeos_restore --no-owner --no-privileges --exit-on-error "/backup/$DumpName"
if ($LASTEXITCODE -ne 0) { throw "Restore failed: stop (runbook S11)." }
```

### 4.2 Verify the data

```powershell
@'
SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY 1;
SELECT 'users'              AS table_name, count(*) FROM users
UNION ALL SELECT 'external_identities', count(*) FROM external_identities
UNION ALL SELECT 'user_sessions',       count(*) FROM user_sessions
UNION ALL SELECT 'accounts',            count(*) FROM accounts
UNION ALL SELECT 'opening_balances',    count(*) FROM opening_balances
UNION ALL SELECT 'categories',          count(*) FROM categories
UNION ALL SELECT 'transactions',        count(*) FROM transactions;
SELECT indexname FROM pg_indexes WHERE indexname = 'ux_categories_user_sibling_name';
'@ | docker exec -i lifeos-restore-drill psql --username=postgres --dbname=lifeos_restore -v ON_ERROR_STOP=1
```

Expected: every migration of the deployed release, row counts that match what you know is in
LifeOS (compare with the app), and the sibling-name index.

### 4.3 Check it through LifeOS (optional, recommended after schema changes)

Point a **local Development API** at the restored database and look at the data with the **Debug
app**:

```powershell
# Terminal 1: Development API on the restored copy (the environment variable overrides User Secrets).
$env:ConnectionStrings__PostgreSQL = "Host=localhost;Port=55432;Database=lifeos_restore;Username=postgres"
dotnet run --project src/dotnet/LifeOS.Api --launch-profile http
# ...when finished: Ctrl+C, then
$env:ConnectionStrings__PostgreSQL = $null
```

Then, with the phone connected (`adb reverse tcp:5050 tcp:5050`, see
[Physical device debugging](../development/physical-device-debugging.md)), sign in to the Debug app
with `<PRODUCTION_GOOGLE_EMAIL>` through the Development Google client. Google identifies the
account with the same subject for both clients, so the restored LifeOS user is found (provider
assumption: verify on the first drill — if a new empty user appears instead, the SQL checks in 4.2
remain the proof). The local Development allowlist must allow that address (empty allows anyone).

Check: account balances, Portfolio, Analytics for a month you know, and the transaction history.
Do not create data you care about here; this copy is thrown away.

### 4.4 Clean up

```powershell
docker rm -f lifeos-restore-drill
```

The restored copy, including session hashes, is gone with the container. Record the drill date and
result next to your backups (e.g. a `restore-drills.txt` file in the backup folder).

This procedure was rehearsed locally with PostgreSQL 17.11: dump with `lifeos-pgclient:17`,
restore with `--no-owner --no-privileges --exit-on-error`, then the checks of 4.2.

## 5. Disaster recovery (restoring Production)

Use when Production data is lost or damaged (for example by a bad migration). EF down-migrations
are not used. **VERIFY AT EXECUTION** for the Neon steps: Neon also offers its own point-in-time
restore of a branch, which may be faster for recent damage; the steps below use your independent
backup.

1. **Stop writes:** suspend the Render web service from its dashboard (*Settings → Suspend*;
   **VERIFY AT EXECUTION** for the label). The app then cannot reach LifeOS until it is resumed.
2. **Back up the damaged state** too (section 3), named `...-damaged.dump`, in case it is needed.
3. **Create an empty database** `lifeos_restored` in the same Neon project (console), then as the
   owner (runbook A3.4):

   ```sql
   GRANT CONNECT ON DATABASE lifeos_restored TO lifeos;
   \c lifeos_restored
   GRANT USAGE, CREATE ON SCHEMA public TO lifeos;
   ```

4. **Restore as `lifeos`**, so the restored objects belong to the LifeOS role:

   ```powershell
   # PG* variables as in section 3, with $env:PGDATABASE = "lifeos_restored"
   docker run --rm `
     -e PGHOST -e PGDATABASE -e PGUSER -e PGPASSWORD -e PGSSLMODE -e PGSSLROOTCERT `
     -v "${BackupDir}:/backup:ro" `
     lifeos-pgclient:17 `
     pg_restore --no-owner --no-privileges --exit-on-error --dbname=lifeos_restored "/backup/$DumpName"
   ```

5. **Verify** with the queries of 4.2 (against `lifeos_restored`).
6. **Switch LifeOS:** in Render, replace the value of `ConnectionStrings__PostgreSQL` with the same
   connection string using `Database=lifeos_restored` (pasted with `Copy-SecretToClipboard`,
   runbook 3.2 and A7.1) and save. When it is safe, resume the service (or deploy it), check the
   logs and run the API smoke test (runbook A8).
7. Keep the damaged database until you are sure nothing else is needed from it, then drop it.
8. Take a fresh backup of the restored database.

Data written after the backup was taken is lost; that is why backups precede every migration.
