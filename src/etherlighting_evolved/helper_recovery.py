"""Bounded cleanup of shell helpers belonging to one lost EE connection."""

import re

CONFIRMED = '__EE_HELPERS_CLOSED__'


def cleanup_command(tags: list[str]) -> str:
    if not tags or len(tags) > 16 or len(set(tags)) != len(tags):
        raise ValueError('Invalid helper ownership set')
    if any(not isinstance(tag, str) or not re.fullmatch(r'ee_[0-9a-f]{32}', tag) for tag in tags):
        raise ValueError('Invalid helper ownership token')
    patterns = ' | '.join(f"'sh -s {tag} ' | '/bin/sh -s {tag} '" for tag in tags)
    return r'''[ -r /proc/self/cmdline ] && [ -r /proc/self/stat ] || exit 1
ee_helpers=''
for ee_path in /proc/[0-9]*/cmdline; do
  ee_dir=${ee_path%/cmdline}
  if [ ! -r "$ee_path" ]; then
    [ ! -d "$ee_dir" ] || exit 1
    continue
  fi
  ee_cmd=$(tr '\000' ' ' < "$ee_path" 2>/dev/null) || {
    [ ! -d "$ee_dir" ] || exit 1
    continue
  }
  case "$ee_cmd" in
    PATTERNS)
      ee_dir=${ee_path%/cmdline}
      ee_pid=${ee_dir##*/}
      ee_before=$(cat "$ee_dir/stat" 2>/dev/null) || exit 1
      [ "$(tr '\000' ' ' < "$ee_path" 2>/dev/null)" = "$ee_cmd" ] || exit 1
      ee_start=$(printf '%s' "$ee_before" | cut -d ' ' -f 22)
      case "$ee_start" in ''|*[!0-9]*) exit 1;; esac
      [ "$(cat "$ee_dir/stat" 2>/dev/null | cut -d ' ' -f 22)" = "$ee_start" ] || exit 1
      kill -TERM "$ee_pid" || exit 1
      ee_helpers="$ee_helpers $ee_path"
      ;;
  esac
done
for ee_try in 1 2 3; do
  ee_wait=0
  for ee_path in $ee_helpers; do
    ee_dir=${ee_path%/cmdline}
    if [ ! -r "$ee_path" ]; then
      [ ! -d "$ee_dir" ] || exit 1
      continue
    fi
    ee_cmd=$(tr '\000' ' ' < "$ee_path" 2>/dev/null) || {
      [ ! -d "$ee_dir" ] || exit 1
      continue
    }
    case "$ee_cmd" in PATTERNS) ee_wait=1;; esac
  done
  [ "$ee_wait" = 0 ] && { printf '__EE_HELPERS_CLOSED__\n'; exit 0; }
  sleep 1
done
exit 1
'''.replace('PATTERNS', patterns)
