import assert from 'node:assert/strict';
import {createHash} from 'node:crypto';
import {mkdir, mkdtemp, readFile, rm, writeFile} from 'node:fs/promises';
import path from 'node:path';
import test from 'node:test';
import {manageApproval} from '../tools/approve-workspace-device.mjs';
import {createApprovedDeviceVerifier} from '../preview/approved-device.mjs';

async function fixture(t) {
  const parent = path.resolve('outputs');
  await mkdir(parent, {recursive: true});
  const directory = await mkdtemp(path.join(parent, 'device-enrollment-'));
  t.after(() => rm(directory, {recursive: true, force: true}));
  const host = {ID: 'fixture-host', Online: true, UserID: 9, DNSName: 'host.example.ts.net.'};
  const login = 'operator@example.invalid';
  const digest = value => createHash('sha256').update(value).digest('hex');
  const settings = {version: 2, enabled: true, ownerHash: digest(login), nodeHash: digest(host.ID),
    httpsPort: 8444, origin: 'https://host.example.ts.net:8444'};
  await writeFile(path.join(directory, 'private-access.json'), JSON.stringify(settings));
  const address = '100.64.5.10';
  const peer = {ID: 'fixture-peer', Online: true, Tags: ['tag:server'], DNSName: 'peer.example.ts.net.', TailscaleIPs: [address]};
  const status = {BackendState: 'Running', Self: host, Peer: {a: peer}, User: {9: {ID: 9, LoginName: login}}};
  const who = {StableID: peer.ID, Addresses: [address + '/32'], Tags: peer.Tags, MachineAuthorized: true};
  const run = async args => JSON.stringify(args[0] === 'whois' ? {Node: who} : status);
  return {directory, host, peer, who, status, address, run, file: path.join(directory, 'approved-device.json')};
}

test('independently identified machine can access after approval and loses access on revoke', async t => {
  const f = await fixture(t);
  const verify = createApprovedDeviceVerifier({configPath: f.file, hostStableID: f.host.ID, runTailscale: f.run});
  const request = {dnsName: f.peer.DNSName, deviceStableID: f.peer.ID};
  assert.equal(await verify(f.address), false);
  assert.equal(await manageApproval('approve', f.directory, request, f.run), 'Device approved for this EE workspace.');
  assert.equal(await verify(f.address), true);
  const saved = JSON.parse(await readFile(f.file, 'utf8'));
  assert.deepEqual(saved, {version: 2, hostStableID: f.host.ID, devices: [{deviceStableID: f.peer.ID, addresses: [f.address]}]});
  assert.equal(await manageApproval('approve', f.directory, request, f.run), 'This device is already approved.');
  f.status.User[9].LoginName = 'different@example.invalid';
  await assert.rejects(manageApproval('approve', f.directory, request, f.run), /Tailscale account/);
  await manageApproval('revoke', f.directory, {deviceStableID: f.peer.ID});
  assert.equal(await verify(f.address), false);
});

test('renamed or mismatched machines and nonexclusive source addresses cannot be enrolled', async t => {
  const f = await fixture(t);
  const request = {dnsName: 'peer.example.ts.net'};
  await assert.rejects(manageApproval('approve', f.directory, {...request, deviceStableID: 'another-peer'}, f.run), /device identity/);
  f.who.Addresses = [f.address + '/24'];
  await assert.rejects(manageApproval('approve', f.directory, request, f.run), /assigned addresses/);
  f.status.Peer.b = structuredClone(f.peer);
  await assert.rejects(manageApproval('approve', f.directory, request, f.run), /exactly one peer/);
  assert.equal(await manageApproval('status', f.directory), 'No device approval is saved.');
});

test('concurrent approvals preserve both tagged peers and targeted revoke preserves the other', async t => {
  const f = await fixture(t);
  const other = {...f.peer, ID: 'fixture-other-peer', DNSName: 'other.example.ts.net.', Online: false, InNetworkMap: true, TailscaleIPs: ['100.64.5.11']};
  f.status.Peer.b = other;
  const run = async args => args[0] === 'whois' && args.at(-1) === other.TailscaleIPs[0]
    ? JSON.stringify({Node: {...f.who, StableID: other.ID, Addresses: [other.TailscaleIPs[0] + '/32']}})
    : f.run(args);
  await Promise.all([f.peer, other].map(peer =>
    manageApproval('approve', f.directory, {dnsName: peer.DNSName}, run)));
  const verify = createApprovedDeviceVerifier({configPath: f.file, hostStableID: f.host.ID, runTailscale: run});
  assert.equal(await manageApproval('status', f.directory), 'Approved devices: 2.');
  assert.equal(await verify(f.address), true);
  assert.equal(await verify(other.TailscaleIPs[0]), true);
  await assert.rejects(manageApproval('revoke', f.directory), /Select the device/);
  await manageApproval('revoke', f.directory, {dnsName: other.DNSName}, run);
  assert.equal(await verify(other.TailscaleIPs[0]), false);
  assert.equal(await verify(f.address), true);
  await manageApproval('revoke', f.directory, {all: true});
  assert.equal(await verify(f.address), false);
});

test('offline or untagged peers require affirmative identity evidence before enrollment', async t => {
  const f = await fixture(t);
  const request = {dnsName: f.peer.DNSName};
  f.peer.Online = false;
  await assert.rejects(manageApproval('approve', f.directory, request, f.run), /tagged Tailscale device/);
  f.peer.InNetworkMap = true;
  f.who.Expired = true;
  await assert.rejects(manageApproval('approve', f.directory, request, f.run), /verify/);
  f.who.Expired = false;
  f.peer.Tags = [];
  await assert.rejects(manageApproval('approve', f.directory, request, f.run), /tagged Tailscale device/);
  assert.equal(await manageApproval('status', f.directory), 'No device approval is saved.');
});

test('old approval survives migration and a new peer cannot reuse its address', async t => {
  const f = await fixture(t);
  const record = {version: 1, hostStableID: f.host.ID, deviceStableID: 'fixture-legacy', addresses: [f.address]};
  await writeFile(f.file, JSON.stringify(record), {mode: 0o600});
  await assert.rejects(manageApproval('approve', f.directory, {dnsName: f.peer.DNSName}, f.run), /conflict/);
  assert.deepEqual(JSON.parse(await readFile(f.file, 'utf8')), record);
  f.peer.TailscaleIPs = ['100.64.5.12'];
  f.who.Addresses = ['100.64.5.12/32'];
  await manageApproval('approve', f.directory, {dnsName: f.peer.DNSName}, f.run);
  assert.equal(await manageApproval('status', f.directory, {deviceStableID: record.deviceStableID}), 'This device is approved.');
  const saved = JSON.parse(await readFile(f.file, 'utf8'));
  assert.equal(saved.version, 2);
  assert.equal(saved.devices.length, 2);
});
