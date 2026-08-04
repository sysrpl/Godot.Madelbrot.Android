#!/usr/bin/env bash
set -e

cd "$(dirname "${BASH_SOURCE[0]}")"

# `dotnet new` doesn't preserve the executable bit; gradlew must be
# executable for Godot's internal gradle build step to run it.
chmod +x android/build/gradlew 2>/dev/null || true

USB_SERIAL=$(adb devices | awk '$2=="device" && $1 !~ /:/ {print $1}' | head -n1)
if [ -z "$USB_SERIAL" ]; then
	echo "No USB-connected device found (phone may only be reachable over Wi-Fi - try wifi-deploy.sh)." >&2
	exit 1
fi

mkdir -p builds
godot --headless --path . --export-debug "Android" builds/mixamo.apk
adb -s "$USB_SERIAL" install -r builds/mixamo.apk
adb -s "$USB_SERIAL" shell monkey -p org.godotdemo.scaffoldproject -c android.intent.category.LAUNCHER 1
