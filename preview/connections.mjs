import {confirmAction} from './confirmation.mjs';
import {controllerConnectionStatus} from './workspace-session.mjs';

export function showConnectionStatus(element, {label, tone}) {
  if (element.textContent !== label) element.textContent = label;
  for (const name of ['online', 'waiting', 'is-disconnected', 'error']) element.classList.toggle(name, name === tone);
}

const node = (tag, className = '', text) => {
  const element = document.createElement(tag);
  if (className) element.className = className;
  if (text !== undefined) element.textContent = text;
  return element;
};

const messages = {
  auth_failed: 'The switch rejected the login. Check your SSH username and password.',
  authentication_failed: 'The switch rejected the login. Check your SSH username and password.',
  missing_host: 'Enter the switch address.',
  missing_username: 'Enter your SSH username.',
  bad_port: 'Enter an SSH port between 1 and 65535.',
  host_key_changed: 'The switch identity has changed. Verify its SSH host key before reconnecting.',
  unreachable: 'The switch could not be reached. Check its address and that SSH is enabled.',
  connect_failed: 'The switch could not be reached. Check its address and that SSH is enabled.',
  ssh_error: 'SSH could not connect. Check the switch address and SSH settings.',
  no_etherlighting: 'The switch connected, but its lighting service is unavailable. Check that this model and firmware support Etherlighting.',
  output_recovery_required: 'An earlier lighting session did not stop cleanly and EE could not return the switch lighting to its defaults yet. Check that the switch is reachable, then connect again to retry the recovery.',
  handoff_unconfirmed: 'Handoff could not be confirmed. Automatic switch connection is paused. Choose Retry handoff to complete cleanup before reconnecting.',
  handoff_recovery_required: 'The previous handoff needs a recovery review before reconnecting. Keep EE disconnected until that review is complete.',
  handoff_save_failed: 'Your lighting setup or handoff preference could not be saved. Retry handoff before closing EE.',
  instance_running: 'EE is already running. Use the open EE window or close it before opening another.',
  instance_unavailable: 'EE could not verify whether another session is running. Check the running EE sessions before trying again.',
  compatibility_verification_required: 'This switch model or firmware needs compatibility verification before lighting control. Use a verified EE switch and firmware profile to continue.',
  saved_login_failed: 'The login could not be saved securely. Turn off Save login and try again.',
  saved_login_unavailable: 'The saved login could not be opened. Enter your SSH credentials to connect.',
  no_saved_login: 'Enter your SSH credentials to connect. Choose Save login to use them next time.',
  saved_login_forget_failed: 'The saved login could not be removed. Try again.',
  busy: 'Another connection is in progress. Wait for it to finish.',
  connection_in_progress: 'Another connection is in progress. Wait for it to finish.',
  timeout: 'The connection timed out. Check that the device is available and try again.',
  unavailable: 'The local connection service is unavailable. Reopen this preview and try again.',
  controller_auth_failed: 'UniFi Network rejected the login. Use a local controller account and check its password.',
  controller_unreachable: 'UniFi Network could not be reached. Check its address and port.',
  controller_invalid_url: 'Enter the controller’s HTTPS address, including its port if required.',
  controller_invalid_response: 'The controller response could not be read. Check the UniFi Network address.',
  site_required: 'Choose the UniFi site that contains your switch.',
  switch_not_found: 'The connected switch was not found in this site. Check the selected site.',
};

export function connectionError(error, fallback = 'The connection could not be confirmed. Check the details and try again.') {
  const message = typeof error?.code === 'string' && Object.hasOwn(messages,error.code) ? messages[error.code] : null;
  return message || (error?.name === 'TimeoutError' ? messages.timeout : fallback);
}

function handoffNeedsReview(session) {return session?.error?.code==='handoff_recovery_required';}
function handoffBlocked(session) {return ['pending','unconfirmed'].includes(session?.handoff_state)||handoffNeedsReview(session);}
function handoffRetryable(session) {return session?.handoff_state==='unconfirmed'&&!handoffNeedsReview(session);}

export function handoffComplete(session) {
  return session?.handed_off===true&&session.handoff_state==='complete'&&session.logged_in===false&&session.connected===false&&session.connecting!==true&&session.automatic_connect_paused===true&&!handoffNeedsReview(session);
}

export function handoffStatus(session) {
  if(handoffNeedsReview(session))return {message:messages.handoff_recovery_required,error:true,tone:''};
  if(session?.handoff_state==='pending')return {message:'Handing lighting back to UniFi…',error:false,tone:'waiting'};
  if(session?.handoff_state==='unconfirmed')return {message:messages.handoff_unconfirmed,error:true,tone:''};
  if(handoffComplete(session))return {message:'Lighting returned to UniFi. Your saved logins and profiles are kept. Connect when you want EE to control lighting again.',error:false,tone:'online'};
  if(session?.automatic_connect_paused&&!session.logged_in)return {message:'Automatic switch connection is paused. Connect when you want EE to control lighting again.',error:false,tone:''};
  return null;
}

export async function requestHandoff(request, isActive = () => true) {
  try{
    const session=await request('/api/session/handoff','POST');if(!isActive())return null;
    if(handoffComplete(session))return {session,error:null};
    throw new Error('Handoff could not be confirmed.');
  }catch(error){
    if(!isActive())return null;
    let session;try{session=await request('/api/session');}catch{}
    if(!isActive())return null;
    return {session,error:handoffComplete(session)?null:handoffNeedsReview(session)?messages.handoff_recovery_required:connectionError(error,'Handoff could not be confirmed. Refresh connection status before reconnecting.')};
  }
}

const controllerMessages = {
  invalid_url: 'Enter the UniFi Network HTTPS address, including its port if required.',
  invalid_controller_kind: 'Choose the type of UniFi controller you use.',
  invalid_auth_method: 'Choose a sign-in method supported by this controller type.',
  api_key_required: 'Enter your UniFi API key.',
  invalid_api_key: 'Enter the API key exactly as it was created in UniFi, without spaces.',
  invalid_console_id: 'Choose the console you want to connect.',
  invalid_cloud_origin: 'Use the UniFi cloud connector address.',
  switch_identity_required: 'Reconnect your switch using its IP address, then connect this controller.',
  api_response_incomplete: 'The controller returned incomplete details. Check its Network version and API access, then try again.',
  integration_unavailable: 'The Network API is unavailable at this address. Check the controller type, Network version, and API access.',
  no_consoles: 'No consoles are available to this API key. Check its access in UniFi Site Manager.',
  invalid_site: 'Choose a valid UniFi site.',
  invalid_certificate: 'Verify the controller certificate fingerprint and try connecting again.',
  invalid_request: 'Check the controller connection details and try again.',
  credentials_required: 'Enter your UniFi Network username and password.',
  authentication_failed: 'UniFi Network rejected the credentials. Check the selected sign-in method and credentials.',
  unreachable: 'UniFi Network could not be reached. Check its address and port.',
  unsupported_controller: 'Check the controller type and its direct HTTPS address, including the port.',
  controller_local_account_required: 'Use a local UniFi account with Network access.',
  invalid_response: 'The controller response could not be read. Check the UniFi Network address.',
  rate_limited: 'UniFi Network asked EE to wait. Try connecting again in a moment.',
  controller_error: 'UniFi Network could not complete the request. Try again.',
  no_sites: 'This account has no available UniFi sites. Check its site access.',
  site_required: 'Choose the UniFi site that contains your switch.',
  site_not_found: 'The selected UniFi site is no longer available. Choose a site again.',
  switch_not_found: 'The connected switch was not found in this UniFi site. Check the selected site.',
  switch_ambiguous: 'More than one controller device matched this switch. Check the switch identity in UniFi Network.',
  switch_required: 'Connect your switch before linking its UniFi Network details.',
  redirect_blocked: 'The controller redirected this request. Enter the final UniFi Network HTTPS address.',
  response_too_large: 'The controller returned more data than expected. Check the UniFi Network address.',
  two_factor_required: 'Enter the verification code for your UniFi account.',
  verification_failed: 'The verification code was not accepted. Enter a new code and try again.',
  controller_login_required: 'Connect your controller before saving its login.',
  controller_login_save_failed: 'The controller login could not be saved securely. Try Save reconnect preference again.',
  controller_login_remove_failed: 'The saved controller login could not be removed. Try Save reconnect preference again.',
  saved_controller_login_unavailable: 'The saved controller login could not be opened. Connect your controller again.',
};

export function controllerError(error, fallback = 'The controller connection could not be confirmed. Check the address and login, then try again.') {
  if (error?.code === 'controller_error') {
    const detail = error.details || error.detail || error;
    const steps = {login:'sign-in', sites:'site discovery', devices:'switch discovery', clients:'client discovery'};
    if (typeof detail.operation === 'string' && Object.hasOwn(steps, detail.operation)) {
      const status = Number.isInteger(detail.upstream_status) && detail.upstream_status >= 400 && detail.upstream_status <= 599
        ? ` (HTTP ${detail.upstream_status})` : '';
      return `UniFi Network could not complete ${steps[detail.operation]}${status}. Try connecting again.`;
    }
  }
  const message = typeof error?.code === 'string' && Object.hasOwn(controllerMessages, error.code) ? controllerMessages[error.code] : null;
  return message || (error?.name === 'TimeoutError' || error?.code === 'timeout' ? 'UniFi Network did not respond in time. Check the controller and try again.' : fallback);
}

