# Numeric scene rows: port, group, target port, threshold, uplink, speed,
# brightness, gamma. Each palette row holds two RGB triples per port.
function configuration() {
    group[$1]=$2; source[$1]=$3; threshold[$1]=$4; uplink[$1]=$5
    speed[$1]=$6; brightness[$1]=$7; gamma[$1]=$8
}
# Open the replacement once per frame. A rename during a read leaves this frame
# on the old complete scene. Original RAM players pass the same four filenames.
BEGIN {
    scene="scene.active"
    if ((getline < scene)>0) {
        edited=1
        if (NF!=1 || ($1!=1 && $1!=120)) {bad=1; exit 2}
        count=$1
        for (row=1;row<=10;row++) {
            if ((getline < scene)!=1 || NF!=9 || $1!=row) {bad=1; exit 2}
            configuration()
            if ($2>0) reactive=1
        }
        wanted=int(stamp*4)%count+1
        for (row=1;row<=wanted;row++) if ((getline < scene)!=1 || NF!=60) {bad=1; exit 2}
        for (i=1;i<=60;i++) color[i]=$i
    }
    close(scene)
}
FILENAME == "config" {
    if ($2>0) originalReactive=1
    if (edited) gamma[$1]=$8
    if (!edited) configuration()
    next
}
FILENAME == "palette" {
    if (!edited && FNR == frame+1) for (i=1; i<=NF; i++) color[i]=$i
    next
}
FILENAME == "previous" {
    oldtime=$2; oldbytes[$1]=$3; oldvalid[$1]=$4; level[$1]=$5; target[$1]=$6
    next
}
function traffic() {
    for (i=1; i<NF; i++) {
        if ($i !~ /^[1-9][0-9]*$/ || $(i+1) !~ /^[UD]\/[UD]$/) continue
        p=$i+0
        if (p>10) break
        if (seen[p]++) { bad=1; break }
        eligible[p]=substr($(i+1),3,1)=="U" && (uplink[p] || i==1 || $(i-1)!="U")
        j=i+2
        if ($j ~ /^[0-9]+[FH]$/) j++
        if ($j ~ /^[0-9]+$/ && $(j+1) ~ /^[0-9]+$/ && $(j+2) ~ /^[0-9]+$/ && $(j+3) ~ /^[0-9]+$/) {
            bytes[p]=$(j+2)+$(j+3); valid[p]=1
        }
        break
    }
}
{traffic()}
END {
    # The original player polls traffic only if the initial scene needed it.
    if (edited && reactive && !originalReactive && !bad) {
        poll="timeout -t 3 swctrl port show"
        while ((poll | getline)>0) traffic()
        if (close(poll)!=0) bad=1
    }
    if (bad || !(1 in color) || !(60 in color)) exit 2
    dt=oldtime ? stamp-oldtime : 0
    if (dt<0) exit 2
    for (p=1; p<=10; p++) {
        if (group[p]>0 && !seen[p]) exit 2
        if (group[p]>0 && dt>0 && valid[p] && oldvalid[p] && eligible[p] && bytes[p]>=oldbytes[p])
            rate[p]=(bytes[p]-oldbytes[p])*8/dt
        g=group[p]
        if (scale[g]<threshold[p]*1000000) scale[g]=threshold[p]*1000000
        if (scale[g]<rate[p]) scale[g]=rate[p]
        if (g>0) {
            tau=(target[p]>level[p] ? 0.25 : 1.2)/speed[p]
            level[p]+=(target[p]-level[p])*(1-exp(-dt/tau))
        }
    }
    for (p=1; p<=10; p++) {
        g=group[p]
        printf "%d %.6f %.0f %d %.12f %.12f\n",p,stamp,bytes[p],valid[p],level[p],(g>0 ? rate[p]/scale[g] : 0) > "next"
        s=source[p]
        if (s==0) continue
        printf "%d",p
        for (c=1; c<=3; c++) {
            lo=color[6*(s-1)+c]; hi=color[6*(s-1)+3+c]
            value=lo+(hi-lo)*level[s]
            pwm=int(0.5+65280*(value/255*brightness[p]/100)^gamma[p])
            if (pwm<0 || pwm>65280) exit 2
            printf " %d",pwm
        }
        print ""
    }
}
