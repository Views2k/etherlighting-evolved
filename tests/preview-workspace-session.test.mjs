import test from 'node:test';
import assert from 'node:assert/strict';
import {workspaceStorageKey, connectionLabel, switchConnectionStatus, controllerConnectionStatus} from '../preview/workspace-session.mjs';

test('only a signed-in opaque workspace identity can restore browser drafts', () => {
  assert.equal(workspaceStorageKey({logged_in:false,workspace_id:'a'.repeat(64)}),null);
  assert.equal(workspaceStorageKey({logged_in:true,host:'private-host'}),null);
  assert.equal(workspaceStorageKey({logged_in:true,workspace_id:'private-host'}),null);
  assert.notEqual(workspaceStorageKey({logged_in:true,workspace_id:'a'.repeat(64)}),
    workspaceStorageKey({logged_in:true,workspace_id:'b'.repeat(64)}));
});

test('status distinguishes real connection, simulator, and interruption', () => {
  assert.equal(connectionLabel({logged_in:true,connected:true,simulated:false}),'Switch connected');
  assert.equal(connectionLabel({logged_in:true,connected:true,simulated:true}),'Demo session');
  assert.equal(connectionLabel({logged_in:true,connected:false}),'Switch reconnecting…');
  assert.equal(connectionLabel({connecting:true}),'Connecting…');
  assert.equal(connectionLabel({logged_in:false}),'Not connected');
  assert.equal(connectionLabel({logged_in:true},true),'Reconnecting…');
});

test('a real switch stays connected while saving but not while reconnecting', () => {
  const session = {logged_in:true, connected:true, simulated:false};
  assert.deepEqual(switchConnectionStatus(session), {label:'Switch connected', tone:'online'});
  assert.deepEqual(switchConnectionStatus(session, false, true), {label:'Saving…', tone:'online'});
  assert.deepEqual(switchConnectionStatus(session, true), {label:'Reconnecting…', tone:'waiting'});
  assert.deepEqual(switchConnectionStatus({...session, connected:false}), {label:'Switch reconnecting…', tone:'waiting'});
  assert.deepEqual(switchConnectionStatus({...session, connecting:true}), {label:'Connecting…', tone:'waiting'});
});

test('demo, unknown, and signed-out switch statuses never imply a hardware connection', () => {
  assert.deepEqual(switchConnectionStatus({logged_in:true, connected:true, simulated:true}), {label:'Demo session', tone:'is-disconnected'});
  assert.deepEqual(switchConnectionStatus({logged_in:false, connected:true}), {label:'Not connected', tone:'is-disconnected'});
  assert.deepEqual(switchConnectionStatus(), {label:'Not connected', tone:'is-disconnected'});
  assert.deepEqual(switchConnectionStatus({connecting:true}), {label:'Connecting…', tone:'waiting'});
});

test('controller connection status is independent of whether a login is saved', () => {
  const expected = {label:'Controller connected', tone:'online'};
  assert.deepEqual(controllerConnectionStatus({connected:true, stale:false, auto_reconnect:false, saved_login_available:false}), expected);
  assert.deepEqual(controllerConnectionStatus({connected:true, auto_reconnect:true, saved_login_available:true}), expected);
  assert.deepEqual(controllerConnectionStatus({connected:false, auto_reconnect:true, saved_login_available:true}), {label:'Controller not connected', tone:'is-disconnected'});
});

test('stale controller data keeps a waiting status without claiming healthy fresh data', () => {
  assert.deepEqual(controllerConnectionStatus({connected:true, stale:true}), {label:'Controller connected · Waiting for fresh data', tone:'waiting'});
  assert.deepEqual(controllerConnectionStatus({connected:true, stale:true, error:{code:'unreachable'}}), {label:'Controller connected · Waiting for fresh data', tone:'waiting'});
});

test('controller failures use a fixed label without exposing diagnostic data', () => {
  assert.deepEqual(controllerConnectionStatus({connected:false, error:{code:'RAW_CODE', message:'RAW_DIAGNOSTIC', host:'private-host'}}), {label:'Controller connection needs attention', tone:'error'});
  assert.deepEqual(controllerConnectionStatus({connected:false, error:null}), {label:'Controller not connected', tone:'is-disconnected'});
  assert.deepEqual(controllerConnectionStatus(), {label:'Controller not connected', tone:'is-disconnected'});
});

test('an unavailable controller status overrides a cached connection without labeling it disconnected', () => {
  assert.deepEqual(controllerConnectionStatus({connected:true}, true), {label:'Controller status unavailable', tone:'is-disconnected'});
  assert.deepEqual(controllerConnectionStatus({connected:false, error:{code:'unreachable'}}, true), {label:'Controller status unavailable', tone:'is-disconnected'});
});
