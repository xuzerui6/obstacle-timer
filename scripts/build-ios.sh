#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")/../mobile/ios"
if [[ "$(uname -s)" != Darwin ]]; then echo 'iOS build requires macOS + Xcode.' >&2; exit 1; fi
if [[ -z "${APPLE_TEAM_ID:-}" ]]; then
  xcodebuild -project FinishCamera.xcodeproj -scheme FinishCamera -configuration Debug -sdk iphonesimulator -destination 'generic/platform=iOS Simulator' CODE_SIGNING_ALLOWED=NO build
else
  xcodebuild -project FinishCamera.xcodeproj -scheme FinishCamera -configuration Release -destination 'generic/platform=iOS' -archivePath ../../artifacts/FinishCamera.xcarchive DEVELOPMENT_TEAM="$APPLE_TEAM_ID" -allowProvisioningUpdates archive
  echo 'Archive created. Export a development/ad-hoc build in Xcode Organizer using your signing profile.'
fi
