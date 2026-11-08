# Numeric scene rows: port, group, target port, threshold, uplink, speed,
# brightness, gamma. Each palette row holds two RGB triples per port.
function configuration() {
    group[$1]=$2; source[$1]=$3; threshold[$1]=$4; uplink[$1]=$5
    speed[$1]=$6; brightness[$1]=$7; gamma[$1]=$8
    mode[$1]=$10+0
    if ($10==1) custom[$1]=$11
    if ($10==2) {position[$1]=$11; width[$1]=$13; members[$1]=$12}
    if ($10==3) needsPoe=1
    if ($10==4) {
        needsClock=1; night[$1]=$11; stops[$1]=$12
        for (j=0;j<$12;j++) {
            hour[$1,j]=$(13+j*4)
            for (c=1;c<=3;c++) daylight[$1,j,c]=$(13+j*4+c)
        }
    }
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
            if ((getline < scene)!=1 || NF<9 || $1!=row) {bad=1; exit 2}
            configuration()
            if ($2>0 || $10==1 || $10==2) reactive=1
        }
        wanted=int(stamp*4)%count+1
        for (row=1;row<=wanted;row++) if ((getline < scene)!=1 || (NF!=60 && NF!=120)) {bad=1; exit 2}
        for (i=1;i<=NF;i++) color[i]=$i
    }
    close(scene)
}
FILENAME == "config" {
    if ($2>0 || $10==1 || $10==2) originalReactive=1
    if (!edited) configuration()
    next
}
FILENAME == "palette" {
    if (!edited && FNR == frame+1) for (i=1; i<=NF; i++) color[i]=$i
    next
}
FILENAME == "previous" {
    oldtime=$2; oldbytes[$1]=$3; oldvalid[$1]=$4; level[$1]=$5; target[$1]=$6
    powered[$1]=$7; powerTarget[$1]=$8
    next
}
function traffic() {
    for (i=1; i<NF; i++) {
        if ($i !~ /^[1-9][0-9]*$/ || $(i+1) !~ /^[UD]\/[UD]$/) continue
        p=$i+0
        if (p>10) break
        if (seen[p]++) { bad=1; break }
        online[p]=substr($(i+1),3,1)=="U"; trunk[p]=(i>1 && $(i-1)=="U")
        eligible[p]=online[p] && (uplink[p] || !trunk[p])
        j=i+2
        if ($j ~ /^[0-9]+[FH]$/) {mbps[p]=$j+0; j++}
        if ($j ~ /^[0-9]+$/ && $(j+1) ~ /^[0-9]+$/ && $(j+2) ~ /^[0-9]+$/ && $(j+3) ~ /^[0-9]+$/) {
            bytes[p]=$(j+2)+$(j+3); valid[p]=1
        }
        break
    }
}
{traffic()}
function clamp(x) {return x<0?0:(x>1?1:x)}
function ease(x) {x=clamp(x); return x*x*(3-2*x)}
function daylightChannel(s,c,    i,f) {
    for (i=1;i<stops[s];i++) if (localHour<=hour[s,i]) break
    if (i>=stops[s]) i=stops[s]-1
    f=clamp((localHour-hour[s,i-1])/(hour[s,i]-hour[s,i-1]))
    return (daylight[s,i-1,c]*(1-f)+daylight[s,i,c]*f)*(night[s]+(1-night[s])*ease((localHour-6)/2)*(1-ease((localHour-20)/2)))
}
function trail(s,    n,phase,d,pulse) {
    n=members[s]; phase=stamp*speed[s]; phase-=int(phase)
    d=position[s]-phase*n; if (d<0) d=-d; if (d>n/2) d=n-d
    pulse=n==1 ? (1+sin(6.2831853*phase))/2 : (d>=width[s] ? 0 : (1+cos(3.14159265*d/width[s]))/2)
    return 0.3+0.2*pulse+0.5*pulse*level[s]
}
END {
    # The original player polls traffic only if the initial scene needed it.
    if (edited && reactive && !originalReactive && !bad) {
        poll="timeout -t 3 swctrl port show"
        while ((poll | getline)>0) traffic()
        if (close(poll)!=0) bad=1
    }
    if (bad || !(1 in color) || !(60 in color)) exit 2
    if (needsClock) {
        clock="date '+%H %M %S'"
        if ((clock | getline)!=1 || NF!=3) exit 2
        localHour=$1+$2/60+$3/3600
        if (close(clock)!=0) exit 2
    }
    if (needsPoe) {
        poll="timeout -t 3 swctrl poe show"
        while ((poll | getline)>0) if ($1 ~ /^[0-9]+$/) {
            on=0; nfloats=0
            for (i=2;i<=NF;i++) {
                if (tolower($i)=="on") on=1
                if ($i ~ /^[0-9]+\.[0-9]+$/) floats[++nfloats]=$i
            }
            if (on && nfloats) watts[$1]=(nfloats>=3?floats[nfloats-2]:floats[1])
        }
        if (close(poll)!=0) exit 2
    }
    dt=oldtime ? stamp-oldtime : 0
    if (dt<0) exit 2
    for (p=1; p<=10; p++) {
        if ((group[p]>0 || mode[p]==1 || mode[p]==2) && !seen[p]) exit 2
        if (dt>0 && valid[p] && oldvalid[p] && eligible[p] && bytes[p]>=oldbytes[p])
            rate[p]=(bytes[p]-oldbytes[p])*8/dt
        g=group[p]
        if (scale[g]<threshold[p]*1000000) scale[g]=threshold[p]*1000000
        if (scale[g]<rate[p]) scale[g]=rate[p]
        if (g>0) {
            tau=(target[p]>level[p] ? 0.25 : 1.2)/speed[p]
            level[p]+=(target[p]-level[p])*(1-exp(-dt/tau))
        }
        if (mode[p]==1) {
            full=(mbps[p]>0 ? mbps[p] : 1000)*1000
            load=rate[p]>1000 ? clamp(log(rate[p]/1000)/log(full)) : 0
            sm=dt>0 ? speed[p]^(dt*4) : 0
            level[p]=(online[p] && !(trunk[p] && custom[p])) ? sm*level[p]+(1-sm)*load : 0
        } else if (mode[p]==2) {
            load=clamp(rate[p]/(threshold[p]*1000000))
            level[p]+=(load-level[p])*(1-exp(-dt/(load>level[p]?0.35:1.4)))
            if (!online[p]) level[p]=0
        } else if (mode[p]==3) {
            a=1-exp(-dt*speed[p]/0.6)
            level[p]+=(target[p]-level[p])*a
            powered[p]+=(powerTarget[p]-powered[p])*a
            target[p]=clamp(log(1+watts[p])/log(61)); powerTarget[p]=watts[p]>0
        }
    }
    for (p=1; p<=10; p++) {
        g=group[p]
        printf "%d %.6f %.0f %d %.12f %.12f %.12f %d\n",p,stamp,bytes[p],valid[p],level[p],(g>0 ? rate[p]/scale[g] : target[p]),powered[p],powerTarget[p] > "next"
        s=source[p]
        if (s==0) continue
        printf "%d",p
        for (c=1; c<=3; c++) {
            lo=color[6*(s-1)+c]; hi=color[6*(s-1)+3+c]
            value=lo+(hi-lo)*level[s]
            aux=color[60+6*(s-1)+c]
            if (mode[s]==1 && !online[s]) value=aux
            else if (mode[s]==1 && trunk[s] && custom[s]) value=color[60+6*(s-1)+3+c]
            else if (mode[s]==2) value=online[s] ? value*trail(s) : aux
            else if (mode[s]==3) value=lo*(1-powered[s])+hi*(powered[s]-level[s])+aux*level[s]
            else if (mode[s]==4) value=daylightChannel(s,c)
            pwm=int(0.5+65280*(value/255*brightness[p]/100)^gamma[p])
            if (pwm<0 || pwm>65280) exit 2
            printf " %d",pwm
        }
        print ""
    }
}
