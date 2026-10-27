#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")/.."
version="$(/usr/bin/python3 - <<'PY'
import pathlib
import re
import xml.etree.ElementTree as ET
value = ET.parse(pathlib.Path('../Directory.Build.props')).findtext('.//Version', '').strip()
if not re.fullmatch(r'\d+\.\d+\.\d+(?:-[A-Za-z0-9.-]+)?', value):
    raise SystemExit('Некорректная версия в Directory.Build.props')
print(value)
PY
)"
swift build -c release --arch arm64 --arch x86_64
binary_dir="$(swift build -c release --arch arm64 --arch x86_64 --show-bin-path)"
app="build/Раскладушка.app"
mkdir -p "$app/Contents/MacOS"
cp "$binary_dir/Raskladushka" "$app/Contents/MacOS/Raskladushka"
cat > "$app/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
<key>CFBundleIdentifier</key><string>io.github.lomer275.raskladushka</string>
<key>CFBundleName</key><string>Раскладушка</string>
<key>CFBundleDisplayName</key><string>Раскладушка</string>
<key>CFBundleExecutable</key><string>Raskladushka</string>
<key>CFBundlePackageType</key><string>APPL</string>
<key>CFBundleShortVersionString</key><string>$version</string>
<key>CFBundleVersion</key><string>$version</string>
<key>LSUIElement</key><true/>
<key>LSMinimumSystemVersion</key><string>13.0</string>
<key>NSAppTransportSecurity</key><dict><key>NSAllowsArbitraryLoads</key><true/></dict>
<key>NSMicrophoneUsageDescription</key><string>Раскладушка записывает голос, пока вы держите правый Option, и отправляет запись на сервер распознавания команды.</string>
</dict></plist>
PLIST
codesign --force --sign - --deep "$app"
codesign --verify --strict "$app"
ditto -c -k --keepParent "$app" "build/Raskladushka-mac-$version.zip"
echo "Готово: mac/build/Raskladushka-mac-$version.zip"
