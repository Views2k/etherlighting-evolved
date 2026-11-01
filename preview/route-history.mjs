const metadata = state => {
  const value=state?.eeRoute;
  return value&&typeof value.chain==='string'&&typeof value.id==='string'&&Number.isInteger(value.position)&&value.position>=0?value:null;
};

export function createRouteHistory(environment = window) {
  const {history,location}=environment;
  const id=()=>globalThis.crypto.randomUUID();
  const fields=()=>history.state&&typeof history.state==='object'&&!Array.isArray(history.state)?history.state:{};
  const url=()=>location.pathname+location.search+location.hash;
  let marker=metadata(history.state);
  if(!marker){marker={chain:id(),id:id(),position:0};history.replaceState({...fields(),eeRoute:marker},'',url());}
  let chain=marker.chain;
  const read=()=>{
    const value=metadata(history.state),native=environment.navigation?.currentEntry;
    return {id:value?.chain===chain?value.id:native?.key??null,
      position:value?.chain===chain?value.position:null,
      kind:value?.chain===chain?'managed':'browser',nativeKey:native?.key??null,
      hash:location.hash,url:url(),marker:value};
  };
  const same=(first,second)=>first.id&&second.id?first.id===second.id:
    first.position===second.position&&first.kind===second.kind&&first.url===second.url;
  let committed=read(),restoring=null;
  const deltaTo=target=>{
    const current=read();
    let nativeDelta=null;
    if(current.nativeKey&&target.nativeKey&&typeof environment.navigation?.entries==='function'){
      const entries=environment.navigation.entries();
      const start=entries.find(entry=>entry.key===current.nativeKey),end=entries.find(entry=>entry.key===target.nativeKey);
      if(!start||!end)return null;
      nativeDelta=end.index-start.index;
    }
    // Browser history limits can shift native indices after a push completes.
    if(current.marker&&target.marker&&current.marker.chain===target.marker.chain)
      return target.marker.position-current.marker.position;
    return nativeDelta;
  };
  const move=target=>{
    if(restoring)return restoring.promise;
    if(same(read(),target))return Promise.resolve(true);
    const delta=deltaTo(target);
    if(delta===null||delta===0)return Promise.resolve(false);
    let resolve;const promise=new Promise(done=>{resolve=done;});
    restoring={target,promise,resolve};
    try{history.go(delta);}catch{restoring=null;resolve(false);}
    return promise;
  };
  return {
    observe(){
      const current=read();
      if(restoring&&same(current,restoring.target)){
        const completed=restoring;restoring=null;completed.resolve(true);return null;
      }
      return current;
    },
    ready(){return restoring?.promise??Promise.resolve(true);},
    reach:move,
    restore(){return move(committed);},
    commit(page,{fromHistory=false}={}){
      const hash=`#${page.toLowerCase()}`,current=read(),existing=metadata(history.state);
      if(fromHistory||location.hash===hash){
        if(existing?.chain!==chain)chain=id();
        const entry=existing?.chain===chain?existing:{chain,id:id(),position:0};
        if(location.hash!==hash||!existing||existing.chain!==chain||history.state?.page!==page)
          history.replaceState({...fields(),page,eeRoute:entry},'',hash);
      }else{
        const known=current.marker?.chain===chain;
        if(!known)chain=id();
        const position=known?current.marker.position+1:0;
        history.pushState({page,eeRoute:{chain,id:id(),position}},'',hash);
      }
      committed=read();
    }
  };
}
