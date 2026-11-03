#!/usr/bin/env bash
# Run the Android emulator checks in one process. The emulator-runner action
# executes individual script lines in separate shells, so state such as exit
# codes must not be held in variables in its inline script.
set -euo pipefail

: "${SMARTPRINT_PUBLIC_BASE_URL:?SMARTPRINT_PUBLIC_BASE_URL must be configured}"
: "${RUNNER_TEMP:?RUNNER_TEMP is required}"

echo "Waking and unlocking the Android emulator before Espresso tests..."
adb wait-for-device
adb shell input keyevent KEYCODE_WAKEUP
adb shell wm dismiss-keyguard || true
adb shell locksettings set-disabled true || true
adb shell settings put global stay_on_while_plugged_in 7
adb shell settings put system screen_off_timeout 2147483647
adb shell input keyevent KEYCODE_HOME
sleep 2

echo "Checking emulator power state..."
adb shell dumpsys power | grep -E 'mWakefulness=Awake|Display Power: state=ON'
echo "Current Android window focus:"
adb shell dumpsys window | grep -E 'mCurrentFocus|mFocusedApp' || true

# Keep the pre-existing UI suite independent from the lifecycle checks:
# even if Espresso fails, still execute the package-manager scenarios.
ui_test_status=0
if gradle --no-daemon -p apps/android -PsmartPrintServiceUrl="$SMARTPRINT_PUBLIC_BASE_URL" :app:connectedDebugAndroidTest; then
  ui_test_status=0
else
  ui_test_status=$?
fi

echo "Building isolated APK N for package-manager lifecycle checks..."
gradle --no-daemon -p apps/android -PsmartPrintServiceUrl="$SMARTPRINT_PUBLIC_BASE_URL" -PsmartPrintVersionCode=9100 -PsmartPrintVersionName="lifecycle-n" :app:assembleDebug
cp apps/android/app/build/outputs/apk/debug/app-debug.apk "$RUNNER_TEMP/smartprint-lifecycle-n.apk"

echo "Building isolated APK N+1 with the same runner debug signing identity..."
gradle --no-daemon -p apps/android -PsmartPrintServiceUrl="$SMARTPRINT_PUBLIC_BASE_URL" -PsmartPrintVersionCode=9101 -PsmartPrintVersionName="lifecycle-n1" :app:assembleDebug
cp apps/android/app/build/outputs/apk/debug/app-debug.apk "$RUNNER_TEMP/smartprint-lifecycle-n1.apk"

if bash scripts/qa/android-install-lifecycle.sh "$RUNNER_TEMP/smartprint-lifecycle-n.apk" "$RUNNER_TEMP/smartprint-lifecycle-n1.apk"; then
  lifecycle_status=0
else
  lifecycle_status=$?
fi

if [ "$ui_test_status" -ne 0 ]; then
  echo "::error::The Android emulator UI suite failed (exit $ui_test_status)."
fi
if [ "$lifecycle_status" -ne 0 ]; then
  echo "::error::Android package installation lifecycle checks failed (exit $lifecycle_status)."
fi
if [ "$ui_test_status" -ne 0 ] || [ "$lifecycle_status" -ne 0 ]; then
  exit 1
fi

echo "Android emulator UI and installation lifecycle checks passed."