export function controllerReconnectControls(current = {}, draft) {
  const supported = current.preferences_supported === true;
  const saved = current.auto_reconnect === true;
  const checked = typeof draft === 'boolean' ? draft : saved;
  return {supported, checked, changed:checked !== saved,
    canApply:supported && (checked ? current.can_save_login === true && (!saved || current.saved_login_matches_connection === false) : saved)};
}

export function controllerReconnectConfirmed(current, enabled) {
  return typeof enabled === 'boolean' && current?.preferences_supported === true && !current.saved_login_error &&
    current.auto_reconnect === enabled && current.saved_login_available === enabled &&
    (!enabled || current.saved_login_matches_connection === true);
}

function controllerOrigin(value) {
  if(typeof value!=='string'||/[\s]/.test(value))return null;
  try{
    const url=new URL(value);
    return url.protocol==='https:'&&url.hostname&&!url.username&&!url.password&&['','/'].includes(url.pathname)&&!url.search&&!url.hash?url.origin:null;
  }catch{return null;}
}

export function controllerSupportedMethods(current = {}) {
  const allowed={self_hosted:['password'],unifi_os:['password','api_key'],cloud_connector:['api_key']};
  const advertised=current.supported_auth_methods;
  if(advertised===undefined)return {self_hosted:['password'],unifi_os:['password'],cloud_connector:[]};
  return Object.fromEntries(Object.entries(allowed).map(([kind,methods])=>[kind,
    methods.filter(method=>Array.isArray(advertised?.[kind])&&advertised[kind].includes(method))]));
}

function sameControllerTarget(target, values) {
  return Boolean(target?.url&&controllerOrigin(values.url?.trim())===controllerOrigin(target.url)
    &&(target.controller_kind||'self_hosted')===values.controller_kind
    &&(target.auth_method||'password')===(values.auth_method||'password')
    &&(target.console_id||null)===(values.console_id||null));
}

export function controllerConnectionProblem(values, current = {}) {
  const methods=controllerSupportedMethods(current),method=values.auth_method||'password';
  if(!['self_hosted','unifi_os','cloud_connector'].includes(values.controller_kind)||!methods[values.controller_kind]?.length)return {field:'controller_kind',message:controllerMessages.invalid_controller_kind};
  if(!methods[values.controller_kind].includes(method))return {field:'auth_method',message:controllerMessages.invalid_auth_method};
  const origin=controllerOrigin(values.url?.trim());
  if(!origin)return {field:'url',message:'Enter the controller’s direct HTTPS address and port, without a page path, query, or account details.'};
  if(values.controller_kind==='cloud_connector'){
    if(values.url?.trim()!=='https://api.ui.com')return {field:'url',message:controllerMessages.invalid_cloud_origin};
    if(!/^[A-Za-z0-9_-]{1,128}(?::[0-9]{1,20})?$/.test(values.console_id||''))return {field:'console_id',message:controllerMessages.invalid_console_id};
  }else if(values.console_id)return {field:'console_id',message:controllerMessages.invalid_console_id};
  const savedTargets=[...(current.configured?[current]:[]),...(current.pending_site_choice?[current.pending_site_choice]:[])];
  const reuse=savedTargets.some(target=>sameControllerTarget(target,values));
  if(method==='api_key'){
    if(!values.api_key&&reuse)return null;
    if(!values.api_key)return {field:'api_key',message:controllerMessages.api_key_required};
    if(typeof values.api_key!=='string'||!/^[\x21-\x7e]{1,4096}$/.test(values.api_key))return {field:'api_key',message:controllerMessages.invalid_api_key};
    return null;
  }
  const username=values.username?.trim()||'',password=values.password||'';
  if(!username&&!password&&reuse)return null;
  if(!username)return {field:'username',message:'Enter your local UniFi username.'};
  if(!password)return {field:'password',message:'Enter your UniFi password.'};
  return null;
}

function input(name, type = 'text', autocomplete = 'off') {
  const control = node('input');
  control.name = name; control.type = type; control.autocomplete = autocomplete;
  control.spellcheck = false; control.autocapitalize = 'none';
  return control;
}

function field(label, control, help) {
  const wrapper = node('label', 'field');
  wrapper.append(node('span', '', label), control);
  if (help) wrapper.append(node('small', '', help));
  return wrapper;
}

function button(text, className = '') {
  const control = node('button', `button ${className}`, text);
  control.type = 'button'; return control;
}

function passwordField(label, control) {
  const wrapper = node('div', 'field');
  const title = node('label', '', label);
  control.id = `connection-${control.name}-${crypto.randomUUID()}`;
  title.htmlFor = control.id;
  const row = node('div', 'connection-secret');
  const reveal = button('Show', 'quiet');
  reveal.setAttribute('aria-label', `Show ${label.toLowerCase()}`);
  reveal.setAttribute('aria-pressed', 'false');
  reveal.addEventListener('click', () => {
    const visible = control.type === 'password'; control.type = visible ? 'text' : 'password';
    reveal.textContent = visible ? 'Hide' : 'Show';
    reveal.setAttribute('aria-label', `${visible ? 'Hide' : 'Show'} ${label.toLowerCase()}`);
    reveal.setAttribute('aria-pressed', String(visible));
  });
  row.append(control, reveal); wrapper.append(title, row); return wrapper;
}

function notice() {
  const element = node('p', 'notice connection-notice'); element.role = 'status';
  element.setAttribute('aria-live', 'polite'); element.setAttribute('aria-atomic','true');
  return [element, (text = '', error = false, tone = '') => {
    element.textContent = text; element.classList.toggle('error', error);
    element.classList.toggle('online', !error && tone === 'online');
    element.classList.toggle('waiting', !error && tone === 'waiting');
  }];
}

const privateAccessMessages = {
  tailscale_not_installed:'Install Tailscale on this computer, then refresh status.',
  tailscale_unavailable:'Open Tailscale on this computer, then refresh status.',
  tailscale_signin_required:'Sign in to Tailscale on this computer to continue setup.',
  tailscale_permission_required:'Tailscale needs permission to manage private access. Open EE as an administrator and try again.',
  tailscale_approval_required:'Complete the approval in Tailscale, then refresh status and apply the setting.',
  tailscale_not_running:'Connect Tailscale on this computer, then refresh status.',
  tailscale_device_identity_required:'Use a personal Tailscale account on the computer running EE, then refresh status.',
  private_access_port_change_requires_off:'Turn private access off and confirm cleanup before changing its port.',
  private_access_identity_changed:'The approved Tailscale account or computer changed. Turn private access off before setting it up again.',
  private_access_port_in_use:'Another service uses EE’s private access port. Keep that service unchanged and choose a separate port in Connection options.',
  private_access_route_unavailable:'Check Tailscale Serve on this PC, then refresh status.',
  private_access_funnel_enabled:'Turn off Tailscale Funnel for this address, then refresh status.',
  private_access_listener_failed:'EE could not open private access. Check the host app and try again.',
  private_access_save_failed:'The private access setting could not be saved. Try Apply setting again.',
  private_access_setup_unconfirmed:'Private access was not confirmed. Refresh status before trying again.',
  private_access_cleanup_unconfirmed:'Private access shutdown was not confirmed. Keep EE open and refresh status before trying again.',
  private_access_shutdown_unconfirmed:'Private access shutdown could not be confirmed. Review the local connection before retrying.',
  private_access_invalid_configuration:'Check the Tailscale configuration on this PC, then refresh status.',
  unavailable:'Private access could not be checked. Refresh status to try again.',
  forbidden:'Change this setting in EE on the host computer.',
};

export function privateAccessError(error) {
  const code=typeof error==='string'?error:error?.code;
  return typeof code==='string'&&Object.hasOwn(privateAccessMessages,code)
    ?privateAccessMessages[code]:'Private access could not be confirmed. Refresh status before trying again.';
}

export function privateAccessUrl(value) {
  if(typeof value!=='string'||/[\u0000-\u0020\u007f]/.test(value))return null;
  try {
    const url=new URL(value),label='[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?';
    if(url.protocol!=='https:'||!/^\d+$/.test(url.port)||Number(url.port)<1024||Number(url.port)>65535||url.username||url.password||url.search||url.hash||url.pathname!=='/'
      ||!new RegExp(`^${label}\\.${label}\\.ts\\.net$`).test(url.hostname))return null;
    return url.origin;
  }catch{return null;}
}

