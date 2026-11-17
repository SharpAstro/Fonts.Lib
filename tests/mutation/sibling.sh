#!/usr/bin/env bash
# Clone a sibling repository beside this one for a mutation run: sibling.sh <Name> [ref]
# The ref is the one given, else the one tests/mutation/siblings names for it (the branch this branch is stacked
# with), else the sibling's default branch. A ref that no longer exists (a merged branch) falls back to the default.
set -euo pipefail
name="$1"; ref="${2:-}"
here="$(cd "$(dirname "$0")" && pwd)"
if [ -z "$ref" ] && [ -f "$here/siblings" ]; then
  ref="$(sed -n "s/^${name}=//p" "$here/siblings" | head -n 1)"
fi
url="https://github.com/SharpAstro/${name}.git"
if [ -n "$ref" ] && git ls-remote --exit-code --heads "$url" "$ref" > /dev/null; then
  echo "cloning ${name} at ${ref}"
  git clone --quiet --depth 1 --branch "$ref" "$url" "$name"
else
  echo "cloning ${name} at its default branch (asked for: '${ref:-nothing}')"
  git clone --quiet --depth 1 "$url" "$name"
fi
