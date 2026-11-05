import {initializeTheme} from './theme.mjs';
import {browserRecoveryMessage} from './browser-support.mjs';

function recovery(message, retry) {
  const app=document.querySelector('#app');
  const panel=document.createElement('section');panel.className='connection-panel panel';
  const heading=document.createElement('h1');heading.textContent='Open the lighting workspace';
  const detail=document.createElement('p');detail.className='help-text';detail.textContent=message;
  panel.append(heading,detail);
  if(retry){
    const button=document.createElement('button');button.className='button primary';button.textContent='Reload';
    button.addEventListener('click',()=>location.reload());panel.append(button);
  }
  app.replaceChildren(panel);
}

initializeTheme();
const supportMessage=browserRecoveryMessage(globalThis);
if(supportMessage) recovery(supportMessage,false);
else import('./app.mjs').then(()=>{
  import('./desktop-chrome.mjs').catch(()=>{});
  if(new URLSearchParams(location.search).has('motion-check'))
    import('./frame-probe.mjs').catch(()=>console.warn('Performance diagnostics could not load.'));
},()=>recovery('The interface could not load. Reload this page to try again.',true));