export function privateAccessSetupUrl(value, action) {
  if(typeof value!=='string'||/[\u0000-\u0020\u007f]/.test(value)||!/^https:\/\/(?:login\.tailscale\.com|tailscale\.com)\/[^#]*$/.test(value))return null;
  const path=value.split('?')[0].replace(/^https:\/\/[^/]+/,'');
  if(path.includes('%')||path.includes('\\')||path.split('/').some(part=>part==='.'||part==='..'))return null;
  try{
    const url=new URL(value);if(url.username||url.password||url.port||url.hash)return null;
    if(action==='install'&&url.origin==='https://tailscale.com'&&url.pathname==='/download'&&!url.search)return url.href;
    if(action==='approve'&&url.origin==='https://login.tailscale.com'&&(
      /^\/a\/[A-Za-z0-9_-]+$/.test(url.pathname)&&!url.search||url.pathname==='/f/serve'||url.pathname==='/admin/machines'&&!url.search))return url.href;
  }catch{}
  return null;
}

export function renderPrivateAccess(host, request, {signal} = {}) {
  let active=!signal?.aborted,current=null,draft=null,portDraft=null,pending=null,completion=Promise.resolve();
  host.replaceChildren();
  if(!active)return ()=>{};
  const section=node('section','private-access automation-section'),header=node('div','connection-heading');
  const title=node('h2','','Mobile & remote access'),refresh=button('Refresh private access','quiet');header.append(title,refresh);
  const state=node('p','connection-state waiting','Checking private access…');
  const help=node('p','help-text','Use Tailscale to open this workspace on your phone or another device.');
  const setup=node('div','connection-setup'),setupHelp=node('p','help-text','Install Tailscale on this computer and your phone or other device. Sign in with the same Tailscale account on both.');
  const setupActions=node('div','connection-actions'),setupLink=node('a','button'),signIn=button('Sign in to Tailscale','primary');
  setupLink.target='_blank';setupLink.rel='noopener noreferrer';setupActions.append(setupLink,signIn);setup.append(setupHelp,setupActions);setup.hidden=true;
  const toggle=input('private_access','checkbox'),label=node('label','check-row');label.append(toggle,node('span','','Use Tailscale'));
  const readOnly=node('p','help-text','Change this setting in EE on the host computer.');readOnly.hidden=true;
  const apply=button('Apply setting','primary'),actions=node('div','connection-actions');actions.append(apply);
  const advanced=node('details','connection-options'),port=input('private_https_port','number');port.min='1024';port.max='65535';port.step='1';port.inputMode='numeric';
  const resetPort=button('Reset port','quiet');resetPort.hidden=true;
  const portHelp=node('p','help-text','Use a free HTTPS port between 1024 and 65535. Turn off private access before changing it.');
  advanced.append(node('summary','','Connection options'),field('Private HTTPS port',port),resetPort,portHelp);
  const address=node('div','connection-actions'),open=node('a','button','Open private workspace'),copy=button('Copy private address','quiet');
  open.target='_blank';open.rel='noopener noreferrer';address.append(open,copy);address.hidden=true;
  const install=node('p','help-text','iPhone or iPad: Share → Add to Home Screen. Android: browser menu → Install app.');install.hidden=true;
  const [message,status]=notice();
  section.append(header,help,state,setup,label,advanced,readOnly,actions,address,install,message);host.append(section);
  const canConfigure=()=>current?.can_configure===true;
  const canRetry=()=>current?.enabled===true&&current?.available===true&&current?.ready!==true;
  const currentPort=()=>Number.isInteger(current?.https_port)&&current.https_port>=1024&&current.https_port<=65535?current.https_port:8444;
  const readyUrl=()=>{const url=privateAccessUrl(current?.url);return current?.available===true&&current.enabled===true&&current.ready===true&&url&&Number(new URL(url).port)===currentPort()?url:null;};
  const canChangePort=()=>canConfigure()&&current?.can_change_port===true&&current?.enabled!==true&&current?.ready!==true;
  const validPort=()=>/^\d+$/.test(portDraft??String(currentPort()))&&Number(portDraft??currentPort())>=1024&&Number(portDraft??currentPort())<=65535;
  const hasPortChange=()=>portDraft!==null&&Number(portDraft)!==currentPort();
  const render=()=>{
    if(!active)return;
    const enabled=current?.enabled===true,url=readyUrl(),ready=Boolean(url),chosen=draft??enabled;
    toggle.checked=chosen;toggle.disabled=Boolean(pending)||!canConfigure()||(!current?.available&&!enabled);
    const portChange=hasPortChange(),portAllowed=!portChange||canChangePort();
    apply.disabled=Boolean(pending)||!canConfigure()||!validPort()||!portAllowed||(chosen===enabled&&!portChange&&!(chosen&&canRetry()))||chosen&&!current?.available;
    apply.textContent=pending==='apply'?'Applying…':chosen===enabled&&canRetry()?'Retry private access':'Apply setting';refresh.disabled=Boolean(pending);
    section.setAttribute('aria-busy',String(Boolean(pending)));readOnly.hidden=!current||canConfigure();
    port.value=portDraft??String(currentPort());port.disabled=Boolean(pending)||!canChangePort();advanced.hidden=!current||!canConfigure();
    resetPort.hidden=portDraft===null;resetPort.disabled=Boolean(pending)||!canConfigure();
    const setupAction=current?.setup_action,setupUrl=privateAccessSetupUrl(current?.action_url,setupAction);
    setup.hidden=!canConfigure()||!['install','signin','approve'].includes(setupAction);
    setupLink.hidden=!setupUrl;setupLink.href=setupUrl||'';setupLink.textContent=setupAction==='install'?'Install Tailscale':'Continue in Tailscale';
    signIn.hidden=setupAction!=='signin';signIn.disabled=Boolean(pending)||!canConfigure()||setupAction!=='signin';signIn.textContent=pending==='login'?'Starting sign-in…':'Sign in to Tailscale';
    setupHelp.textContent=setupAction==='approve'?'Complete the Tailscale approval in your browser, then refresh status here.':setupAction==='signin'?'Sign in with the same Tailscale account you use on your phone or other device.':'Install Tailscale on this computer and your phone or other device. Sign in with the same Tailscale account on both.';
    const firstSetup=!setup.hidden&&!current.available&&!enabled;
    label.hidden=firstSetup;actions.hidden=firstSetup;advanced.hidden ||= firstSetup;
    address.hidden=!ready;install.hidden=!ready;open.href=ready?url:'';copy.disabled=!ready||Boolean(pending);
    if(!current)showConnectionStatus(state,{label:pending?'Checking private access…':'Private access unavailable',tone:pending?'waiting':'is-disconnected'});
    else if(ready)showConnectionStatus(state,{label:'Private access connected',tone:'online'});
    else if(setupAction==='approve'&&setupUrl)showConnectionStatus(state,{label:'Waiting for Tailscale approval',tone:'waiting'});
    else if(setupAction==='install'&&setupUrl)showConnectionStatus(state,{label:'Set up Tailscale',tone:'is-disconnected'});
    else if(setupAction==='signin')showConnectionStatus(state,{label:'Sign in to enable private access',tone:'is-disconnected'});
    else if(current.error)showConnectionStatus(state,{label:privateAccessError(current.error),tone:'is-disconnected'});
    else if(!current.available)showConnectionStatus(state,{label:'Tailscale is unavailable on this PC.',tone:'is-disconnected'});
    else showConnectionStatus(state,{label:enabled?'Private access is not ready. Refresh status to check it.':'Private access is off',tone:enabled?'waiting':'is-disconnected'});
  };
  const update=value=>{
    if(!active)return;
    if(!value||typeof value.enabled!=='boolean'||typeof value.available!=='boolean'||typeof value.ready!=='boolean')throw new Error('invalid_private_status');
    current=value;if(draft===current.enabled)draft=null;if(portDraft!==null&&Number(portDraft)===currentPort())portDraft=null;render();
  };
  const perform=async (operation,check=false)=>{
    if(!active||pending)return;
    const saving=operation==='apply',login=operation==='login';
    const wanted=draft??current?.enabled;
    const portChange=hasPortChange(),wantedPort=Number(portDraft??currentPort());
    if(saving&&(!canConfigure()||!validPort()||portChange&&!canChangePort()||typeof wanted!=='boolean'||(wanted===current?.enabled&&!portChange&&!(wanted&&canRetry()))||wanted&&!current.available))return;
    if(login&&(!canConfigure()||current?.setup_action!=='signin'))return;
    pending=operation;render();status();
    try{
      const payload={enabled:wanted};if(portChange)payload.https_port=wantedPort;
      const result=saving?await request('/api/private-access','PUT',payload):login?await request('/api/private-access/login','POST'):check&&canConfigure()
        ?await request('/api/private-access/refresh','POST'):await request('/api/private-access');
      if(!active)return;update(result);
      const approval=result.setup_action==='approve'&&privateAccessSetupUrl(result.action_url,'approve')&&['tailscale_signin_required','tailscale_approval_required'].includes(result.error?.code);
      if(approval||!saving&&!setup.hidden&&(result.setup_action==='signin'||result.setup_action==='install'&&privateAccessSetupUrl(result.action_url,'install')))status();
      else if(result.error)status(privateAccessError(result.error),true);
      else if(saving&&(result.enabled!==wanted||portChange&&result.https_port!==wantedPort))status('The setting could not be confirmed. Refresh status before trying again.',true);
      else if(saving)status(wanted?'Private access enabled.':'Private access disabled.');
      else if(login&&result.setup_action==='approve')status('Continue in Tailscale to finish signing in, then refresh status.');
    }catch(error){if(active){status(privateAccessError(error),true);if(!current)showConnectionStatus(state,{label:'Private access unavailable',tone:'is-disconnected'});}}
    finally{pending=null;if(active){render();if(saving)(toggle.disabled?refresh:toggle).focus({preventScroll:true});else if(login)(setupLink.hidden?refresh:setupLink).focus({preventScroll:true});}}
  };
  const start=(operation,check=false)=>{if(pending)return completion;completion=perform(operation,check);return completion;};
  toggle.addEventListener('change',()=>{if(!active||pending||!canConfigure())return;draft=toggle.checked===current.enabled?null:toggle.checked;status();render();});
  port.addEventListener('input',()=>{if(!active||pending||!canChangePort())return;portDraft=port.value===String(currentPort())?null:port.value;status(validPort()?'':'Enter a whole-number port between 1024 and 65535.',!validPort());render();});
  resetPort.addEventListener('click',()=>{if(!active||pending||!canConfigure())return;portDraft=null;status();render();(port.disabled?refresh:port).focus({preventScroll:true});});
  apply.addEventListener('click',()=>{void start('apply');});refresh.addEventListener('click',()=>{void start('refresh',true);});signIn.addEventListener('click',()=>{void start('login');});
  copy.addEventListener('click',async()=>{
    const url=readyUrl();if(!active||pending||!url)return;
    try{
      if(!globalThis.navigator?.clipboard?.writeText)throw new Error('clipboard_unavailable');
      await navigator.clipboard.writeText(url);if(active)status('Private address copied.');
    }catch{if(active){status('Open the private workspace and copy its address from your browser.');open.focus({preventScroll:true});}}
  });
  const cleanup=()=>{active=false;signal?.removeEventListener('abort',cleanup);};
  signal?.addEventListener('abort',cleanup,{once:true});
  cleanup.update=update;cleanup.refresh=()=>start('refresh',true);cleanup.hasChanges=()=>draft!==null||portDraft!==null;
  cleanup.canLeave=async()=>{await completion;return true;};
  render();void start('refresh');
  return cleanup;
}

let sceneChoiceDialog = null;

export function showSceneChoice(request, session, {signal} = {}) {
  if(sceneChoiceDialog){const current=sceneChoiceDialog;current.update(session);return current.promise;}
  if(handoffBlocked(session))return Promise.resolve(session);
  if(!session?.pending_scene_choice)return Promise.resolve(session);
  if(signal?.aborted)return Promise.resolve(null);
  const previousFocus=document.activeElement;
  const hadSession=session.logged_in===true,previousWorkspace=session.workspace_id;
  let current=session,busy=false,disposed=false,expired=false,invalidSaved=false,recoveryRequired=session.error?.code==='output_recovery_required';
  let resolve;
  const promise=new Promise(done=>{resolve=done;});
  const dialog=node('dialog','dialog scene-choice-dialog');
  const heading=node('h2','','Choose lighting for this switch');heading.id=`scene-choice-${crypto.randomUUID()}`;
  dialog.setAttribute('aria-labelledby',heading.id);
  const intro=node('p','help-text','Choose which lighting to start on this switch.');
  const choices=node('div','connection-form');
  const savedSection=node('div','field'),newSection=node('div','field');
  const useSaved=button('Use saved lighting','primary'),startNew=button('Start new setup');
  savedSection.append(useSaved,node('small','muted','Start with the saved colors, effects, and automations.'));
  newSection.append(startNew,node('small','muted','Start with the default lighting for this switch.'));
  choices.append(savedSection,newSection);
  const [message,status]=notice(),footer=node('footer','dialog-footer'),cancel=button('Cancel','quiet');
  cancel.autofocus=true;footer.append(cancel);dialog.append(heading,intro,choices,message,footer);
  const render=()=>{
    const choice=current?.pending_scene_choice;
    savedSection.hidden=choice?.legacy_available!==true;
    useSaved.disabled=busy||expired||invalidSaved||recoveryRequired||!choice?.legacy_available;
    startNew.disabled=busy||expired||recoveryRequired;
    cancel.disabled=busy;cancel.textContent=expired?'Back to connection':'Cancel';
    dialog.setAttribute('aria-busy',String(busy));
  };
  const finish=value=>{
    if(disposed)return;disposed=true;signal?.removeEventListener('abort',abort);
    if(sceneChoiceDialog?.promise===promise)sceneChoiceDialog=null;
    dialog.close();dialog.remove();
    if(previousFocus?.isConnected)previousFocus.focus({preventScroll:true});
    resolve(value);
  };
  const abort=()=>finish(null);
  const update=value=>{
    if(disposed||!value)return;
    const old=current?.pending_scene_choice;current=value;
    if(handoffBlocked(current)){finish(current);return;}
    const next=current.pending_scene_choice;
    if(!next){
      if(current.logged_in&&(!hadSession||current.workspace_id!==previousWorkspace)){finish(current);return;}
      expired=true;status('This connection is no longer waiting for a lighting choice. Return to the connection screen to continue.',true);
    }else if(old?.connection_id!==next.connection_id||old?.legacy_revision!==next.legacy_revision){
      expired=false;invalidSaved=false;recoveryRequired=false;
      status('The connection or saved lighting changed. Review these choices before continuing.');
    }
    if(current.error?.code==='output_recovery_required'){recoveryRequired=true;status(messages.output_recovery_required,true);}
    render();
  };
  const choose=async choice=>{
    if(disposed||busy)return;
    if(expired){if(choice==='cancel')finish(current);return;}
    const pending=current.pending_scene_choice;
    if(!pending)return;
    if(choice==='use_saved'&&(!pending.legacy_available||invalidSaved||recoveryRequired))return;
    if(choice==='start_new'&&recoveryRequired)return;
    const payload={connection_id:pending.connection_id,choice,legacy_revision:pending.legacy_revision};
    busy=true;render();status(choice==='cancel'?'Canceling lighting setup…':'Starting selected lighting…');
    try{
      const result=await request('/api/session/scene-choice','POST',payload);
      if(disposed)return;
      if(current.pending_scene_choice&&current.pending_scene_choice.connection_id!==payload.connection_id){status('The connection changed. Review these choices before continuing.');return;}
      if(result&&!result.pending_scene_choice&&(choice==='cancel'||result.logged_in)){finish(result);return;}
      update(result);status('The lighting choice could not be confirmed. Review the connection and try again.',true);
    }catch(error){
      if(disposed)return;
      if(current.pending_scene_choice&&current.pending_scene_choice.connection_id!==payload.connection_id){status('The connection changed. Review these choices before continuing.');return;}
      const text={
        scene_choice_expired:'This connection expired. Connect again to choose its lighting.',
        scene_choice_stale:'Saved lighting changed. Review the updated choices before continuing.',
        saved_scene_unavailable:'Saved lighting is no longer available. Start a new setup or cancel.',
        invalid_saved_scene:'Saved lighting could not be used. Start a new setup or cancel.',
        output_recovery_required:messages.output_recovery_required,
      };
      const code=typeof error?.code==='string'?error.code:'';
      if(code==='invalid_saved_scene')invalidSaved=true;
      if(code==='output_recovery_required')recoveryRequired=true;
      try{
        const latest=await request('/api/session');
        if(disposed)return;
        if(latest?.logged_in&&!latest.pending_scene_choice&&choice!=='cancel'&&(!hadSession||latest.workspace_id!==previousWorkspace)){finish(latest);return;}
        update(latest);
      }catch{}
      if(disposed)return;
      if(code==='scene_choice_expired'&&(!current.pending_scene_choice||current.pending_scene_choice.connection_id===payload.connection_id))expired=true;
      if(code==='output_recovery_required')recoveryRequired=true;
      status(Object.hasOwn(text,code)?text[code]:'The lighting choice could not be confirmed. Check the connection and try again.',true);
    }finally{
      busy=false;if(!disposed){render();cancel.focus({preventScroll:true});}
    }
  };
  useSaved.addEventListener('click',()=>{void choose('use_saved');});
  startNew.addEventListener('click',()=>{void choose('start_new');});
  cancel.addEventListener('click',()=>{void choose('cancel');});
  dialog.addEventListener('cancel',event=>{event.preventDefault();void choose('cancel');});
  sceneChoiceDialog={promise,update};
  document.body.append(dialog);render();dialog.showModal();cancel.focus();
  if(recoveryRequired)status(messages.output_recovery_required,true);
  signal?.addEventListener('abort',abort,{once:true});
  return promise;
}

export function renderSwitchConnection(host, {request, session = {}, onConnected, onSessionChange, onDemo, restoreSaved, onHelp}) {
  let active = true, pending = null, completed = false, dirty = false, saved = session, forgetting = false;
  let retryingHandoff = false;
  const lifecycle = new AbortController();
  host.replaceChildren();
  const panel = node('section', 'connection-panel panel');
  const logo = node('img', 'connection-logo'); logo.src = '/assets/logo.png'; logo.alt = '';
  const heading=node('h1','','Connect your switch');
  const intro=node('p','help-text','Enter the switch’s SSH login to open its lighting workspace.');
  panel.append(logo,heading,intro);
  const [message, status] = notice();
  const form = node('form', 'connection-form'); form.autocomplete = 'on';
  const address = input('host'); address.required = true; address.maxLength = 253;
  const username = input('username', 'text', 'username'); username.required = true; username.maxLength = 128;
  const password = input('password', 'password', 'current-password'); password.required = true;
  const port = input('port', 'number'); port.value = '22'; port.min = '1'; port.max = '65535'; port.step = '1'; port.required = true;
  const remember = input('remember', 'checkbox');
  const rememberLabel = node('label', 'check'); rememberLabel.append(remember, node('span', '', 'Save login on this PC'));
  const options = node('details', 'connection-options'); options.append(node('summary', '', 'Connection options'), field('SSH port', port));
  const submit = button('Connect to switch', 'primary'); submit.type = 'submit';
  const restore = button('Connect with saved login');
  const forget = button('Forget saved login','quiet');
  const retryHandoff = button('Retry handoff','primary');retryHandoff.hidden=true;
  const actions = node('div', 'connection-actions'); actions.append(submit);
  if (restoreSaved) actions.append(restore);
  actions.append(forget,retryHandoff);
  form.append(field('Switch address', address), field('SSH username', username), passwordField('SSH password', password), rememberLabel, options, actions);
  panel.append(message, form);
  if (onDemo) {
    const demo = button('Continue with demo', 'quiet');
    demo.addEventListener('click', () => { if (!pending && !saved.connecting && !handoffBlocked(saved)) void onDemo(); });
    panel.append(demo);
  }
  if (onHelp) {
    const help = button('Connection help','quiet');
    help.addEventListener('click',()=>{void onHelp();});panel.append(help);
  }
  const privateHost=node('div','connection-private-access');panel.append(privateHost);
  const privateAccess=renderPrivateAccess(privateHost,request,{signal:lifecycle.signal});
  panel.append(node('p', 'connection-footer muted', 'Unofficial community project. Not affiliated with Ubiquiti.'));
  host.append(panel);

  const setBusy = () => {
    if (!active) return;
    const blocked=handoffBlocked(saved);
    const waiting = Boolean(pending || saved.connecting || (saved.pending_scene_choice&&!blocked) || saved.handoff_state==='pending');
    const recovering=blocked&&saved.handoff_state!=='pending';
    form.setAttribute('aria-busy', String(waiting));
    for (const control of form.elements) control.disabled = waiting||recovering;
    retryHandoff.hidden=!handoffRetryable(saved);retryHandoff.disabled=waiting||!handoffRetryable(saved);
    retryHandoff.textContent=retryingHandoff?'Handing off…':'Retry handoff';
    submit.textContent = saved.handoff_state==='pending'||retryingHandoff ? 'Handing off…' : waiting && !forgetting ? 'Connecting…' : 'Connect to switch';
    forget.textContent = forgetting ? 'Removing saved login…' : 'Forget saved login';
    restore.hidden=!restoreSaved || !saved.has_saved_login;
    forget.hidden=!saved.has_saved_login;
    panel.querySelector(':scope > button')?.toggleAttribute('disabled', waiting);
  };
  const enter = async value => {
    if (!active || completed) return;
    const resolved=await showSceneChoice(request,value,{signal:lifecycle.signal});
    if(!active||completed||!resolved)return;
    saved=resolved;
    if(handoffBlocked(resolved)){const handoff=handoffStatus(resolved);status(handoff.message,handoff.error,handoff.tone);setBusy();return;}
    if(!resolved.logged_in){
      if(value?.pending_scene_choice)status('Lighting setup canceled. You can edit this login and connect again.');
      setBusy();submit.focus({preventScroll:true});return;
    }
    completed = true; password.value = ''; dirty = false;
    try { await onConnected(resolved); }
    catch { if (active) { completed = false; status('The switch connected, but the workspace could not load. Try opening it again.', true); } }
  };
  const update = value => {
    if (!active) return;
    saved = value || {};
    if (!dirty && !pending) {
      if (!address.value) address.value = saved.remembered?.host || '';
      if (!username.value) username.value = saved.remembered?.username || '';
      if (saved.remembered?.port) port.value = saved.remembered.port;
      remember.checked = saved.has_saved_login === true;
    }
    const handoff=handoffStatus(saved);
    if(handoffBlocked(saved)&&handoff)status(handoff.message,handoff.error,handoff.tone);
    else if (saved.pending_scene_choice) status('Choose lighting to finish connecting.');
    else if (saved.connecting) status('Connecting with saved login…');
    else if (saved.saved_login_error) status(messages.saved_login_unavailable, true);
    else if (saved.error) status(connectionError(saved.error), true);
    else if(handoff)status(handoff.message,handoff.error,handoff.tone);
    setBusy();
    if(sceneChoiceDialog||(!handoffBlocked(saved)&&(saved.logged_in||saved.pending_scene_choice)))queueMicrotask(() => { void enter(saved); });
  };
  const connect = action => {
    if (!active || pending || saved.connecting || saved.pending_scene_choice || handoffBlocked(saved)) return;
    status('Connecting…');
    pending = (async () => {
      try {
        const result = await action();
        if (!active) return;
        saved = result;
        if (!result?.logged_in&&!result?.pending_scene_choice) throw new Error('Connection was not established.');
        await enter(result);
      } catch (error) { if (active) status(connectionError(error), true); }
      finally { pending = null; setBusy();if(active&&!completed)submit.focus({preventScroll:true}); }
    })();
    setBusy();
  };
  form.addEventListener('input', event => { dirty = true;event?.target?.removeAttribute('aria-invalid'); });
  form.addEventListener('submit', event => {
    event.preventDefault();
    const invalid = [
      [address,!address.value.trim(),'Enter the switch address.'],
      [username,!username.value.trim(),'Enter the SSH username.'],
      [password,password.value.length===0,restoreSaved&&saved.has_saved_login?'Enter the SSH password, or choose Connect with saved login.':'Enter the SSH password.'],
      [port,!Number.isInteger(Number(port.value))||Number(port.value)<1||Number(port.value)>65535,'Enter an SSH port from 1 to 65535.'],
    ].find(([,failed])=>failed);
    if(invalid){invalid[0].setAttribute('aria-invalid','true');status(invalid[2],true);invalid[0].focus();return;}
    if (!form.reportValidity()) return;
    const payload = {host:address.value.trim(),username:username.value.trim(),password:password.value,port:Number(port.value),remember:remember.checked};
    connect(() => request('/api/session/login', 'POST', payload));
  });
  restore.addEventListener('click', () => connect(restoreSaved));
  retryHandoff.addEventListener('click',()=>{
    if(!active||pending||!handoffRetryable(saved))return;
    retryingHandoff=true;
    pending=(async()=>{
      try{
        const title='Retry handoff to UniFi?';
        const action='Retry handoff';
        if(!await confirmAction(host,title,'Stop EE lighting and return control to UniFi. Your saved setup is kept.',action,lifecycle.signal)||!active)return;
        status('Handing lighting back to UniFi…');
        const result=await requestHandoff(request,()=>active);if(!result)return;
        if(result.session){update(result.session);await onSessionChange?.(result.session,result.error?{message:result.error,error:true}:undefined);}
        if(active&&result.error)status(result.error,true);
      }catch{if(active)status('Handoff status could not be displayed. Refresh the page to check its status.',true);}
      finally{pending=null;retryingHandoff=false;setBusy();if(active){const target=handoffRetryable(saved)?retryHandoff:handoffBlocked(saved)?message:restoreSaved&&saved.has_saved_login?restore:submit;if(target===message)message.tabIndex=-1;target.focus({preventScroll:true});}}
    })();setBusy();
  });
  forget.addEventListener('click', async () => {
    if (!active || pending || saved.connecting || saved.pending_scene_choice || handoffBlocked(saved)) return;
    if (!await confirmAction(host,'Forget saved login?','Remove the saved switch login from this PC.','Forget login',lifecycle.signal)) return;
    if (!active || pending || saved.connecting || saved.pending_scene_choice || handoffBlocked(saved)) return;
    forgetting = true;
    pending = (async () => {
      try {
        const result = await request('/api/session/forget','POST');
        if (!active) return;
        saved = result; remember.checked = false;
        status('Saved login removed.');
      } catch (error) { if (active) status(connectionError(error,messages.saved_login_forget_failed),true); }
      finally { pending = null; forgetting = false; setBusy(); if (active) submit.focus({preventScroll:true}); }
    })();
    setBusy();
  });
  update(session);
  const cleanup = () => { active = false; lifecycle.abort(); password.value = ''; };
  cleanup.update = update;
  cleanup.hasChanges = () => dirty || privateAccess.hasChanges();
  cleanup.canLeave = async () => { if (pending) await pending; await privateAccess.canLeave(); return true; };
  return cleanup;
}

export function renderControllerConnection(host, {request, onChanged = () => {}}) {
  let active = true, pending = null, dirty = false, current = {}, certificate = null, challenge = null;
  let editingConnection = false;
  let reconnectDraft = null, savingPreference = false, discovering = false, liveStatusKey, siteContextKey = '', localAddress = '', lastKind = 'self_hosted';
  let consoles = [];
  const methodDrafts = new Map();
  host.replaceChildren();
  const panel = node('section', 'controller-connection connection-card panel');
  const header = node('div', 'connection-heading');
  const title = node('div'), introduction = node('p', 'help-text', 'Connect UniFi Network to show controller details for your switch.'); title.append(node('h2', '', 'UniFi Network'), introduction);
  const reload = button('Refresh controller', 'quiet'); header.append(title, reload);
  const state = node('p', 'connection-state is-disconnected', 'Checking controller connection…');
  state.setAttribute('role', 'status'); state.setAttribute('aria-live', 'polite');
  const details = node('dl', 'settings-details connection-details');
  const capabilityHint = node('p', 'help-text'); capabilityHint.hidden = true;
  const [message, status] = notice();
  const reconnectSection = node('section', 'connection-reconnect automation-section'); reconnectSection.hidden = true;
  const reconnect = input('auto_reconnect', 'checkbox');
  const reconnectLabel = node('label', 'check-row'); reconnectLabel.append(reconnect, node('span', '', 'Reconnect controller automatically'));
  const reconnectHelp = node('p', 'help-text', 'Save this controller login securely on this PC.');
  reconnectHelp.id = `controller-reconnect-help-${crypto.randomUUID()}`; reconnect.setAttribute('aria-describedby', reconnectHelp.id);
  const reconnectHint = node('p', 'help-text');
  const applyPreference = button('Save reconnect preference', 'primary');
  const preferenceActions = node('div', 'connection-actions'); preferenceActions.append(applyPreference);
  const [preferenceNotice, preferenceStatus] = notice();
  reconnectSection.append(reconnectLabel, reconnectHelp, reconnectHint, preferenceActions, preferenceNotice);
  const form = node('form', 'connection-form'); form.autocomplete = 'on';
  form.id = `controller-form-${crypto.randomUUID()}`;
  const editConnection = button('Edit connection'); editConnection.hidden = true;
  editConnection.setAttribute('aria-controls', form.id); editConnection.setAttribute('aria-expanded', 'false');
  const editorHint = node('p', 'help-text', 'Connection edits are not applied.'); editorHint.hidden = true;
  const kind = node('select'); kind.name = 'controller_kind';
  const kindHelp = node('small');
  const kindField = field('Controller type', kind); kindField.append(kindHelp);
  const auth = node('select'); auth.name = 'auth_method';
  const authHelp = node('small'), authField = field('Sign-in method', auth); authField.append(authHelp);
  const address = input('url', 'url'); address.required = true; address.maxLength = 2048; address.placeholder = 'https://controller:8443';
  const addressField = field('Controller address', address);
  const username = input('username', 'text', 'username'); username.maxLength = 128;
  const password = input('password', 'password', 'current-password');
  const usernameField = field('Username', username), passwordRow = passwordField('Controller password', password);
  const apiKey = input('api_key', 'password'); apiKey.maxLength = 4096;
  const apiKeyField = passwordField('API key', apiKey), keyHelp = node('small'); apiKeyField.append(keyHelp);
  const consoleId = node('select'); consoleId.name = 'console_id';
  consoleId.id = `controller-console-${crypto.randomUUID()}`;
  const consoleField = node('div', 'field'), consoleLabel = node('label', '', 'Console'); consoleLabel.htmlFor = consoleId.id;consoleField.append(consoleLabel,consoleId);
  const findConsoles = button('Find consoles', 'quiet'), consoleHelp = node('small', '', 'Enter an API key, then find and choose your console.');
  consoleField.append(findConsoles, consoleHelp);
  const verification = input('verification_code', 'text', 'one-time-code');
  verification.inputMode = 'numeric'; verification.pattern = '[0-9]{6,8}'; verification.maxLength = 8;
  const twoFactor = node('details', 'connection-options');
  twoFactor.append(node('summary', '', 'Two-factor authentication'), field('Verification code', verification, 'Enter your account’s current verification code.'));
  const site = node('select'); site.name = 'site'; site.append(new Option('Automatic', ''));
  const siteField = field('UniFi site', site); siteField.hidden = true;
  const submit = button('Connect controller', 'primary'); submit.type = 'submit';
  const disconnect = button('Disconnect controller', 'quiet'); disconnect.hidden = true;
  const cancelChange = button('Cancel connection change', 'quiet'); cancelChange.hidden = true;
  const actions = node('div', 'connection-actions'); actions.append(submit, cancelChange);
  const connectionActions = node('div', 'connection-actions'); connectionActions.append(editConnection, disconnect);
  const trustPanel = node('div', 'connection-trust notice'); trustPanel.hidden = true;
  const fingerprint = node('code', 'certificate-fingerprint');
  const trust = button('Trust this certificate and connect');
  trustPanel.append(node('p', '', 'Verify this SHA-256 fingerprint against your controller, then trust it to continue.'), fingerprint, trust);
  const setupHelp = node('details', 'connection-options');
  const addressHelp = node('p', 'help-text'), credentialHelp = node('p', 'help-text');
  setupHelp.append(node('summary', '', 'Where do I find these details?'), addressHelp, credentialHelp);
  form.append(kindField, authField, addressField, usernameField, passwordRow, apiKeyField, consoleField, setupHelp, twoFactor, siteField, trustPanel, actions);
  panel.append(header, state, details, capabilityHint, message, reconnectSection, connectionActions, editorHint, form); host.append(panel);
  const renderChoices = (selectedKind, selectedMethod, useDefault = false) => {
    const supported = controllerSupportedMethods(current);
    kind.replaceChildren();
    for (const [value,label] of [['self_hosted','Self-hosted Network'],['unifi_os','UniFi Console or OS Server'],['cloud_connector','UniFi cloud connector']])
      if (supported[value].length) kind.append(new Option(label,value));
    kind.value = supported[selectedKind]?.length ? selectedKind : '';
    auth.replaceChildren();
    for (const value of supported[kind.value] || []) auth.append(new Option(value === 'api_key' ? 'API key' : 'Controller account',value));
    auth.value = supported[kind.value]?.includes(selectedMethod) ? selectedMethod : useDefault ? supported[kind.value]?.[0] || '' : '';
    authField.hidden = !Object.values(supported).some(methods => methods.includes('api_key'));
  };
  renderChoices('self_hosted','password');
  const supportsVerification = () => kind.value === 'self_hosted' && auth.value === 'password';
  const showKind = () => {
    const selfHosted = kind.value === 'self_hosted', cloud = kind.value === 'cloud_connector', key = auth.value === 'api_key';
    kindHelp.textContent = cloud ? 'Find a console through UniFi Site Manager.' : selfHosted ? 'Standalone UniFi Network installed on a PC or server. HTTPS usually uses port 8443.' : 'A Cloud Gateway, Cloud Key running UniFi OS, or UniFi OS Server. HTTPS usually uses port 443.';
    address.placeholder = selfHosted ? 'https://controller:8443' : 'https://console';
    address.readOnly = cloud;
    usernameField.hidden = key; passwordRow.hidden = key; apiKeyField.hidden = !key; twoFactor.hidden = !supportsVerification(); consoleField.hidden = !cloud;
    if(!supportsVerification()){twoFactor.open = false;verification.required = false;}
    authHelp.textContent = key ? 'Read switch and port details with an API key.' : 'Show device names and port details with a local UniFi account.';
    keyHelp.textContent = cloud ? 'Create a key in Site Manager → Settings → API Keys.' : 'Create a key in UniFi Network → Control Plane → Integrations.';
    addressHelp.textContent = cloud ? 'The cloud connector uses UniFi’s fixed HTTPS address.' : 'Use the direct HTTPS address you open for UniFi on your network. Enter only the address and port, without a page path.';
    credentialHelp.textContent = key ? keyHelp.textContent : 'Use a local UniFi account with Network access to the site that contains your switch. You can create a separate local account in UniFi’s administrator settings.';
  };
  const showConsoles = (selected = consoleId.value || '') => {
    consoleId.replaceChildren(new Option('Choose a console',''));
    if(selected&&!consoles.some(item=>item.id===selected))consoleId.append(new Option('Selected console',selected));
    for(const item of consoles){const option = new Option(item.name,item.id);option.disabled = item.blocked === true;consoleId.append(option);}
    consoleId.value = consoles.find(item=>item.id===selected)?.blocked ? '' : selected;
  };
  showConsoles();
  showKind();
  const formValues = () => JSON.stringify([kind.value,auth.value,address.value,username.value,password.value,apiKey.value,consoleId.value,verification.value,site.value]);
  let cleanForm = formValues();
  const formChanged = () => formValues() !== cleanForm;
  const formTarget = () => ({url:address.value,controller_kind:kind.value,auth_method:auth.value,console_id:kind.value === 'cloud_connector' ? consoleId.value.trim() : null});
  const matchesForm = target => sameControllerTarget(target,formTarget());
  const syncTarget = () => {
    if (dirty && formChanged()) return;
    dirty = false;
    const target = current.pending_site_choice || current;
    if (target.url) address.value = target.url;
    renderChoices(target.controller_kind || 'self_hosted',target.auth_method || 'password');
    showConsoles(target.console_id || ''); lastKind = kind.value;
    if (kind.value !== 'cloud_connector') localAddress = address.value;
    showKind();
  };
  const showSites = () => {
    const candidate = current.pending_site_choice;
    const target = candidate && matchesForm(candidate) ? candidate : matchesForm(current) ? current : null;
    const isCandidate = Boolean(target && target === candidate);
    const sites = target?.sites || [];
    const key = target ? `${isCandidate ? 'pending' : 'active'}|${controllerOrigin(target.url)}|${target.controller_kind || 'self_hosted'}|${target.auth_method || 'password'}|${target.console_id || ''}` : '';
    const selected = key === siteContextKey ? site.value : isCandidate ? '' : target?.site || '';
    siteContextKey = key;
    site.replaceChildren(new Option(isCandidate || sites.length > 1 ? 'Choose a site' : 'Automatic', ''));
    for (const item of sites) site.append(new Option(item.name || item.id, item.id));
    site.value = sites.some(item => item.id === selected) ? selected : '';
    site.required = isCandidate || Boolean(target && current.error?.code === 'site_required');
    siteField.hidden = !sites.length;
    cancelChange.hidden = !candidate;
  };
  const pendingSiteMessage = () => status('Choose the site that contains your switch to finish this connection change.');

  const setBusy = () => {
    if (!active) return;
    form.setAttribute('aria-busy', String(Boolean(pending)));
    for (const control of form.elements) control.disabled = Boolean(pending);
    auth.disabled = Boolean(pending) || (controllerSupportedMethods(current)[kind.value]?.length || 0) < 2;
    username.disabled = password.disabled = Boolean(pending) || auth.value === 'api_key';
    apiKey.disabled = Boolean(pending) || auth.value !== 'api_key';
    verification.disabled = Boolean(pending) || !supportsVerification();
    consoleId.disabled = Boolean(pending) || kind.value !== 'cloud_connector';
    findConsoles.hidden = current.console_discovery_supported !== true;
    findConsoles.disabled = Boolean(pending) || kind.value !== 'cloud_connector' || auth.value !== 'api_key' || current.console_discovery_supported !== true || !/^[\x21-\x7e]{1,4096}$/.test(apiKey.value);
    findConsoles.textContent = discovering ? 'Finding consoles…' : consoles.length ? 'Refresh consoles' : 'Find consoles';
    reload.disabled = Boolean(pending);
    disconnect.disabled = Boolean(pending);
    const editorRequired = !current.connected || Boolean(current.pending_site_choice) || Boolean(challenge);
    const editorOpen = editorRequired || editingConnection;
    form.hidden = !editorOpen;
    editConnection.hidden = !current.connected || editorRequired;
    editConnection.disabled = Boolean(pending);
    editConnection.textContent = editorOpen ? 'Hide connection details' : 'Edit connection';
    editConnection.setAttribute('aria-expanded', String(editorOpen));
    editorHint.hidden = editorOpen || !formChanged();
    submit.textContent = pending && !savingPreference && !discovering ? 'Connecting…' : current.pending_site_choice && matchesForm(current.pending_site_choice) ? 'Connect selected site' : current.connected ? 'Update connection' : 'Connect controller';
    const preference = controllerReconnectControls(current, reconnectDraft);
    reconnectSection.hidden = !preference.supported;
    reconnect.checked = preference.checked;
    reconnect.disabled = Boolean(pending);
    applyPreference.disabled = Boolean(pending) || !preference.canApply;
    applyPreference.textContent = savingPreference ? 'Saving…' : 'Save reconnect preference';
    reconnectHelp.textContent = preference.changed && !preference.checked
      ? `Saving this preference removes the saved controller login.${current.connected ? ' The current connection stays active.' : ''}`
      : preference.checked ? 'Save this controller login securely on this PC and reconnect when EE opens.'
      : 'Enable this to save the controller login and reconnect when EE opens.';
    reconnectHint.textContent = preference.changed && preference.checked && !current.can_save_login ? 'Connect your controller to save this login.' : preference.checked && current.auto_reconnect && current.can_save_login && current.saved_login_matches_connection === false ? 'Save reconnect preference to save the current controller login.' : preference.checked && current.auto_reconnect && current.reconnect_state === 'paused' ? 'Reconnect will resume when EE opens again.' : '';
    reconnectHint.hidden = !reconnectHint.textContent;
  };
  const showDetails = value => {
    details.replaceChildren();
    introduction.textContent = value.connected ? 'Controller details for this switch.' : 'Connect UniFi Network to show controller details for your switch.';
    const siteName = value.sites?.find(item => item.id === value.site)?.name || value.site;
    const facts = [];
    if (siteName) facts.push(['Site', siteName]);
    if (value.switch_name) facts.push(['Switch', value.switch_name]);
    if (value.connected) facts.push(['Ports identified', String(value.mapped_ports ?? 0)]);
    for (const [label, value] of facts) details.append(node('dt', 'muted', label), node('dd', '', String(value)));
    disconnect.hidden = !value.configured;
    capabilityHint.textContent = 'Connect with a controller account to show device names.';
    capabilityHint.hidden = !(value.connected && value.auth_method === 'api_key' && value.capabilities?.client_mapping === false);
  };
  const show = value => {
    if (!active) return;
    current = value || {};
    showConnectionStatus(state, controllerConnectionStatus(current));
    showDetails(current);
    syncTarget(); showSites();
    if (!dirty) cleanForm = formValues();
    if (current.error) status(controllerError(current.error, 'Controller data is unavailable. Check the connection and try again.'), true);
    else if (current.pending_site_choice) pendingSiteMessage();
    if (current.saved_login_error) preferenceStatus(controllerError(current.saved_login_error, 'The saved controller login needs attention. Connect your controller again.'), true);
    else preferenceStatus();
    if (supportsVerification() && ['two_factor_required','verification_failed'].includes(current.error?.code)) { editingConnection = true; twoFactor.open = true; verification.required = true; }
    setBusy();
  };
  const perform = (action, label, actionKind = 'status') => {
    if (!active || pending) return;
    let focusVerification = false, focusSite = false, focusSummary = false;
    const canceledTarget = actionKind === 'cancel' ? current.pending_site_choice : null;
    const resetCanceledForm = Boolean(canceledTarget && matchesForm(canceledTarget));
    status(label);
    pending = (async () => {
      try {
        const result = await action();
        if (!active) return;
        show(result);
        focusVerification = supportsVerification() && ['two_factor_required','verification_failed'].includes(result.error?.code);
        focusSite = Boolean(result.pending_site_choice && matchesForm(result.pending_site_choice)) || result.error?.code === 'site_required';
        const canceled = actionKind === 'cancel' && !result.pending_site_choice;
        if (!result.error && !result.pending_site_choice) status(actionKind === 'status' ? '' : canceled ? 'Connection change canceled.' : result.connected ? 'Controller connected.' : actionKind === 'disconnect' ? 'Controller disconnected.' : 'Controller not connected.', false, controllerConnectionStatus(result).tone);
        const connected = actionKind === 'connect' && result.connected && !result.error && !result.pending_site_choice && matchesForm(result);
        const disconnected = actionKind === 'disconnect' && !result.configured && !result.error;
        if (connected || disconnected) { dirty = false; password.value = ''; apiKey.value = ''; verification.value = ''; verification.required = false; twoFactor.open = false; challenge = null; trustPanel.hidden = true; }
        if (connected) { username.value = ''; editingConnection = false; focusSummary = true; }
        if (canceled && resetCanceledForm) {
          dirty = false; username.value = ''; password.value = ''; apiKey.value = ''; verification.value = ''; verification.required = false; twoFactor.open = false;
          certificate = null; challenge = null; trustPanel.hidden = true; address.value = result.url || ''; syncTarget(); showSites();
          editingConnection = false; focusSummary = true;
        }
        if (disconnected) { certificate = null; editingConnection = false; }
        if (!dirty) cleanForm = formValues();
        await onChanged(result);
      } catch (error) {
        if (!active) return;
        const detail = error.details || error.detail || {};
        const value = detail.certificate_sha256;
        if (['two_factor_required','verification_failed'].includes(error.code)) {
          if(supportsVerification()){
            editingConnection = true; twoFactor.open = true; verification.required = true; focusVerification = true;
            status(controllerError(error), true);
          }else status(controllerError({code:'controller_local_account_required'}), true);
        } else if (['certificate_untrusted', 'certificate_changed'].includes(error.code) && kind.value === 'cloud_connector') {
          challenge = null; certificate = null; trustPanel.hidden = true;
          status('The UniFi cloud certificate could not be verified. Check this computer’s date and trusted certificates, then try again.', true);
        } else if (['certificate_untrusted', 'certificate_changed'].includes(error.code) && /^[a-f\d]{64}$/i.test(value || '')) {
          challenge = {fingerprint:value, ...formTarget()};
          fingerprint.textContent = value.toUpperCase().match(/.{1,4}/g).join(' ');
          trustPanel.hidden = false;
          status(error.code === 'certificate_changed' ? 'The controller certificate changed. Verify the new fingerprint before connecting.' : 'The controller uses a certificate that needs your approval.', true);
        } else status(controllerError(error), true);
      } finally { pending = null; setBusy(); if (active && focusVerification) verification.focus(); else if (active && focusSite) site.focus(); else if (active && focusSummary && !editConnection.hidden) editConnection.focus(); else if (active && actionKind === 'cancel') submit.focus(); }
    })();
    setBusy();
  };
  const connect = () => {
    if (!active || pending) return;
    const target = formTarget();
    const problem = controllerConnectionProblem({...target,username:username.value,password:password.value,api_key:apiKey.value}, current);
    if (problem) { status(problem.message, true); ({url:address,username,password,controller_kind:kind,auth_method:auth,api_key:apiKey,console_id:consoleId})[problem.field].focus(); return; }
    if(target.controller_kind==='cloud_connector'&&consoles.some(item=>item.id===target.console_id&&item.blocked)){status('Choose an available console.',true);consoleId.focus();return;}
    if (site.required && !site.value) { status(controllerError({code:'site_required'}), true); site.focus(); return; }
    if (!form.reportValidity()) return;
    const payload = {url:target.url.trim(),controller_kind:target.controller_kind,site:site.value || null};
    if(current.supported_auth_methods !== undefined)payload.auth_method = target.auth_method;
    if(target.controller_kind === 'cloud_connector')payload.console_id = target.console_id;
    else payload.certificate_sha256 = certificate;
    if(target.auth_method === 'api_key')payload.api_key = apiKey.value;
    else{
      Object.assign(payload,{username:username.value.trim(),password:password.value});
      if(supportsVerification())payload.verification_code = verification.value.trim();
    }
    verification.value = '';
    perform(() => request('/api/controller/connect', 'POST', payload), 'Connecting to UniFi Network…', 'connect');
  };
  form.addEventListener('input', () => { dirty = true; });
  const resetControllerChallenge = () => { certificate = null; challenge = null; trustPanel.hidden = true; siteContextKey = ''; siteField.hidden = true; site.required = false; site.replaceChildren(new Option('Automatic', '')); site.value = ''; verification.value = ''; verification.required = false; twoFactor.open = false; };
  address.addEventListener('input', () => { resetControllerChallenge(); showSites(); setBusy(); });
  kind.addEventListener('change', () => {
    const chosen = kind.value;
    methodDrafts.set(lastKind,auth.value);
    if(chosen === 'cloud_connector' && lastKind !== chosen){localAddress = address.value;address.value = 'https://api.ui.com';}
    else if(lastKind === 'cloud_connector' && chosen !== lastKind)address.value = localAddress;
    dirty = true; renderChoices(chosen,methodDrafts.get(chosen) || 'password',true);lastKind = chosen;
    resetControllerChallenge(); showKind(); showSites(); setBusy();
  });
  auth.addEventListener('change', () => { dirty = true;methodDrafts.set(kind.value,auth.value);resetControllerChallenge();showKind();showSites();setBusy(); });
  consoleId.addEventListener('change', () => { dirty = true;resetControllerChallenge();showSites();setBusy(); });
  apiKey.addEventListener('input', () => { dirty = true;consoles = [];showConsoles('');resetControllerChallenge();showSites();setBusy(); });
  findConsoles.addEventListener('click', () => {
    if(!active||pending||kind.value!=='cloud_connector'||auth.value!=='api_key'||current.console_discovery_supported!==true)return;
    if(!/^[\x21-\x7e]{1,4096}$/.test(apiKey.value)){status(controllerMessages.api_key_required,true);apiKey.focus();return;}
    discovering = true;status('Finding consoles…');
    pending = (async () => {
      try{
        const result = await request('/api/controller/consoles','POST',{api_key:apiKey.value});
        if(!active)return;
        if(!Array.isArray(result?.consoles)||result.consoles.length>1000||result.consoles.some(item=>!item||!/^[A-Za-z0-9_-]{1,128}(?::[0-9]{1,20})?$/.test(item.id)||typeof item.name!=='string'||!item.name.trim()||item.name.length>512||typeof item.blocked!=='boolean'))throw {code:'api_response_incomplete'};
        consoles = result.consoles;showConsoles();
        if(!consoles.length)status(controllerMessages.no_consoles,true);
        else if(consoles.every(item=>item.blocked))status('These consoles are blocked in UniFi. Check console access in Site Manager.',true);
        else status('Choose the console to connect.');
      }catch(error){if(active)status(controllerError(error),true);}
      finally{pending = null;discovering = false;setBusy();if(active)consoleId.focus();}
    })();setBusy();
  });
  username.addEventListener('input', () => { verification.value = ''; verification.required = false; });
  form.addEventListener('submit', event => { event.preventDefault(); connect(); });
  trust.addEventListener('click', () => {
    if (!challenge || kind.value === 'cloud_connector' || !sameControllerTarget(challenge,formTarget())) return;
    certificate = challenge.fingerprint; challenge = null; trustPanel.hidden = true; connect();
  });
  reload.addEventListener('click', () => perform(() => request('/api/controller'), 'Checking controller status…'));
  editConnection.addEventListener('click', () => {
    if (!active || pending) return;
    editingConnection = !editingConnection; setBusy();
    if (editingConnection) kind.focus(); else editConnection.focus();
  });
  cancelChange.addEventListener('click', () => { if(current.pending_site_choice)perform(() => request('/api/controller/cancel', 'POST'), 'Canceling connection change…', 'cancel'); });
  disconnect.addEventListener('click', () => perform(() => request('/api/controller/disconnect', 'POST'), 'Disconnecting controller…', 'disconnect'));
  reconnect.addEventListener('change', () => {
    reconnectDraft = reconnect.checked;
    preferenceStatus(); setBusy();
  });
  applyPreference.addEventListener('click', () => {
    const preference = controllerReconnectControls(current, reconnectDraft);
    if (!active || pending || !preference.canApply) return;
    const enabled = preference.checked;
    savingPreference = true; preferenceStatus('Saving controller preference…');
    pending = (async () => {
      try {
        const result = await request('/api/controller/preferences', 'PUT', {auto_reconnect:enabled});
        if (!active) return;
        show(result);
        if (!controllerReconnectConfirmed(result, enabled)) {
          preferenceStatus(controllerError(result.saved_login_error, 'The setting could not be confirmed. Select Refresh controller before trying again.'), true);
          return;
        }
        reconnectDraft = null;
        preferenceStatus(enabled ? 'Automatic reconnect enabled. Controller login saved.' : 'Automatic reconnect disabled. Saved controller login removed.');
        try { await onChanged(result); }
        catch { if (active) status('The setting was saved. Select Refresh controller to update its details.', true); }
      } catch (error) {
        if (active) preferenceStatus(controllerError(error, 'The controller setting could not be saved. Try Save reconnect preference again.'), true);
      } finally { pending = null; savingPreference = false; setBusy(); }
    })();
    setBusy();
  });
  perform(() => request('/api/controller'), 'Checking controller status…');
  const cleanup = () => { active = false; password.value = ''; apiKey.value = ''; verification.value = ''; certificate = null; challenge = null; };
  cleanup.update = (value, unavailable = false) => {
    if (!active || pending) return;
    const presentation = controllerConnectionStatus(value, unavailable);
    const key = JSON.stringify([presentation, value?.configured, value?.controller_kind, value?.auth_method, value?.console_id, value?.supported_auth_methods, value?.capabilities, value?.pending_site_choice, value?.site, value?.sites, value?.switch_name, value?.mapped_ports, value?.error?.code, value?.error?.operation, value?.error?.upstream_status, value?.preferences_supported, value?.auto_reconnect, value?.can_save_login, value?.saved_login_available, value?.saved_login_matches_connection, value?.reconnect_state]);
    if (key === liveStatusKey) return;
    liveStatusKey = key;
    showConnectionStatus(state, presentation);
    if (unavailable) { status(); return; }
    current = value || {};
    showDetails(current); syncTarget(); showSites();
    if (!dirty) cleanForm = formValues();
    if (current.error) status(controllerError(current.error), true);
    else if (current.pending_site_choice) pendingSiteMessage();
    else if (!current.connected || current.stale || message.classList.contains('error')) status();
    setBusy();
  };
  cleanup.hasChanges = () => formChanged() || controllerReconnectControls(current, reconnectDraft).changed;
  cleanup.canLeave = async () => { if (pending) await pending; return true; };
  return cleanup;
}
