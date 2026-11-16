#!/bin/sh
# EE fan control for the USW-Pro-XG-8-PoE. Runs from its private directory in
# switch RAM, so a switch restart returns the fans to the switch's own control.
#
#   sh fans.sh check            print "ok" or the reason this switch is not supported
#   sh fans.sh run              control the fans from ./config until ./stop exists
#   sh fans.sh guard <pid>      started by run: returns the fans to the switch if run stops
#   sh fans.sh compute <cpu> <sfp> <phy> <last> <integral> <mode> [values]
#                               print "<duty> <integral> <reason>" for one step (tests)
#   sh fans.sh estimate <cpu> <average> <target>
#                               print the speed estimated to hold the target (tests)
#
# ./config holds one line: "manual <percent>", "target <celsius>" or
# "curve <celsius> <percent> ..." with up to eight points in rising order.
# While run is active, the switch's own fan daemon is paused (SIGSTOP) and both
# fan outputs follow EE. On stop, on any failure, or if run disappears, the
# daemon is resumed (SIGCONT) and takes the fans back.
umask 077
HW=${EE_FAN_HW:-/sys/class/hwmon/hwmon0/device}
PHY=${EE_FAN_PHY:-/proc/phy_temp}
SYSINFO=${EE_FAN_SYSINFO:-/proc/ubnthal/system.info}
TICK=${EE_FAN_TICK:-5}
MIN=30
# Thermal target tuning: percent per degree C (the boost from it is at most
# BOOST percent either way), and integral gain in thousandths of a percent per
# degree C per step. Within BAND degrees of the target the integral corrects
# at full rate; farther away at a sixteenth of it, so the fans do not pile on
# speed while the switch is still cooling down to the target.
KP=4
KI=150
BOOST=5
BAND=2
# The switch CPU settles about HEAT / (29.1 + fan percent) degrees C above the
# room (fitted to measured fan tests). HEAT is in millidegrees times tenths of
# a percent, so the arithmetic stays in whole numbers.
HEAT=39830000
# Every mode speeds the fans up as a sensor nears the switch's own limit.
CPU_GUARD="85 95"
SFP_GUARD="65 75"
PHY_GUARD="100 110"

num() { case "$1" in ''|*[!0-9]*) return 1;; esac; return 0; }

# Sets r to the guard duty for a reading in millidegrees ($1) between $2 and $3 C.
ramp() {
    if [ "$1" -le $(($2 * 1000)) ]; then r=0
    elif [ "$1" -ge $(($3 * 1000)) ]; then r=100
    else r=$((MIN + (100 - MIN) * ($1 - $2 * 1000) / (($3 - $2) * 1000))); fi
}

