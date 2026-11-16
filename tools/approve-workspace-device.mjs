import {createHash, randomUUID} from 'node:crypto';
import {lstat, open, readFile, rename, unlink} from 'node:fs/promises';
import {createServer} from 'node:net';
import {tmpdir} from 'node:os';
import path from 'node:path';
import {setTimeout as delay} from 'node:timers/promises';
import {pathToFileURL} from 'node:url';
import {canonicalTailnetAddress, matchesApprovedNode, MAX_APPROVED_DEVICES, parseApprovedDevices} from '../preview/approved-device.mjs';
import {createTailscaleRunner} from '../preview/tailscale-cli.mjs';

class ApprovalError extends Error {}
function fail(message) { throw new ApprovalError(message); }

// Full status lists every peer, which outgrows the gateway's 64 KB output limit on a tailnet of a few dozen devices.
const runTailscale = createTailscaleRunner({maxBuffer: 4 * 1024 * 1024});

async function readStatus(run) {
  try { return JSON.parse(await run(['status', '--json'])); }
  catch (error) {
    if (error.code === 'tailscale_output_too_large') fail('Tailscale returned more device data than EE can read.');
    throw error;
  }
}

function dnsName(input) {
  if (typeof input !== 'string' || input.length > 254) return '';
  const name = input.toLowerCase().replace(/\.$/, '');
  return name.split('.').every(label => /^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$/.test(label))
    && name.endsWith('.ts.net') ? name : '';
}

async function readRecord(file) {
  let stat;
  try { stat = await lstat(file); }
  catch (error) { if (error.code === 'ENOENT') return null; throw error; }
  if (!stat.isFile() || stat.size > 8192) fail('EE could not verify its local settings file.');
  return JSON.parse(await readFile(file, 'utf8'));
}

const sha256 = value => createHash('sha256').update(value).digest('hex');

function approvedHost(status, privateAccess) {
  const host = status?.Self;
  const owner = status?.User?.[host?.UserID];
  const login = owner?.LoginName;
  const httpsPort = privateAccess?.version === 1 ? 8444 : privateAccess?.httpsPort;
  if (status?.BackendState !== 'Running' || typeof host?.ID !== 'string' || !host.ID || host.Online !== true || host.Expired === true
      || !Number.isSafeInteger(host.UserID) || host.UserID <= 0 || owner?.ID !== host.UserID
      || (host.Tags !== undefined && (!Array.isArray(host.Tags) || host.Tags.length > 0))
      || typeof login !== 'string' || !login || !dnsName(host.DNSName)
      || privateAccess?.enabled !== true || ![1, 2].includes(privateAccess.version)
      || !Number.isInteger(httpsPort) || httpsPort < 1024 || httpsPort > 65535
      || privateAccess.origin !== 'https://' + dnsName(host.DNSName) + ':' + httpsPort
      || privateAccess.nodeHash !== sha256(host.ID) || privateAccess.ownerHash !== sha256(login)) {
    fail('Enable private access on this Windows workspace and confirm its Tailscale account before approving a device.');
  }
  return host.ID;
}

function selectPeer(status, request) {
  const requestedName = dnsName(request?.dnsName);
  if (!requestedName) fail('Enter the device’s full Tailscale DNS name.');
  if (status?.BackendState !== 'Running') fail('Connect Tailscale on this computer and try again.');
  const peers = Object.values(status.Peer ?? {}).filter(peer => dnsName(peer?.DNSName) === requestedName);
  if (peers.length !== 1) fail('The host must see exactly one peer with that full DNS name.');
  const peer = peers[0];
  if (typeof peer.ID !== 'string' || !/^[!-~]{1,256}$/.test(peer.ID)
      || (request.deviceStableID !== undefined && request.deviceStableID !== peer.ID)) {
    fail('Tailscale did not confirm the requested device identity.');
  }
  return peer;
}

async function makeApproval(directory, request, run) {
  const status = await readStatus(run);
  const hostStableID = approvedHost(status, await readRecord(path.join(directory, 'private-access.json')));
  const peer = selectPeer(status, request);
  if (peer.ID === hostStableID || peer.Expired === true || peer.InNetworkMap === false
      || (peer.Online !== true && !(peer.Online === false && peer.InNetworkMap === true))
      || !Array.isArray(peer.Tags) || !peer.Tags.length) {
    fail('Select a tagged Tailscale device currently registered with this host.');
  }
  const addresses = [...new Set((peer.TailscaleIPs ?? []).map(canonicalTailnetAddress))];
  if (!addresses.length || addresses.includes(null)) fail('The device has no verified Tailscale addresses.');
  for (const address of addresses) {
    const whois = JSON.parse(await run(['whois', '--json', address]));
    if (!matchesApprovedNode(whois?.Node, peer.ID, address)) {
      fail('Tailscale could not verify this device at each of its assigned addresses.');
    }
  }
  return {hostStableID, device: {deviceStableID: peer.ID, addresses: addresses.sort()}};
}

