# Local Development

The canonical quick start: from a fresh clone on **Windows** to PostgreSQL,
migrations, the API and the Android app running locally. All commands are
PowerShell and run from the **repository root** unless stated otherwise.

```text
Android app (MAUI Blazor Hybrid, emulator or phone)
    ↓ HTTP (port 5050)
LifeOS.Api (ASP.NET Core)
    ↓
Application / Domain
    ↓
EF Core / Infrastructure
    ↓
PostgreSQL (Docker, port 5432)
```

Architecture background:
- [overview.md](../architecture/overview.md)
- [dependency-rules.md](../architecture/dependency-rules.md)

---

## 1. Prerequisites

- Git and PowerShell
- .NET 10 SDK with the .NET MAUI workload
- JDK 21 (Microsoft OpenJDK)
- Android SDK (platform 36, platform-tools)
- Docker Desktop
- An Android emulator or a physical Android device (only needed to run the app)

Installation details:
- toolchain, SDK and emulator: [android-setup.md](android-setup.md)
- phones: [physical-device-debugging.md](physical-device-debugging.md)

## 2. Clone and restore

```powershell
git clone https://github.com/colteo/LifeOS.git
cd LifeOS

dotnet restore src/dotnet/LifeOS.slnx
dotnet tool restore
```

`dotnet tool restore` installs the tools pinned in `dotnet-tools.json`.
Currently that is **`dotnet-ef` 10.0.4**, matching the EF Core version used by
Infrastructure. Always invoke it as `dotnet tool run dotnet-ef ...`.

## 3. Environment configuration

### Docker (`.env`)

```powershell
Copy-Item .env.example .env
```

- `.env` provides `POSTGRES_DB`, `POSTGRES_USER` and `POSTGRES_PASSWORD` to
  `docker-compose.yml`.
- `.env` is **ignored by git**. Its values are for **local Docker development
  only**. Choose your own local password, and never commit real credentials.

### API connection string (User Secrets)

The API reads `ConnectionStrings:PostgreSQL` from .NET User Secrets. Nothing
is stored in `appsettings*.json`.

```powershell
dotnet user-secrets set `
  "ConnectionStrings:PostgreSQL" `
  "Host=localhost;Port=5432;Database=<db>;Username=<user>;Password=<local-password>" `
  --project src/dotnet/LifeOS.Api/LifeOS.Api.csproj
```

`<db>`, `<user>` and `<local-password>` **must match** the values in your `.env`.
User Secrets are loaded only when `ASPNETCORE_ENVIRONMENT=Development`. The
API's default launch profile sets that.

## 4. Start PostgreSQL

```powershell
docker compose up -d postgres
docker compose ps          # lifeos-postgres should be "Up"
```

Optional connectivity check:

```powershell
docker exec -it lifeos-postgres psql -U <user> -d <db> -c "SELECT version();"
```

The container uses `restart: unless-stopped`: once started, it comes back
automatically whenever Docker Desktop starts, until you stop it explicitly.

## 5. Apply database migrations

```powershell
dotnet tool run dotnet-ef database update `
  --project src/dotnet/LifeOS.Infrastructure/LifeOS.Infrastructure.csproj `
  --startup-project src/dotnet/LifeOS.Api/LifeOS.Api.csproj
```

- Migrations live in `src/dotnet/LifeOS.Infrastructure/Persistence/Migrations`.
  The current one is `CreateAccounts`.
- The command is **safe to re-run**: EF Core applies only migrations that are
  still pending.

Diagnostic: check whether the EF model has changes that no migration covers yet.

```powershell
dotnet tool run dotnet-ef migrations has-pending-model-changes `
  --project src/dotnet/LifeOS.Infrastructure/LifeOS.Infrastructure.csproj `
  --startup-project src/dotnet/LifeOS.Api/LifeOS.Api.csproj
```

Expected: `No changes have been made to the model since the last migration.`

## 6. Start the API

```powershell
dotnet run `
  --project src/dotnet/LifeOS.Api/LifeOS.Api.csproj `
  --launch-profile http
```

- The `http` launch profile listens on **port 5050**, which is the port the
  Android app expects. It also sets `ASPNETCORE_ENVIRONMENT=Development`, so
  User Secrets are loaded.
- Wait for `Now listening on: http://localhost:5050`.
- **Keep this terminal open.** Use a second terminal for the next steps.

## 7. Verify the API

```powershell
Invoke-RestMethod http://localhost:5050/health/database
Invoke-RestMethod http://localhost:5050/api/accounts
```

- `/health/database` returns `database: connected`. This confirms
  API → EF Core → PostgreSQL connectivity. It is currently a **technical
  development endpoint**, not a feature API.
- `/api/accounts` returns the list of persisted accounts. It is empty on a
  fresh database.

---

## 8. Run on the Android emulator

Create the emulator first, as described in [android-setup.md](android-setup.md).
Then set the SDK paths for the session:

