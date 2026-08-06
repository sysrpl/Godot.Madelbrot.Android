# Godot Mandelbrot Viewer With Android Support

![Welcome to Mandelbrot](resources/images/splash.png)

A real-time Mandelbrot set explorer built with Godot 4.7 and .NET 8 (C#), rendered
entirely in a fragment shader with pan, zoom, rotation, and a scripted tour of
documented locations in the set. Runs on Windows, Linux, and Android.

## Features

- Full-screen Mandelbrot rendering (`resources/shaders/mandelbrot.gdshader`) with
  smooth, continuous escape-time coloring (no banding between iteration levels).
- Interactive pan, zoom, and rotation on both desktop and Android — rotation is
  compensated for so panning always tracks what looks right on screen, regardless
  of current rotation.
- **Tour mode**: cycles through seven real, documented locations in the Mandelbrot
  set (Elephant Valley, Seahorse Valley, Triple Spiral Valley, Scepter Valley, the
  Myrberg-Feigenbaum point, and more), each visited with a scripted
  hold/zoom-out/pan/zoom-in/hold sequence and synchronized rotation.
- Framerate throttling (5 FPS idle, 60 FPS while interacting or touring) to keep
  power draw down on battery-powered devices.
- Android-specific tuning: pinch/pan zoom jumps directly to its target instead of
  animating, to keep frame time low on mobile GPUs.

![Screen Capture](docs/snapshot.png)

## Requirements

- [Godot 4.7](https://godotengine.org/) with .NET/C# support (Mono build)
- [.NET 8 SDK](https://dotnet.microsoft.com/)
- For Android builds: Godot's Android export templates (matching version) and the
  Android SDK/`adb` on your `PATH`

## Controls

**Desktop (mouse):**

| Action | Control |
|---|---|
| Pan | Left-click drag |
| Zoom | Mouse wheel |
| Rotate | Hold Ctrl + drag (horizontal movement only) |
| Reset rotation | Double-click |

**Android (touch):**

| Action | Control |
|---|---|
| Pan | One-finger drag |
| Zoom | Two-finger pinch |
| Rotate | Three-or-more-finger drag |
| Reset rotation | Double-tap |

Zooming out is capped at the full default view (zoom level 1) in all cases.

The **Tour** button (top-left) starts/stops the guided tour; while touring, manual
pan/zoom/rotate input is disabled. **Quit** sits directly below it.

## Running

Open the project in the Godot editor and run it directly for Windows/Linux.

For Android:

```sh
./restore.sh       # unpacks the Android export template/build tree (once)
./deploy.sh         # builds and installs to a USB-connected device
./deploy-wifi.sh    # builds and installs to a device over Wi-Fi (adb)
```

## Project layout

- `Main.tscn` / `Main.cs` — the scene root and all pan/zoom/rotation/tour logic
- `resources/shaders/mandelbrot.gdshader` — the Mandelbrot fragment shader
- `project.godot` — Godot project settings
