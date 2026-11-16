export async function request(path, method = 'GET', body, {workspace} = {}) {
  if (!path.startsWith('/api/')) throw new Error('Invalid API route.');
  const writing=!['GET','HEAD'].includes(method.toUpperCase());
  const headers=body === undefined ? {} : {'Content-Type': 'application/json'};
  if(workspace)headers['X-EE-Workspace']=workspace;
  let response,result;
  try {
    response = await fetch(path, {method,headers,
      body: body === undefined ? undefined : JSON.stringify(body),
      signal: AbortSignal.timeout(30000)});
  } catch {
    const error=new Error(writing?'The connection was interrupted before this change could be confirmed. Refresh the current setup before retrying.':'The workspace could not be reached. Check its connection and try again.');
    error.code='connection_unavailable';error.uncertain=writing;throw error;
  }
  if(response.ok&&[204,205].includes(response.status))return null;
  try { result=await response.json(); }
  catch {
    const error=new Error(response.ok&&writing?'The response could not be read. Refresh the current setup before retrying this change.':'The workspace returned an unreadable response. Refresh and try again.');
    error.code='invalid_response';error.status=response.status;error.uncertain=writing;throw error;
  }
  if (!response.ok) {
    const detail = result?.detail;
    const error = new Error(typeof detail === 'string' ? detail : detail?.message || 'The change could not be confirmed. Try again.');
    error.code = detail?.code;
    error.status = response.status;
    error.uncertain = writing && response.status >= 500;
    error.details = typeof detail === 'object' ? detail : {};
    // A LAN device whose pairing was removed gets this from every request, so the workspace shows the pairing screen.
    if (error.code === 'lan_pairing_required') globalThis.dispatchEvent(new Event('ee-lan-pairing-required'));
    throw error;
  }
  return result;
}
