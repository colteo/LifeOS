# BRAND-001 — LifeOS icon and launch experience

Status: implemented; physical-phone acceptance pending.

## Source-photo handling

Read personal source material only from the user-supplied local location. Never upload it,
copy the original into this repository, or commit the original or intermediate crops.
Only the final application icon derivatives may be committed. Strip photograph metadata
from final assets. Processing must be deterministic and local: crop/resize and optional
light exposure/contrast correction only; no face alteration or generated replacement.

The source was read locally with Pillow, applying EXIF orientation before cropping. It
is 899 x 1599 pixels after orientation. Crop coordinates are (180, 380, 899, 1320),
with right/bottom exclusive: a 719 x 940 portrait around the head, wink, chin and a
small amount of shoulders. Most room background is removed. No exposure correction,
background replacement, stylization or facial alteration was applied.

Resize the crop with Lanczos to 783 x 1024; place it at (120, 0) on a 1024 x 1024
RGB #F6F7FB canvas. Save a metadata-free PNG as Resources/AppIcon/appiconfg.png.
The original remains outside the repository. No processing script or preview is tracked.

## Icon implementation and adaptive safe area

One unconditional MauiIcon entry serves Debug and Release. appicon.svg is a plain
#F6F7FB background; appiconfg.png is the derived foreground, at ForegroundScale=0.70.
The former .NET foreground SVG is deleted. MAUI Single Project/Resizetizer produces
the adaptive foreground/background layers and both legacy square and round icons.
There is no base64 photograph in SVG, runtime image-processing dependency, package ID
change, signing change or configuration-specific photograph.

Android adaptive layers are 108 dp, with a nominal 72 dp masked viewport and a central
66 dp safe circle. The portrait crop centers the facial features rather than the full
source frame. Scale 0.70 yields a 75.6 dp foreground; neutral margins and unimportant
outer photo regions absorb mask clipping. Both eyes, nose, mouth and chin remain inside
the central safe circle, with enough hair to preserve recognition. Some outer curls
already reach the original source boundary; no missing hair was synthesized.

Local previews of the generated foreground/background under circle, squircle and rounded
square masks, including launcher-size views, and generated legacy icons were inspected.
The face/wink remain visible. Device/OEM-specific appearance still needs phone acceptance.
Implementation reference: [MAUI 10.0.20 adaptive icon generator](https://github.com/dotnet/maui/blob/10.0.20/src/SingleProject/Resizetizer/src/AndroidAdaptiveIconGenerator.cs).

## Launch implementation

Use a plain #F6F7FB native splash, with a same-color rectangular SVG. It contains no
photo, text, template artwork, or animation. This explicitly supplies a splash image
rather than allowing Android to fall back to the photographic launcher icon.

Android 12+ treats the native splash asset as a centered, masked icon. A wide tagline
does not belong there. See [MAUI splash screens](https://learn.microsoft.com/en-us/dotnet/maui/user-interface/images/splashscreen?view=net-maui-10.0)
and [Android splash screen constraints](https://developer.android.com/develop/ui/views/launch/splash-screen).

The existing index.html boot placeholder and AuthGate Restoring branch share the same
centered launch surface: bold dark (#111827) LifeOS with smaller muted text:

> Your personal life, in one place.

Use the existing application typography and light neutral background. The WebView
placeholder is replaced when Blazor renders. The restoring surface disappears when
the existing auth state changes. There is no timer, additional state machine, delayed
navigation, or overlay over errors. Signed-out, Google sign-in, unreachable, onboarding,
and authenticated branches and session service logic remain unchanged. Retry can show
the restoring surface through the existing auth state; ordinary navigation cannot.

## Validation

- Baseline: a024c23486321c414595a6fe8267eb832c528a57, matching origin/main when work started.
- LaunchBrandingTests check exact copy, no startup timers, existing AuthGate branches,
  plain neutral native splash, derived PNG dimensions/reference, neutral launcher background,
  deleted template foreground, and absence of source-photo references in project config.
- Full UnitTests: 1,465 passed, zero failed/skipped (including three branding checks).
- Android Debug build: succeeded, zero warnings/errors, using .NET 10.0.401,
  Android SDK at C:\Android\Sdk and Microsoft OpenJDK 21.
- Generated drawable-v31/maui_splash_image.xml references the neutral splash;
  generated maui_colors.xml contains #fff6f7fb. Adaptive XML references the new
  foreground/background in mipmap-anydpi-v26; square, round and layer PNGs are generated
  at mdpi, hdpi, xhdpi, xxhdpi and xxxhdpi. Manifest uses appicon/appicon_round and
  the Debug package it.colazzo.lifeos.dev.
- git diff --check passed.
- Application IDs, versions, signing, production API guards, and backend are unchanged.
- No secret-directory path or original source photograph is tracked. Only the final
  PNG derivative and required neutral SVG assets are included; generated resources remain ignored.
- Both configurations evaluate to the same icon/splash entries; Release keeps
  it.colazzo.lifeos and Debug keeps it.colazzo.lifeos.dev. No Release APK was built.
- Phone acceptance has not been performed.

## Phone acceptance checklist

1. Install Debug build.
2. Launcher shows the photo icon.
3. Face remains recognizable at launcher size.
4. Circular/launcher mask does not awkwardly cut important facial features.
5. Recent-apps/app-switcher icon looks correct.
6. No purple/.NET template branding appears.
7. Cold-start LifeOS.
8. Launch branding is clean and centered.
9. LifeOS displays correctly.
10. Tagline reads exactly: "Your personal life, in one place."
11. No ugly crop/shrink of tagline on Android 12+.
12. No perceptible artificial startup delay.
13. Existing signed-in session restores normally.
14. Signed-out login and Google sign-in still work.
15. Startup/API errors become visible normally.
16. Home opens correctly.
17. Settings works.
18. Finance/Gym/Nutrition are unaffected.
19. Rotate/restart if supported; no branding artifact remains over UI.

## Delivery

Commit/push on improve/app-branding only; do not merge.

## Risks and deferred work

Physical phone/OEM masks, recent-apps icon and cold-start transitions require the checklist
above. MAUI 10.0.20 automatically reuses the foreground for Android's monochrome themed
icon layer. A photograph does not retain its colors/details in launcher themed-icon mode;
use normal full-color icons for photo acceptance. No custom themed-icon pipeline was added.
