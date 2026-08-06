#!/usr/bin/env bash
set -e

cd "$(dirname "${BASH_SOURCE[0]}")"

# `dotnet new` doesn't preserve the executable bit; gradlew must be
# executable for Godot's internal gradle build step to run it.
chmod +x android/build/gradlew 2>/dev/null || true

PORT=5555
IP_CACHE=".wifi_device_ip"
PACKAGE="org.godotdemo.scaffoldproject"

# Already connected wirelessly?
WIFI_SERIAL=$(adb devices | awk -v p=":$PORT" '$0 ~ p && $2=="device" {print $1}' | head -n1)

# Not connected, but we remember an IP from last time: try reconnecting to it.
if [ -z "$WIFI_SERIAL" ] && [ -f "$IP_CACHE" ]; then
	CACHED_IP=$(cat "$IP_CACHE")
	echo "Reconnecting to $CACHED_IP:$PORT ..."
	adb connect "$CACHED_IP:$PORT" >/dev/null 2>&1 || true
	WIFI_SERIAL=$(adb devices | awk -v p=":$PORT" '$0 ~ p && $2=="device" {print $1}' | head -n1)
fi

# Still nothing: bootstrap wireless mode from a USB-connected phone.
if [ -z "$WIFI_SERIAL" ]; then
	USB_SERIAL=$(adb devices | awk '$2=="device" && $1 !~ /:/ {print $1}' | head -n1)
	if [ -z "$USB_SERIAL" ]; then
		echo "No device found over Wi-Fi or USB." >&2
		echo "Plug your phone in via USB once (with USB debugging on) to bootstrap the wireless connection, then rerun this script." >&2
		exit 1
	fi

	echo "Bootstrapping Wi-Fi adb from USB device $USB_SERIAL ..."
	DEVICE_IP=$(adb -s "$USB_SERIAL" shell ip route get 1.1.1.1 2>/dev/null | grep -oE 'src [0-9.]+' | awk '{print $2}')
	if [ -z "$DEVICE_IP" ]; then
		echo "Could not determine the phone's Wi-Fi IP address. Is it connected to Wi-Fi?" >&2
		exit 1
	fi

	adb -s "$USB_SERIAL" tcpip "$PORT"
	sleep 2
	adb connect "$DEVICE_IP:$PORT"
	sleep 1

	echo "$DEVICE_IP" > "$IP_CACHE"
	WIFI_SERIAL="$DEVICE_IP:$PORT"
fi

echo "Deploying to $WIFI_SERIAL over Wi-Fi ..."

mkdir -p builds
godot --headless --path . --export-debug "Android" builds/mandelbrot.apk
adb -s "$WIFI_SERIAL" install -r builds/mandelbrot.apk
adb -s "$WIFI_SERIAL" shell monkey -p "$PACKAGE" -c android.intent.category.LAUNCHER 1
