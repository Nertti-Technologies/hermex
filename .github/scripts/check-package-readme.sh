#!/usr/bin/env bash
# README.md is also the NuGet package readme (PackageReadmeFile in src/Hermex/Hermex.csproj).
# nuget.org shows raw HTML as text and cannot resolve links or images relative to the
# repository, so outside fenced code blocks this refuses both. Use Markdown and absolute URLs.
set -euo pipefail
readme="${1:-README.md}"

problems="$(awk '
  # Fenced code is skipped. A fence closes only with the same character, at least as long,
  # and nothing after it. (No {n,m} intervals: older mawk on the CI runner lacks them.)
  fence == "" && match($0, /^ ? ? ?(````*|~~~~*)/) {
    fence = substr($0, RSTART, RLENGTH); sub(/^ */, "", fence); opened = NR; next
  }
  fence != "" {
    line = $0; sub(/^ ? ? ?/, "", line); sub(/[[:space:]]*$/, "", line)
    if (line ~ /^(``*|~~*)$/ && substr(line, 1, 1) == substr(fence, 1, 1) && length(line) >= length(fence))
      fence = ""
    next
  }
  /^[[:space:]]*<[A-Za-z\/!]/ { printf "%d: raw HTML: %s\n", NR, $0 }
  {
    line = $0
    while (match(line, /\]\([^)]*\)/)) {
      target = substr(line, RSTART + 2, RLENGTH - 3)
      if (target !~ /^#/ && target !~ /^[A-Za-z][A-Za-z0-9+.-]*:/)
        printf "%d: relative link: %s\n", NR, target
      line = substr(line, RSTART + RLENGTH)
    }
  }
  END { if (fence != "") printf "%d: unterminated code fence (the rest of the file was not checked)\n", opened }
' "$readme")"

if [ -n "$problems" ]; then
  while IFS= read -r problem; do
    # Escape as GitHub documents for workflow commands, so README text cannot start one.
    message="${problem#*: } (nuget.org cannot render this)"
    message="${message//'%'/%25}"; message="${message//$'\r'/%0D}"
    echo "::error file=$readme,line=${problem%%:*}::$message"
  done <<< "$problems"
  exit 1
fi
echo "$readme renders on nuget.org."
