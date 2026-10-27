# Physical Android Device: Deploy, Test and Debug

Operational guide for running LifeOS on a **real Android device** connected by
USB to a **Windows** development machine. Every workflow below has been
verified in this repository.

For the base toolchain (SDK, JDK, workloads, emulator) see
[Android Development Setup](android-setup.md). For architecture context see
[docs/architecture/overview.md](../architecture/overview.md).

---

## Prerequisites

- The toolchain from [android-setup.md](android-setup.md): .NET 10 SDK with the
  `maui` workload, Microsoft OpenJDK 21 and the Android SDK (platform-tools / `adb`).
- An Android device with **Developer Options** and **USB debugging** enabled.
- A USB cable (a data cable, not charge-only) connected to the development PC.
- For end-to-end testing: PostgreSQL (Docker) and `LifeOS.Api` running locally.

Set the session variables once per PowerShell window. The commands below use
`$adb` for brevity.

```powershell
$env:JAVA_HOME    = "C:\Program Files\Microsoft\jdk-21.0.12.101-hotspot"
$env:ANDROID_HOME = "$env:LOCALAPPDATA\Android\Sdk"
$adb = "$env:ANDROID_HOME\platform-tools\adb.exe"
```

`-d` in the `adb` commands targets the single USB-connected device, and `-e`
targets the emulator.

---

## Quick start: LifeOS Assistant on the Pixel 9 (one command)

`tools/dev/start-lifeos-dev.ps1` brings up everything the LifeOS Assistant
(AI-007) needs and opens the Debug app on the phone. It has been validated on
the Pixel 9 both with every service already running and from a cold start.

From the repository root, in any PowerShell window:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\dev\start-lifeos-dev.ps1
```

Optional parameters: `-Repo <path>` (default
`C:\lifeos-worktrees\assistant-chat`) and `-DeviceId <adb serial>` (default:
the validated Pixel 9; see `& $adb devices` for another phone).

What it does, in order. Each step reuses what is already running, so running it
again is safe:

1. **PostgreSQL**: starts the `lifeos-postgres` container (`docker compose up -d postgres`)
   unless it is already running. Docker Desktop must be running.
2. **Python AI** on `127.0.0.1:8000`: if nothing listens there, it asks for the
   OpenAI Assistant key with a hidden prompt, then starts the service in its own
   window with the OpenAI provider (`gpt-6-luna`). The key exists only in that
   process's environment. It is never written to disk and is removed from the
   script's environment right after the launch.
3. **LifeOS.Api** on `localhost:5050` (`http` launch profile, own window) with
   `AssistantAi__Enabled=true`, pointed at the local Python service.
4. **Pixel 9**: checks that the phone is authorised, runs
   `adb reverse tcp:5050 tcp:5050` and opens `it.colazzo.lifeos.dev`. Then go
   to **More → Assistant**.

Prerequisites specific to the script: the Python virtualenv in
`src/python/lifeos-ai/.venv`, adb in `%LOCALAPPDATA%\Android\Sdk\platform-tools`,
and the Debug app `it.colazzo.lifeos.dev` already installed on the phone. The
script never builds, installs or reinstalls the app. To deploy app changes, use §3
first.

**Python ↔ API service key.** The two local services authenticate with a shared
key (`LIFEOS_AI_SERVICE_KEY` / `NutritionAi__ServiceKey`). The script generates
a random 64-character key the first time it runs and stores it for your Windows
user, outside the repository:

```text
%LOCALAPPDATA%\LifeOS\dev-ai-service-key.txt
```

Later runs reuse it, so a service that is already running and one started now
share the same key. Never commit it. To rotate it, delete the file and restart
both services. The first time you run the updated script, close any Python AI or
API window started by an older version: those used a different key. With a
mismatched key, the Assistant answers "unavailable".

If the API is already listening on port 5050, the script cannot verify that it
was started with the Assistant enabled. It prints a warning; close that API
window and run the script again.

### Testing the Assistant draft lifecycle on the phone

Use synthetic data only.

1. Open a conversation and write an incomplete expense, e.g.
   `Ieri ho speso 10 euro al supermercato`. The draft bar under the header shows
   **Expense · 10,00 …**, **Draft · details missing**, what is missing, and
   **Confirm** (disabled until complete) and **Cancel**.
2. Send a few more messages, including a correction (`No, erano 12 euro, conto
   <name of a test account>`). The bar stays pinned while the conversation scrolls. While the
   assistant is answering, its buttons are disabled but stay visible. The
   correction updates the same draft. The amount changes and no second draft
   appears.
3. Leave the conversation (back) and reopen it: messages and the open draft are
   reloaded from the API.
4. Tap **Confirm**. The bar and the card show **Recorded** and **View
   transaction**, which opens the transaction. Nothing is recorded before this tap.
   Writing "confirm" in the chat does not record anything.
5. Start another draft and tap **Cancel**. Both show **Cancelled** with no
   action left except **Close**. Nothing is recorded.

---

## 1. Verify the device

```powershell
& $adb devices
```

| State shown | Meaning |
|---|---|
| `<serial>    device` | Ready. |
| `<serial>    unauthorized` | Unlock the phone and accept the **"Allow USB debugging?"** prompt. |
| no line for the phone | Not detected: check the cable, the USB mode and that USB debugging is on. |

Reconnecting the phone, or restarting adb (`& $adb kill-server`, then
`& $adb start-server`), may show the USB debugging prompt again; accept it on
the device.

## 2. Determine the device architecture

```powershell
& $adb -d shell getprop ro.product.cpu.abi
```

The tested physical device reports `arm64-v8a`, which corresponds to the .NET
runtime identifier **`android-arm64`**.

Emulators on a PC are typically `x86_64` (`android-x64`). An APK containing
native code only for the emulator's ABI will not install on an arm64 phone:

```text
INSTALL_FAILED_NO_MATCHING_ABIS
```

When building an APK for manual installation, build for the phone's ABI (see §4).

---

## 3. Preferred: development deployment with `dotnet run`

For day-to-day development, deploy straight to the phone and let the .NET
Android tooling handle packaging, installation and launch:

```powershell
dotnet run `
  --project src/dotnet/LifeOS.App/LifeOS.App.csproj `
  -f net10.0-android `
  -p:AdbTarget=-d `
  -p:AndroidSdkDirectory="$env:ANDROID_HOME" `
  -p:JavaSdkDirectory="$env:JAVA_HOME"
```

