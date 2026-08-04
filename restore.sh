#!/usr/bin/env bash
set -e

cd "$(dirname "${BASH_SOURCE[0]}")"

TEMPLATE_ZIP="$HOME/.local/share/godot/export_templates/4.7.1.stable.mono/android_source.zip"
BUILD_VERSION="4.7.1.stable.mono"

if [ -d android/build ]; then
	echo "android/build already exists - nothing to do (delete it first to force a rebuild)."
	exit 0
fi

if [ ! -f "$TEMPLATE_ZIP" ]; then
	echo "Export templates not found at $TEMPLATE_ZIP" >&2
	echo "Install Godot's 4.7.1 mono export templates first." >&2
	exit 1
fi

mkdir -p android/build
unzip -q "$TEMPLATE_ZIP" -d android/build
chmod +x android/build/gradlew
touch android/build/.gdignore
echo "$BUILD_VERSION" > android/.build_version

echo "Restored android/build from export templates."
