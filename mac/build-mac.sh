#!/bin/bash
# XSD Audio Rack 一键编译+运行（在 Mac 终端里执行：bash build-mac.sh）
set -e
cd "$(dirname "$0")"
PROJ="XSDAudioRack.xcodeproj"

echo "== [1/3] 解析 Swift 组件（首次需联网下载 KeyboardShortcuts）=="
xcodebuild -project "$PROJ" -scheme XSDAudioRack -resolvePackageDependencies 2>&1 | tail -3

echo "== [2/3] 编译 Debug 版 =="
OUT=$(xcodebuild -project "$PROJ" -scheme XSDAudioRack -configuration Debug -derivedDataPath build build 2>&1 || true)
echo "$OUT" | grep -E "error:" || true
echo "$OUT" | grep -E "BUILD" || true

if ! echo "$OUT" | grep -q "BUILD SUCCEEDED"; then
    echo "!! 编译失败：没运行（避免跑旧包），把上面的报错发给 Claude"
    exit 1
fi

APP="build/Build/Products/Debug/XSDAudioRack.app"
if [ ! -d "$APP" ]; then
    echo "!! 编译产物缺失：没找到 $APP，把上面的输出发给 Claude"
    exit 1
fi

echo "== [3/3] 运行（窗口死住的话按 Ctrl+C 停止，然后把终端输出发给 Claude）=="
"$APP/Contents/MacOS/XSDAudioRack"