`-p:AdbTarget=-d` selects the USB device. The emulator equivalent in
`android-setup.md` uses `-e`.

Use this whenever the phone is connected. The tooling chooses the right ABI
and uses **Fast Deployment**: it copies the app's assemblies to the device
separately from the APK. That is fast, but it also means the APK on its own is
incomplete (see §4).

---

## 4. Standalone Debug APK (manual installation)

Use this only when you need an installable file. For example, you might
install it without `dotnet run`, or keep a build on the phone. The APK must be
**self-contained** and built for the phone's ABI:

```powershell
dotnet publish `
  src/dotnet/LifeOS.App/LifeOS.App.csproj `
  -f net10.0-android `
  -c Debug `
  -r android-arm64 `
  -p:AndroidPackageFormats=apk `
  -p:EmbedAssembliesIntoApk=true `
  -p:AndroidSdkDirectory="$env:ANDROID_HOME" `
  -p:JavaSdkDirectory="$env:JAVA_HOME"
```

**Why `EmbedAssembliesIntoApk=true` matters.** Debug builds normally rely on
Fast Deployment, so the APK does not contain the .NET assemblies. Installed
manually, such an APK starts and then closes immediately. Logcat shows:

```text
No assemblies found ... Assuming this is part of Fast Deployment
```

A manually installed APK must carry its assemblies inside it. This property
makes it do so.

**Debug, not Release.** Plain HTTP to the development API is allowed only in
Debug builds (see §6). A Release APK cannot reach the local API.

Output folder:

```text
src/dotnet/LifeOS.App/bin/Debug/net10.0-android/android-arm64/publish/
    it.colazzo.lifeos.dev-Signed.apk   ← install this one
    it.colazzo.lifeos.dev.apk          (unsigned, do not install)
```

## 5. Install the APK manually (clean install)

```powershell
& $adb -d uninstall it.colazzo.lifeos.dev

& $adb -d install --no-incremental `
  "src/dotnet/LifeOS.App/bin/Debug/net10.0-android/android-arm64/publish/it.colazzo.lifeos.dev-Signed.apk"
```

- Install the **`-Signed.apk`**.
- **Uninstall first.** This removes a previous installation and any leftover
  Fast Deployment state or override files that `dotnet run` placed on the device.
- **`--no-incremental`** disables adb's incremental install, which is unreliable
  in this workflow. The whole APK is transferred before installation.

`it.colazzo.lifeos.dev` is the **development ApplicationId** used by every
non-Release configuration (from `LifeOS.App.csproj`). Release builds use
`it.colazzo.lifeos`, so the two apps can be installed side by side and
uninstalling the Debug app never touches the Release one.

---

## 6. Connect the physical device to the local API

The app picks its development API address automatically
(`Services/ApiSettings.cs`):

| Running on | API base URL |
|---|---|
| Android emulator | `http://10.0.2.2:5050/` |
| Physical Android device | `http://127.0.0.1:5050/` |

