# Numeric scene rows: port, group, target port, threshold, uplink, speed,
# brightness, gamma. Each palette row holds two RGB triples per port.
FILENAME == "config" {
    group[$1]=$2; source[$1]=$3; threshold[$1]=$4; uplink[$1]=$5
    speed[$1]=$6; brightness[$1]=$7; gamma[$1]=$8; next
}
FILENAME == "palette" {
    if (FNR == frame+1) for (i=1; i<=NF; i++) color[i]=$i
    next
}
FILENAME == "previous" {
    oldtime=$2; oldbytes[$1]=$3; oldvalid[$1]=$4; level[$1]=$5; target[$1]=$6
    next
}
{
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
END {
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