```powershell
# Example path. Use the location of *your* JDK 21 installation.
$env:JAVA_HOME    = "C:\Program Files\Microsoft\jdk-21.0.12.101-hotspot"
$env:ANDROID_HOME = "$env:LOCALAPPDATA\Android\Sdk"

& "$env:ANDROID_HOME\platform-tools\adb.exe" devices    # emulator-5554  device

dotnet run `
  --project src/dotnet/LifeOS.App/LifeOS.App.csproj `
  -f net10.0-android `
  -p:AdbTarget=-e `
  -p:AndroidSdkDirectory="$env:ANDROID_HOME" `
  -p:JavaSdkDirectory="$env:JAVA_HOME"
```

The emulator reaches the host API at **`http://10.0.2.2:5050/`**. The app
selects this address automatically (`Services/ApiSettings.cs`, Debug builds).

## 9. Run on a physical Android device

The only essential difference from the emulator is **port forwarding over USB**:

```powershell
$adb = "$env:ANDROID_HOME\platform-tools\adb.exe"

& $adb devices                          # phone listed as "device"
& $adb -d reverse tcp:5050 tcp:5050
& $adb -d reverse --list                # expect tcp:5050 tcp:5050

dotnet run `
  --project src/dotnet/LifeOS.App/LifeOS.App.csproj `
  -f net10.0-android `
  -p:AdbTarget=-d `
  -p:AndroidSdkDirectory="$env:ANDROID_HOME" `
  -p:JavaSdkDirectory="$env:JAVA_HOME"
```

The phone uses **`http://127.0.0.1:5050/`**, and `adb reverse` forwards it to
the PC's `localhost:5050`. The forwarding is lost when the phone disconnects,
so re-run `reverse` after reconnecting.

For ABI selection, standalone APKs, Fast Deployment and logcat, see
[physical-device-debugging.md](physical-device-debugging.md).

---

## 10. Smoke test

Use synthetic data only.

1. `docker compose ps` shows `lifeos-postgres` **Up**.
2. The API is listening on `http://localhost:5050`.
3. `Invoke-RestMethod http://localhost:5050/api/accounts` succeeds.
4. The Android app starts on the emulator or phone.
5. Menu → **Accounts** (Finance → Accounts) opens.
6. Existing accounts are displayed, or the empty state if there are none.
7. Create an account: Name `Test Wallet`, Type `Cash`, Currency `EUR`.
8. `Test Wallet` appears in the list **without restarting the app**.
9. `Invoke-RestMethod http://localhost:5050/api/accounts` includes `Test Wallet`, confirming it was persisted.

## 11. Build and tests

```powershell
dotnet build src/dotnet/LifeOS.slnx

dotnet test tests/dotnet/LifeOS.UnitTests/LifeOS.UnitTests.csproj
dotnet test tests/dotnet/LifeOS.ArchitectureTests/LifeOS.ArchitectureTests.csproj

dotnet build `
  src/dotnet/LifeOS.App/LifeOS.App.csproj `
  -f net10.0-android `
  -p:AndroidSdkDirectory="$env:ANDROID_HOME" `
  -p:JavaSdkDirectory="$env:JAVA_HOME"
```

- **Unit tests** cover the Domain, Application handlers and API endpoint methods.
- **Architecture tests** enforce the layer rules described in
  [dependency-rules.md](../architecture/dependency-rules.md).
- `LifeOS.IntegrationTests` currently holds only a **placeholder**. There are
  no HTTP or PostgreSQL integration tests yet.
- The solution build includes the MAUI app, so set `JAVA_HOME` and
  `ANDROID_HOME` first if the build cannot find the Android SDK.

## 12. Daily workflow

1. Start Docker Desktop.
2. `docker compose up -d postgres`
3. Start the API on port 5050 (§6) and keep its terminal open.
4. Start the emulator, or connect the phone.
5. Phone only: `adb -d reverse tcp:5050 tcp:5050`.
6. Run the MAUI app (§8 or §9).
7. Develop and test.
8. Before committing, run the relevant tests (§11), and the migration check (§5) if you changed the EF model.

## 13. Stop the local environment

```powershell
docker compose stop
```

| Command | Effect | Data |
|---|---|---|
| `docker compose stop` | Stops the container | **Kept** |
| `docker compose down` | Stops and removes the container and network | **Kept**: the named volume `lifeos_postgres_data` survives |
| `docker compose down -v` | Also deletes the volumes | ⚠️ **Deleted**: the whole development database |

`docker compose down -v` is a **destructive reset**. Use it only deliberately.
Afterwards, start PostgreSQL again (§4) and re-apply the migrations (§5).

To stop the API, press `Ctrl+C` in its terminal.

---

## Adding a migration (when you change the EF Core model)

This is not part of normal startup. Use it only after changing entities or
EF Core configurations in Infrastructure.

```powershell
dotnet tool run dotnet-ef migrations add <MigrationName> `
  --project src/dotnet/LifeOS.Infrastructure/LifeOS.Infrastructure.csproj `
  --startup-project src/dotnet/LifeOS.Api/LifeOS.Api.csproj `
  --output-dir Persistence/Migrations

dotnet tool run dotnet-ef database update `
  --project src/dotnet/LifeOS.Infrastructure/LifeOS.Infrastructure.csproj `
  --startup-project src/dotnet/LifeOS.Api/LifeOS.Api.csproj
```

**Review the generated migration code before applying or committing it.**
Check table and column names, types, nullability and anything that drops or
alters existing data.