On a phone, `127.0.0.1` is the phone itself. `adb reverse` forwards that port
over USB to the PC:

```powershell
& $adb -d reverse tcp:5050 tcp:5050
& $adb -d reverse --list        # expect: ... tcp:5050 tcp:5050
```

```text
LifeOS app on the phone
    ↓  http://127.0.0.1:5050
adb reverse (over USB)
    ↓
PC localhost:5050
    ↓
LifeOS.Api
```

- **`adb reverse` is temporary.** It disappears when the phone disconnects or
  adb restarts, so re-run it after reconnecting.
- If the app shows **"Could not reach the LifeOS API"**, run `adb reverse --list`
  first.
- The API must be listening on **port 5050**. The `http` launch profile uses
  that port (see §7).
- Plain HTTP to `10.0.2.2`, `127.0.0.1` and `localhost` is allowed in **Debug
  builds only**, via the Android network security config.

---

## 7. Full end-to-end test on a physical device

Run from the repository root. Use synthetic data only.

```powershell
# 1. PostgreSQL
docker compose up -d postgres

# 2. API on localhost:5050 (separate terminal; keep it running)
dotnet run `
  --project src/dotnet/LifeOS.Api/LifeOS.Api.csproj `
  --launch-profile http

# 3. Phone connected and authorised
& $adb devices

# 4. Port forwarding
& $adb -d reverse tcp:5050 tcp:5050
& $adb -d reverse --list

# 5. Deploy and launch (see §3)
dotnet run --project src/dotnet/LifeOS.App/LifeOS.App.csproj -f net10.0-android `
  -p:AdbTarget=-d -p:AndroidSdkDirectory="$env:ANDROID_HOME" -p:JavaSdkDirectory="$env:JAVA_HOME"
```

On the phone:

6. Open the menu and tap **Accounts** (Finance → Accounts).
7. Existing accounts load. With no data you see the empty state instead.
8. Create an account, e.g. Name `Wallet`, Type `Cash`, Currency `EUR`.
9. It appears in the list immediately, without restarting the app.
10. Optionally, confirm persistence from the PC:

```powershell
Invoke-RestMethod http://localhost:5050/api/accounts
```

---

## 8. Crash diagnostics (logcat)

Clear the log, reproduce the problem, then dump the log and filter it:

```powershell
& $adb -d logcat -c

# Launch the app from the phone, or via monkey:
& $adb -d shell monkey `
  -p it.colazzo.lifeos.dev `
  -c android.intent.category.LAUNCHER `
  1

& $adb -d logcat -d |
  Select-String -Pattern "FATAL|AndroidRuntime|Unhandled|Exception|DOTNET|mono|it.colazzo.lifeos.dev" `
  -Context 5,15
```

`monkey` with a count of `1` simply launches the app's launcher activity. It
is a convenient way to start it from the terminal.

Look for the **first real application or runtime error**: a `FATAL EXCEPTION`,
an unhandled .NET exception, or a `DOTNET`/`mono` startup message such as
"No assemblies found". Logcat is noisy, so ignore unrelated Android system
warnings from other processes.

---

## 9. Troubleshooting

| Symptom | Likely cause | Next check / action |
|---|---|---|
| `adb devices` lists nothing | Cable, USB mode, or USB debugging off | Try a data cable or another port, re-enable USB debugging, restart adb |
| Device shows `unauthorized` | Debugging prompt not accepted | Unlock the phone and accept "Allow USB debugging?"; reconnect if no prompt appears |
| `INSTALL_FAILED_NO_MATCHING_ABIS` | APK built for another ABI (e.g. emulator x86_64) | `getprop ro.product.cpu.abi`; publish with `-r android-arm64` for `arm64-v8a` (§2, §4) |
| App installs but closes immediately | Missing assemblies, or a startup exception | Run the logcat workflow (§8) |
| Logcat: "No assemblies found … Fast Deployment" | Manually installed Debug APK without embedded assemblies | Re-publish with `-p:EmbedAssembliesIntoApk=true`, then uninstall and reinstall (§4, §5), or use `dotnet run` (§3) |
| API works on the PC, app says "Could not reach the LifeOS API" | No port forwarding, API not on 5050, or not a Debug build | `adb -d reverse --list`; check the API console says `Now listening on: http://localhost:5050`; use a Debug build |
| `adb reverse --list` is empty | Forwarding lost after reconnect or adb restart | `& $adb -d reverse tcp:5050 tcp:5050` |
