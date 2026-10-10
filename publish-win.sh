#!/usr/bin/env bash
# Cross-publish a self-contained single-file Windows build (no .NET install needed on the target).
# Usage: ./publish-win.sh [win-x64|win-x86|win-arm64]
set -euo pipefail
RID="${1:-win-x64}"
dotnet publish src/NetManager.App/NetManager.App.csproj \
  -c Release -r "$RID" --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true -p:DebugType=none -p:DebugSymbols=false \
  -o "publish/$RID"
rm -f "publish/$RID"/*.pdb
echo "→ publish/$RID/NetManager.exe"
