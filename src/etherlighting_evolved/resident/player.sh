#!/bin/sh
# Runs in the private scene directory on the switch RAM filesystem.
umask 077
mark() { printf '%s\n' "$1" > status.new && mv -f status.new status; }
# BusyBox on this firmware supports whole-second sleeps. Count every LED call,
# including optical acquisition, and reduce the budget when CPU headroom is low.
limit=$(awk 'NR==1{print int($9); exit}' config)
[ "$limit" -ge 1 ] && [ "$limit" -le 10 ] || exit 1
budget=$limit
sent=0
set_led() {
    if [ "$sent" -ge "$budget" ]; then sleep 1; sent=0; fi
    [ ! -e stop ] || return 1
    sent=$((sent+1))
    reply=$(ubus -t 2 call etherlight.mcu set "$1") || return 1
    printf '%s\n' "$reply" | awk '{body=body $0}
        END {exit !(body ~ /^[[:space:]]*\{[[:space:]]*"set"[[:space:]]*:[[:space:]]*"ok"[[:space:]]*\}[[:space:]]*$/)}'
}
pwm() { set_led "{\"port_pwm\":\"$1 $2 $3 $4 0\"}"; }
optical() {
    case "$1" in 9) mask=100;; 10) mask=200;; *) return 1;; esac
    set_led "{\"mcu_mode\":\"soc_port_map steady 0 $mask\"}"
}
prepare() {
    set_led '{"behavior":"steady"}' || return 1
    for p in 9 10; do
        if awk -v p="$p" '$1==p && $3>0 {found=1} END{exit !found}' config; then
            [ ! -e stop ] && pwm "$p" 0 0 0 && optical "$p" || return 1
        fi
    done
}
read -r start unused < /proc/uptime || exit 1
: > previous
: > ports
reactive=$(awk '$2>0{found=1} END{print found+0}' config)
count=$(wc -l < palette)
[ "$count" -gt 0 ] || exit 1
initialized=0
mark starting || exit 1
while [ ! -e stop ]; do
    read -r tag u n s idle wait irq soft steal rest < /proc/stat || break
    total=$((u+n+s+idle+wait+irq+soft+steal))
    if [ -n "$last_total" ] && [ $((total-last_total)) -gt 0 ]; then
        if [ $(((idle-last_idle)*100/(total-last_total))) -lt 25 ]; then
            budget=$(((limit+1)/2))
        else
            budget=$limit
        fi
    fi
    last_total=$total
    last_idle=$idle
    if [ "$initialized" = 0 ]; then
        if ! prepare; then mark retrying; sleep 3; continue; fi
        initialized=1
    fi
    read -r stamp unused < /proc/uptime || break
    frame=$(awk -v now="$stamp" -v start="$start" -v count="$count" 'BEGIN{print int((now-start)*4)%count}')
    if [ "$reactive" = 1 ] && ! timeout -t 3 swctrl port show > ports; then mark retrying; sleep 3; continue; fi
    if ! awk -v stamp="$stamp" -v frame="$frame" -f frames.awk config palette previous ports > output; then
        mark retrying; sleep 3; continue
    fi
    mv -f next previous || break
    ok=1
    while read -r port red green blue; do
        [ ! -e stop ] || break
        if ! pwm "$port" "$red" "$green" "$blue"; then ok=0; break; fi
        # Each write waits for the MCU acknowledgement. The frame delay bounds
        # traffic to ten per-port updates per second on this ten-port profile.
    done < output
    [ ! -e stop ] || break
    if [ "$ok" = 1 ]; then mark playing; sleep 1; else mark retrying; initialized=0; sleep 3; fi
    sent=0
done
mark stopped
