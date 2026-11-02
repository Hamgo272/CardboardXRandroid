# Glasses XR — plan (for Claude)

This repository is a fork of [brenpoly/CardboardXR](https://github.com/brenpoly/CardboardXR)
(Apache-2.0), the project behind the video *"I Built a $5 Apple Vision Pro"*
(https://youtu.be/XtNmiTohUcs). Our branch is `iphone11`; the original is the `upstream` remote.

## The goal behind the request

The user wants a personal "Apple Vision Pro" from a phone VR headset and an **iPhone 11**:

1. **Now:** an exact, working replica of the CardboardXR app on the iPhone 11, as smooth as in the
   video — 6DoF room tracking, passthrough, surfaces that objects stand on and fall off, smooth
   hand tracking with pinch/grab, the demos.
2. **Next:** what the user actually wants to *use* it for — **watching movies on a virtual screen
   of any size, anchored in the room** — plus room-aware objects and a Vision-Pro feel.

Constraints: the user is a Hebrew-speaking non-developer on **Windows with no Mac**, with **no
budget**. Talk to them in Hebrew, in short concrete steps. They enter their own credentials
(GitHub, Unity, Apple ID); never ask them to paste a password to you.

## What upstream already does (read its README before changing anything)

- Unity **6000.6.0f1**, URP 17.6, AR Foundation + ARKit/ARCore **6.6.2**, Inference Engine 2.6.1,
  XR Hands 1.9, XR Interaction Toolkit 3.6.
- **Head & room:** ARKit VIO 6DoF fused with a gyro tracker (`HeadPoseFusion`), plane detection,
  anchors, `PlaneCollider`s so physics objects land on real tables and floors.
- **Hands:** MediaPipe BlazePalm + 21-landmark model (`Assets/_Project/Hands/Models/*.onnx`) on
  Inference Engine (**CPU backend**), One Euro filtering, pinch/grab/ray, gaze-and-pinch through
  `PinchRouter`; a lighter `PinchSensor` in 3DoF.
- **Rendering:** its own stereo rig with Cardboard SDK lens distortion (one XR loader only, so no
  Cardboard plugin); viewer profiles from QR codes (ZXing).
- **Heat:** thermal state logging, frame-rate and inference step-down.
- **Demos:** Lobby (room scan), Cube Sandbox, Air Paint, Orb Rush, Portal, 360 Video.
- **Structure:** `Core.unity` is **generated** by `Assets/Editor/ProjectBootstrap/StereoBuilder.cs`
  on every build — edit the builder, never the scene. Demos are additive scenes in
  `Assets/_Project/Demos/<Name>/` listed in `DemoCatalog.asset`; demos reach Core through
  `CoreServices`. Logs: `session.log` in the app's Documents, lines tagged `[Pose] [Hands] [Room]
  [Frames] [Thermal] …`, a status line every 5 s.
- Known limits (`KNOWN_ISSUES.md`): hand must be inside the camera's 68°×42° view; passthrough is
  flat (one camera); hand depth is estimated from palm size; surface scan stops after the floor
  except in the Sandbox.

## iPhone 15 Pro (upstream's test phone) → iPhone 11

| | iPhone 15 Pro | iPhone 11 | Consequence |
|---|---|---|---|
| Chip | A17 Pro | A13 | Hand models (7–31 ms/frame on A17) will be slower: cap `handHz`, consider GPU or Apple Vision |
| Display | 120 Hz OLED 2556×1179 | **60 Hz LCD 1792×828** | 16.7 ms frame budget; lower pixel density in the viewer |
| LiDAR | yes | **no** | No environment depth: palm size comes from the model (8–12 % near), planes from vision only (slower, needs texture and movement) |
| RAM | 8 GB | 4 GB | Watch memory with video textures |
| iOS | 26 | 26 (supported) | Same APIs; people occlusion and plane classification need A12+, so both are available |

Upstream code already degrades without depth (`HandTracker` falls back when
`TryAcquireEnvironmentDepthCpuImage` fails), so the first build should run unchanged.

## Toolchain (no Mac, $0)

- **Windows:** Unity 6000.6.0f1 + iOS Build Support in
  `C:\Program Files\Unity\Hub\Editor\6000.6.0f1`. Use it for C# compile checks and EditMode tests
  in batch mode before every push (see "Commands").
- **CI:** `.github/workflows/ios-build.yml` — GameCI exports the Xcode project on Linux
  (`StereoBuilder.BuildIos`), a macOS runner builds it **unsigned**, the `.ipa` goes to the
  `latest-iphone` release. Needs secrets `UNITY_LICENSE`, `UNITY_EMAIL`, `UNITY_PASSWORD` (the user
  sets them). Standard runners are free for public repositories, so keep the fork public.
- **Fallback if CI licensing fails:** export locally (command below), tar `Builds/iOS`, upload it
  to a release, and add an Xcode-only workflow that downloads it.
- **Install:** Sideloadly on Windows with the user's free Apple ID (needs the desktop iTunes and
  iCloud from apple.com, not the Microsoft Store versions). Developer Mode on the phone. Free
  signing expires after **7 days** and allows 3 apps; re-sign weekly.
- **Logs from the phone:** Files app → On My iPhone → CardboardXR → `session.log` → share.
- iOS bundle id: `com.glassesxr.cardboardxr` (the original id belongs to the author's team).

### Commands (Windows, from the repo root, Unity closed)

```
"C:\Program Files\Unity\Hub\Editor\6000.6.0f1\Editor\Unity.exe" -batchmode -quit -projectPath . -buildTarget iOS -logFile Logs\compile.log
"C:\Program Files\Unity\Hub\Editor\6000.6.0f1\Editor\Unity.exe" -batchmode -projectPath . -runTests -testPlatform EditMode -testResults Logs\tests.xml -logFile Logs\tests.log
"C:\Program Files\Unity\Hub\Editor\6000.6.0f1\Editor\Unity.exe" -batchmode -quit -projectPath . -buildTarget iOS -executeMethod ProjectBootstrap.StereoBuilder.BuildIos -logFile Logs\ios.log
```

Look for `error CS` in the log; the export prints `[Build] Build Succeeded`.

## Phases

Each phase ends with a build on the phone and the user's `session.log`. Don't start the next
phase on guesses: measure.

### Phase 0 — Pipeline (in progress)
- [x] Fork cloned, branch `iphone11`, bundle id changed, CI workflow written.
- [ ] Unity installed locally; project imports; EditMode tests pass on Windows.
- [ ] User: GitHub account, fork pushed (public), the three secrets set.
- [ ] CI run green; `.ipa` on the release.
- [ ] User: iTunes + iCloud (desktop) + Sideloadly; app installed; Developer Mode; app opens.
- **Done when:** the lobby shows on the iPhone 11.

### Phase 1 — Faithful replica on the iPhone 11
Walk every upstream feature on the device: viewer QR (or the default profile if the user's viewer
has none), stereo + distortion, room scan, 6DoF, passthrough, gaze + pinch, hand tracking, all
six demos. Collect `[Frames] [Hands] [Thermal] [Room]` figures. Fix only iPhone-11 breakage here.
- **Done when:** every demo works; ≥55 fps with no hand in view; hands usable for pinch and grab.

### Phase 2 — iPhone 11 tuning ("as smooth as the video")
- Enable `AROcclusionManager` only when environment depth is supported (no LiDAR = wasted work).
- Adaptive `handHz` (start ~30, step down with thermal state); try `BackendType.GPUCompute` for
  the landmarker and compare.
- **Palm calibration without LiDAR** (our idea): when a plane is under the hand (palm flat on a
  table), the plane's raycast depth at the palm pixel is a true measurement — feed it to the
  existing palm-size learning in place of LiDAR depth.
- Room scan guidance for vision-only plane finding; keep scanning tables (not just the floor) when
  CPU allows; use plane classification (floor/table/wall) for placement.
- Thermal governor thresholds for A13.
- **Done when:** 60 fps with a hand in view for ≥5 minutes before a thermal step-down; hand depth
  within ±5 % of a ruler at 30–60 cm after calibration; first surface ≤20 s in a normal room.

### Phase 3 — Cinema (the user's main goal)
A new demo, `Assets/_Project/Demos/Cinema/`, added to `DemoCatalog`:
- A flat 16:9 screen in 6DoF passthrough, anchored in the room; optional dark "theater" (VR) mode.
- Any size: grab two corners / two-hand pinch to scale (0.5–10 m), grab to move, snap to walls
  (vertical planes), optional curve. Passthrough dims while playing.
- Sources: the app's Videos folder (Files app), a document-picker import (small native iOS
  plugin), network streams/HLS (Jellyfin/Plex), Google Drive links (reuse `DriveDownloader`),
  side-by-side / top-bottom 3D movies (reuse `StereoLayout`).
- Controls by gaze + pinch (reuse the Video demo's controls); remember screen pose and size.
- Out of reach: DRM services (Netflix, Disney+, Apple TV+) can't be drawn into a texture; YouTube
  needs a web view rendered to a texture (spike R4).
- **Done when:** a 2-hour 1080p movie plays smoothly on a stable screen of any size.

### Phase 3b — Computer Screen (built, untested on device)
The user asked to open "any app on my phone" on a floating screen. iOS forbids drawing other
apps inside ours, and switching apps would background ours (ARKit and rendering stop). The
closest real feature: **the computer's screen** (any app on it) on a room-anchored screen.
- PC: `Tools/screen-server/` — `start.bat` runs `server.js` (Node, stdlib only) and opens
  `sender.html` in Chrome: `getDisplayMedia` → JPEG frames → `POST /push`. Phones long-poll
  `GET /frame`, send input to `POST /input`; `mouse.ps1` moves/clicks the real mouse (DPI-aware).
  Pairing: a phone must be approved on the page (`/approve`, localhost only) before it gets frames
  or sends input. Audio stays on the PC.
- App: demo `desktop` (`Assets/_Project/Demos/Desktop/`, scene made by `DemoScenes.EnsureDesktop`).
  `DesktopLink` scans the phone's /24 on port 8765 (no multicast entitlement needed), saves the
  host, long-polls frames with `UnityWebRequestTexture`. `DesktopDemo` (IPinchHandler): gaze +
  pinch = click, pinch + hand up/down = wheel, held pinch = drag; bar = move (gaze + hand push),
  corner = resize (0.3–12 m). Fallback: pinch while searching opens the iOS keyboard for the address.
- iOS: `NSLocalNetworkUsageDescription`, ATS `NSAllowsLocalNetworking` (IosProMotion.cs),
  `insecureHttpOption: 2` (Unity allows http://).
- Next: H.264/WebRTC instead of JPEG if fps is too low; keyboard input (dictation); audio.

### Phase 3c — Browser (built, untested on device)
Netflix/YouTube/Google "apps" on a room-anchored screen. On the iPhone alone the only legal route
is a web browser inside our app: `Plugins/iOS/GlassesWebView.mm` keeps a WKWebView behind Unity's
view and snapshots it ~20×/s into a texture (`WebView.cs`); input is JavaScript (tap = pointer/mouse
events + click(), scroll = nearest scrollable, Type = TouchScreenKeyboard → focused field + submit).
`WebDemo` buttons: Back, Google, YouTube, Reload, Type. Both screens share `FloatingScreen.cs`
(move/resize/anchor/gaze-pinch). WebKit is linked by `IosProMotion.cs`.
- Risk to verify first on device: WKWebView snapshots may leave `<video>` black (YouTube). If so:
  try a desktop user agent / `AVPlayer` for direct media URLs, or route video through Computer Screen.
- Netflix: impossible on iPhone (no web playback on iOS, DRM). Via Computer Screen only, and Chrome
  needs hardware acceleration off for DRM video to be capturable.

### Phase 4 — Vision Pro feel (spikes, measured before adopted)
- **R1 Apple Vision hands:** `VNDetectHumanHandPoseRequest` runs on the Neural Engine; compare
  with MediaPipe on fps, latency, heat. Possible hybrid: Vision 2D joints every frame, MediaPipe
  3D shape at a low rate.
- **R2 People occlusion:** ARKit person segmentation (A12+) so the real hand hides virtual
  objects in passthrough; needs the stencil/depth in the stereo rig.
- **R3 Persistence:** ARKit world map so screens and objects stay put between sessions.
- **R4 Panels:** multiple windows (photos, notes, a browser drawn from WKWebView snapshots).

## Working rules

- Keep upstream's structure and style so upstream fixes can be merged; mark our changes in
  commit messages. Apache-2.0: keep `LICENSE` and `THIRD_PARTY_NOTICES.md`, note modified files.
- Compile-check and run EditMode tests locally before every push; CI takes ~40 minutes.
- New behaviour logs in the existing `[Tag]` style so a `session.log` answers "did it work?".
- Commit and push only when the user agrees.
