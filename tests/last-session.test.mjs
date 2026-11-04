import test from 'node:test';
import assert from 'node:assert/strict';
import {lastSessionView} from '../preview/last-session.mjs';

test('last session displays persisted cleanup outcome and preserves the need for review',()=>{
  const result=lastSessionView({available:true,last_session:{time:'2026-11-04T13:30:00Z',stage:'parent_cleanup',outcome:'cleanup_unconfirmed',build:'0.4.19',attempts:4}});
  assert.equal(result.rows.find(row=>row[0]==='Result')[1],'Lighting cleanup needs review');
  assert.equal(result.rows.find(row=>row[0]==='Cleanup attempts')[1],'4');
  assert.match(result.message,/connection status/);
});

test('missing and unavailable session evidence are distinct from a confirmed shutdown',()=>{
  assert.match(lastSessionView({available:true,last_session:null}).message,/No earlier session/);
  assert.match(lastSessionView({available:false,last_session:null}).message,/could not be read/);
  assert.equal(lastSessionView({available:true,last_session:{time:'2026-11-04T13:30:00Z',stage:'shutdown',outcome:'closed',build:'0.4.19'}}).message,'');
});