// The OS releases a Windows named pipe if the command exits during an update.
async function lockApprovals(directory) {
  const key = sha256(process.platform === 'win32' ? path.resolve(directory).toLowerCase() : path.resolve(directory)).slice(0, 32);
  const name = 'ee-device-approval-' + key;
  const endpoint = process.platform === 'win32' ? '\\\\.\\pipe\\' + name : path.join(tmpdir(), name + '.sock');
  const until = Date.now() + 3000;
  for (;;) {
    const server = createServer(socket => socket.destroy());
    try {
      await new Promise((resolve, reject) => {
        server.once('error', reject);
        server.listen(endpoint, resolve);
      });
      return () => new Promise(resolve => server.close(resolve));
    } catch (error) {
      if (error.code !== 'EADDRINUSE') throw error;
      if (Date.now() >= until) fail('Another device approval is in progress. Try again.');
      await delay(25);
    }
  }
}

async function saveDevices(directory, file, hostStableID, devices) {
  if (!devices.length) {
    await unlink(file);
    return;
  }
  const record = {version: 2, hostStableID, devices};
  if (!parseApprovedDevices(record, hostStableID)) fail('The device identity or addresses conflict with a saved approval.');
  const contents = JSON.stringify(record) + '\n';
  if (Buffer.byteLength(contents) > 4096) fail('The saved device list is full. Revoke an unused approval and try again.');
  const temporary = path.join(directory, '.ee-approval-' + randomUUID() + '.tmp');
  const out = await open(temporary, 'wx', 0o600);
  try {
    try { await out.writeFile(contents); await out.sync(); }
    finally { await out.close(); }
    await rename(temporary, file);
  } catch (error) { await unlink(temporary); throw error; }
}

export async function manageApproval(action, directory, request, run = runTailscale) {
  if (!['approve', 'revoke', 'status'].includes(action)) fail('Choose approve, status, or revoke.');
  const file = path.join(directory, 'approved-device.json');
  const approval = action === 'approve' ? await makeApproval(directory, request, run) : null;
  let selected = approval?.device.deviceStableID;
  if (!approval && request && !request.all) {
    selected = request.deviceStableID ?? selectPeer(await readStatus(run), request).ID;
    if (typeof selected !== 'string' || !/^[!-~]{1,256}$/.test(selected)) fail('Enter a valid device identity.');
  }
  if (action === 'revoke' && !selected && !request?.all) fail('Select the device to revoke, or explicitly choose --all.');
  const release = action === 'status' ? null : await lockApprovals(directory);
  try {
    if (action === 'revoke' && request?.all) {
      try { await unlink(file); } catch (error) { if (error.code !== 'ENOENT') throw error; }
      return 'All device approvals removed.';
    }
    const existing = await readRecord(file);
    const hostStableID = approval?.hostStableID ?? existing?.hostStableID;
    const devices = existing ? parseApprovedDevices(existing, hostStableID) : [];
    if (!devices) fail('EE could not verify its saved device approvals. Use revoke --all to clear them.');
    const index = devices.findIndex(device => device.deviceStableID === selected);
    if (action === 'status') {
      if (selected) return index >= 0 ? 'This device is approved.' : 'This device has no saved approval.';
      return devices.length ? `Approved devices: ${devices.length}.` : 'No device approval is saved.';
    }
    if (action === 'revoke') {
      if (index < 0) return 'This device has no saved approval.';
      devices.splice(index, 1);
      await saveDevices(directory, file, hostStableID, devices);
      return 'Device approval removed.';
    }
    if (index >= 0) {
      if (JSON.stringify([...devices[index].addresses].sort()) !== JSON.stringify(approval.device.addresses)) {
        fail('The approved device addresses changed. Revoke this device before approving it again.');
      }
      return 'This device is already approved.';
    }
    if (devices.length >= MAX_APPROVED_DEVICES) fail('The saved device list is full. Revoke an unused approval and try again.');
    await saveDevices(directory, file, hostStableID, [...devices, approval.device]);
    return 'Device approved for this EE workspace.';
  } finally { if (release) await release(); }
}

async function readSelection() {
  let input = '';
  for await (const chunk of process.stdin.setEncoding('utf8')) {
    input += chunk;
    if (input.length > 4096) fail('Device selection is too long.');
  }
  const selection = JSON.parse(input);
  if (typeof selection?.deviceStableID !== 'string' || !selection.deviceStableID) {
    fail('Piped selection must include dnsName and deviceStableID.');
  }
  return {dnsName: selection.dnsName, deviceStableID: selection.deviceStableID};
}

if (process.argv[1] && pathToFileURL(path.resolve(process.argv[1])).href === import.meta.url) {
  try {
    const [action, selection, ...rest] = process.argv.slice(2);
    if (process.platform !== 'win32' || !path.isAbsolute(process.env.LOCALAPPDATA ?? '')) {
      fail('Run this command as the user hosting the Windows EE workspace.');
    }
    if (rest.length || !['approve', 'revoke', 'status'].includes(action) || (action !== 'status' && !selection)
        || (selection === '--all' && action !== 'revoke')) {
      fail('Choose approve DEVICE, status [DEVICE], revoke DEVICE, or revoke --all. Use --stdin for a private selection.');
    }
    const request = selection === '--stdin' ? await readSelection()
      : selection === '--all' ? {all: true} : selection ? {dnsName: selection} : null;
    const directory = path.join(process.env.LOCALAPPDATA, 'Etherlighting Evolved', 'workspace');
    console.log(await manageApproval(action, directory, request));
  } catch (error) {
    console.error(error instanceof ApprovalError ? error.message : 'EE could not complete the local device approval command.');
    process.exitCode = 1;
  }
}
