# Android Development Setup

This document describes how to configure the development environment
required to build and run the LifeOS Android application.

## Requirements

LifeOS Android development currently requires:

- .NET 10 SDK
- .NET MAUI workload
- Microsoft OpenJDK 21
- Android Studio
- Android SDK 36
- Android Emulator

## 1. Verify .NET

Check the installed .NET SDK:

```powershell
dotnet --version
```

Check installed workloads:

```powershell
dotnet workload list
```

The `maui` workload must be installed.

If it is missing:

```powershell
dotnet workload install maui
```

## 2. Java Development Kit

LifeOS uses Microsoft OpenJDK 21 for Android development.

Verify:

```powershell
java -version
```

The major version should be `21`.

Set `JAVA_HOME` for the current PowerShell session:

```powershell
$env:JAVA_HOME = "C:\Program Files\Microsoft\jdk-21.0.12.101-hotspot"
$env:Path = "$env:JAVA_HOME\bin;" + $env:Path
```

Verify:

```powershell
java -version
javac -version
```

## 3. Android SDK

Android Studio is used to install and manage the Android SDK and emulators.

The default Android SDK location on Windows is:

```text
C:\Users\<USERNAME>\AppData\Local\Android\Sdk
```

LifeOS currently requires Android SDK Platform 36.

The following Android SDK tools should be installed:

- Android SDK Platform 36
- Android SDK Build-Tools
- Android SDK Platform-Tools
- Android SDK Command-line Tools
- Android Emulator

Set `ANDROID_HOME` for the current PowerShell session:

```powershell
$env:ANDROID_HOME = "$env:LOCALAPPDATA\Android\Sdk"
```

Verify Android SDK 36:

```powershell
Test-Path "$env:ANDROID_HOME\platforms\android-36"
```

Expected result:

```text
True
```

## 4. Android Emulator

The current development emulator is:

- Device: Pixel 9
- Android: Android 16
- API: 36
- Architecture: x86_64

List available Android Virtual Devices:

```powershell
& "$env:ANDROID_HOME\emulator\emulator.exe" -list-avds
```

Expected example:

```text
Pixel_9
```

Start the emulator from Android Studio:

```text
Tools → Device Manager
```

Or from PowerShell:

```powershell
& "$env:ANDROID_HOME\emulator\emulator.exe" -avd Pixel_9
```

Verify that the emulator is detected:

```powershell
& "$env:ANDROID_HOME\platform-tools\adb.exe" devices
```

Expected output:

```text
List of devices attached
emulator-5554    device
```

## 5. Build LifeOS

From the repository root:

```powershell
dotnet build src/dotnet/LifeOS.App/LifeOS.App.csproj `
  -f net10.0-android `
  -p:AndroidSdkDirectory="$env:ANDROID_HOME" `
  -p:JavaSdkDirectory="$env:JAVA_HOME"
```

The build should complete successfully.

## 6. Run LifeOS

Make sure the Android emulator is running.

Then execute:

```powershell
dotnet run `
  --project src/dotnet/LifeOS.App/LifeOS.App.csproj `
  -f net10.0-android `
  -p:AdbTarget=-e `
  -p:AndroidSdkDirectory="$env:ANDROID_HOME" `
  -p:JavaSdkDirectory="$env:JAVA_HOME"
```

LifeOS should be installed and automatically launched on the emulator.

## Troubleshooting

### Android SDK not found

Verify:

```powershell
$env:ANDROID_HOME
```

and:

```powershell
Test-Path "$env:ANDROID_HOME\platforms\android-36"
```

### Java SDK not found

Verify:

```powershell
$env:JAVA_HOME
java -version
```

LifeOS currently uses Microsoft OpenJDK 21.

### InstallAndroidDependencies fails

On managed Windows environments, antivirus or endpoint security software
may lock files while the .NET Android installer is extracting SDK components.

If this happens, install the Android SDK through Android Studio instead of
using the `InstallAndroidDependencies` MSBuild target.