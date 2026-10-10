#!/usr/bin/env bash
# Regenerates the app icon files from the vector artwork:
#   src/NetManager.App/Assets/icon.png   (256 px, Window.Icon)
#   src/NetManager.App/Assets/app.ico    (16–256 px, the exe icon)
# Needs: dotnet and ImageMagick (`magick`). The artwork itself lives in Assets/icon.svg and is mirrored in tools/IconRender/Program.cs
# (ImageMagick/librsvg-less SVG renderers can't draw its gradient + round-capped strokes, and Quick Look adds a white background).
set -euo pipefail
cd "$(dirname "$0")/.."

out="$(mktemp -d)"
trap 'rm -rf "$out"' EXIT
dotnet run --project tools/IconRender -c Release -- "$out"

assets=src/NetManager.App/Assets
cp "$out/icon-256.png" "$assets/icon.png"
magick "$out"/icon-{16,20,24,32,40,48,64,128,256}.png "$assets/app.ico"
echo "→ $assets/icon.png, $assets/app.ico"
