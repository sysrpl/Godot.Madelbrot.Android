#!/usr/bin/env bash

godot --headless --export-release "Windows Desktop" builds/windows/mandelbrot.exe
godot --headless --export-release "Linux" builds/linux/mandelbrot.x86_64
godot --headless --export-release "Android" builds/android/mandelbrot.apk