# One control step. Inputs: cpu sfp phy (millidegrees), last duty, integral,
# then the config words. Sets duty, integral and why.
step() {
    cpu=$1; sfp=$2; phy=$3; last=$4; integral=$5; mode=$6
    shift 6
    why=$mode
    case "$mode" in
        manual)
            num "$1" || return 1
            duty=$1 ;;
        target)
            num "$1" || return 1
            error=$((cpu - $1 * 1000))
            p=$((KP * error / 1000))
            [ "$p" -le "$BOOST" ] || p=$BOOST
            [ "$p" -ge $((-BOOST)) ] || p=$((-BOOST))
            rate=$KI
            [ "$error" -le $((BAND * 1000)) ] && [ "$error" -ge $((-BAND * 1000)) ] || rate=$((KI / 16))
            next=$((integral + rate * error / 1000))
            [ "$next" -ge 0 ] || next=0
            [ "$next" -le $(((100 - MIN) * 1000)) ] || next=$(((100 - MIN) * 1000))
            try=$((MIN + p + next / 1000))
            # Stop adding up while the output is pinned in the same direction.
            if ! { [ "$try" -gt 100 ] && [ "$error" -gt 0 ]; } && ! { [ "$try" -lt "$MIN" ] && [ "$error" -lt 0 ]; }; then
                integral=$next
            fi
            duty=$((MIN + p + integral / 1000)) ;;
        curve)
            [ $# -ge 2 ] && num "$1" && num "$2" || return 1
            duty=$2
            if [ "$cpu" -gt $(($1 * 1000)) ]; then
                while [ $# -ge 2 ]; do
                    t0=$1; d0=$2; shift 2
                    if [ $# -lt 2 ]; then duty=$d0; break; fi
                    num "$1" && num "$2" && [ "$1" -gt "$t0" ] || return 1
                    if [ "$cpu" -le $(($1 * 1000)) ]; then
                        duty=$((d0 + ($2 - d0) * (cpu - t0 * 1000) / (($1 - t0) * 1000)))
                        break
                    fi
                done
            fi
            # Slow down gradually as the switch cools: at most 2 percent per step.
            # A new setting is passed with last 0, so it takes effect at once.
            [ "$duty" -ge $((last - 2)) ] || duty=$((last - 2)) ;;
        *) return 1 ;;
    esac
    [ "$duty" -ge "$MIN" ] || duty=$MIN
    [ "$duty" -le 100 ] || duty=100
    guard=0
    set -- $CPU_GUARD; ramp "$cpu" "$1" "$2"; [ "$r" -le "$guard" ] || guard=$r
    set -- $SFP_GUARD; ramp "$sfp" "$1" "$2"; [ "$r" -le "$guard" ] || guard=$r
    set -- $PHY_GUARD; ramp "$phy" "$1" "$2"; [ "$r" -le "$guard" ] || guard=$r
    if [ "$guard" -gt "$duty" ]; then duty=$guard; why=guard; fi
    return 0
}

# Sets estimated to the fan speed that would hold the CPU at $1 C, from the
# CPU reading (cpu) and the recent average speed (average, tenths of a percent).
estimate() {
    room=$((cpu - HEAT / (291 + average)))
    need=$(($1 * 1000 - room))
    if [ "$need" -le 0 ]; then estimated=100; else estimated=$(((HEAT / need - 291) / 10)); fi
    [ "$estimated" -ge "$MIN" ] || estimated=$MIN
    [ "$estimated" -le 100 ] || estimated=100
}

daemons() { pidof ubnt-fanctrl 2>/dev/null; }
release() { for d in $(daemons); do kill -CONT "$d" 2>/dev/null; done; }
hold() { for d in $(daemons); do kill -STOP "$d" 2>/dev/null || return 1; done; return 0; }
mark() { printf '%s\n' "$1" > status.new && mv -f status.new status; }
# True when process $1 runs in this directory. A PID taken over by another process fails this check.
mine() { [ "$(readlink "/proc/$1/cwd" 2>/dev/null)" = "$(pwd -P)" ]; }

check() {
    [ "$(awk -F= '/systemid=/{print $2}' "$SYSINFO" 2>/dev/null)" = ed76 ] ||
        { echo "This switch model is not supported for fan control."; return 1; }
    name=; read -r name 2>/dev/null < "$HW/name"
    [ "$name" = adt7475 ] || { echo "The fan controller was not found."; return 1; }
    for f in pwm1 pwm2; do
        [ -w "$HW/$f" ] || { echo "The fan outputs cannot be written."; return 1; }
        mode=; read -r mode 2>/dev/null < "$HW/${f}_enable"
        [ "$mode" = 1 ] || { echo "The fan outputs are not in manual mode."; return 1; }
    done
    for f in temp1_input temp3_input fan1_input fan2_input; do
        [ -r "$HW/$f" ] || { echo "A fan or temperature reading is missing."; return 1; }
    done
    [ -r "$PHY/1" ] || { echo "The port temperature readings are missing."; return 1; }
    set -- $(daemons)
    [ $# -le 1 ] || { echo "More than one switch fan daemon is running."; return 1; }
    echo ok
}

# Reads the sensors into cpu, board, sfp, phy, rpm1 and rpm2; fails on a bad reading.
sample() {
    cpu=; board=; sfp=; rpm1=; rpm2=; phy=0; seen=0
    read -r cpu 2>/dev/null < "$HW/temp1_input"
    read -r board 2>/dev/null < "$HW/temp2_input"
    read -r sfp 2>/dev/null < "$HW/temp3_input"
    read -r rpm1 2>/dev/null < "$HW/fan1_input"
    read -r rpm2 2>/dev/null < "$HW/fan2_input"
    for i in 1 2 3 4 5 6 7 8; do
        v=; read -r v 2>/dev/null < "$PHY/$i"
        if num "$v"; then seen=$((seen + 1)); [ "$v" -le "$phy" ] || phy=$v; fi
    done
    num "$board" || board=0
    num "$rpm1" || rpm1=0
    num "$rpm2" || rpm2=0
    num "$cpu" && num "$sfp" && [ "$seen" -gt 0 ]
}

guard() {
    owner=$1
    num "$owner" || exit 1
    while :; do
        sleep "$TICK"
        if ! kill -0 "$owner" 2>/dev/null || ! mine "$owner"; then
            release; mark released; exit 0
        fi
        beat=; now=
        read -r beat rest < status 2>/dev/null
        read -r now rest < /proc/uptime
        now=${now%.*}
        if num "$beat" && [ $((now - beat)) -gt $((TICK * 6)) ]; then
            kill -KILL "$owner" 2>/dev/null
            release; mark released; exit 0
        fi
    done
}

run() {
    [ "$(check)" = ok ] || { mark unsupported; exit 1; }
    watcher=
    # On a clean exit the guard goes first, so it cannot write here while EE removes the directory.
    finish() { [ -z "$watcher" ] || kill "$watcher" 2>/dev/null; release; mark released; }
    trap finish EXIT
    # Only TERM: HUP and INT stay ignored as nohup and the launching shell left them, so closing
    # EE's SSH session does not stop fan control.
    trap 'exit 0' TERM
    sh fans.sh guard $$ </dev/null >/dev/null 2>&1 &
    watcher=$!
    last=50; integral=0; faults=0; active=
    read -r p1 < "$HW/pwm1" && num "$p1" && last=$((p1 * 100 / 255))
    # The CPU temperature follows the fan speed of the last couple of minutes,
    # so the target estimate uses that average (in tenths of a percent).
    average=$((last * 10))
    while [ ! -e stop ]; do
        if ! kill -0 "$watcher" 2>/dev/null; then
            sh fans.sh guard $$ </dev/null >/dev/null 2>&1 &
            watcher=$!
        fi
        hold || exit 1
        line=; read -r line < config 2>/dev/null
        set -- $line
        # A new setting starts at its own speed instead of easing from the old one.
        slew=$last
        [ "$active" = "$line" ] || slew=0
        if [ "$1" = target ] && num "$2" && [ "$active" != "$line" ] && sample; then
            # Start at the speed estimated to hold the target; the integral corrects the rest.
            estimate "$2"
            integral=$(((estimated - MIN) * 1000))
        fi
        if sample && step "$cpu" "$sfp" "$phy" "$slew" "$integral" $line; then
            average=$((average + (duty * 10 - average) / 24))
            faults=0
        else
            faults=$((faults + 1))
            [ "$faults" -lt 6 ] || exit 1
            duty=100; why=fault
        fi
        pwm=$(((duty * 255 + 50) / 100))
        echo "$pwm" > "$HW/pwm1" && echo "$pwm" > "$HW/pwm2" || exit 1
        last=$duty
        read -r now rest < /proc/uptime
        printf '%s %s %s %s %s %s %s %s %s %s\n' "${now%.*}" "$why" "$duty" "$rpm1" "$rpm2" \
            "$cpu" "$board" "$sfp" "$phy" "${1:-none}" > status.new && mv -f status.new status || exit 1
        # ./active confirms which setting the fans now follow.
        if [ "$faults" = 0 ] && [ "$active" != "$line" ]; then
            printf '%s\n' "$line" > active.new && mv -f active.new active || exit 1
            active=$line
        fi
        # Wait for the next step, but take a new setting or a stop request within a second.
        waited=0
        while [ "$waited" -lt "$TICK" ] && [ ! -e stop ]; do
            sleep 1
            waited=$((waited + 1))
            next=; read -r next 2>/dev/null < config
            [ "$next" = "$line" ] || break
        done
    done
    exit 0
}

case "$1" in
    check) check ;;
    run) run ;;
    guard) guard "$2" ;;
    compute)
        shift
        step "$@" || { echo invalid; exit 1; }
        echo "$duty $integral $why" ;;
    estimate)
        cpu=$2; average=$3
        num "$cpu" && num "$average" && num "$4" || { echo invalid; exit 1; }
        estimate "$4"
        echo "$estimated" ;;
    *) echo "usage: sh fans.sh check|run|guard <pid>|compute ...|estimate <cpu> <average> <target>"; exit 2 ;;
esac
