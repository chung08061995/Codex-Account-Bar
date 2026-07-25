#!/bin/zsh
set -euo pipefail

script_dir=${0:A:h}
build_dir="$script_dir/.build/release"
app_dir="$script_dir/dist/Codex Account Bar.app"

cd "$script_dir"
swift test
swift build -c release

rm -rf "$app_dir"
mkdir -p "$app_dir/Contents/MacOS" "$app_dir/Contents/Resources"
cp "$build_dir/CodexAccountBar" "$app_dir/Contents/MacOS/CodexAccountBar"
cp "$script_dir/Info.plist" "$app_dir/Contents/Info.plist"

installed_icon="/Applications/Codex Account Bar.app/Contents/Resources/AppIcon.icns"
if [[ -f "$installed_icon" ]]; then
  cp "$installed_icon" "$app_dir/Contents/Resources/AppIcon.icns"
fi

xattr -cr "$app_dir"
codesign --force --deep --sign - "$app_dir"
xattr -d com.apple.FinderInfo "$app_dir" 2>/dev/null || true
xattr -d 'com.apple.fileprovider.fpfs#P' "$app_dir" 2>/dev/null || true
xattr -cr "$app_dir"
echo "$app_dir"
