import test from 'node:test';
import assert from 'node:assert/strict';
import {scheduleConflicts,scheduleDraftError} from '../preview/weekly-schedules.mjs';
import {profileLayoutNotice} from '../preview/profile-preview.mjs';

test('schedule editor warns only for enabled same-time overlapping weekdays on the connected switch',()=>{
 const draft={id:'one',name:'Evening',enabled:true,at:'18:00',days:['wed'],action:{type:'brightness',value:23}};
 const rows=[{...draft,id:'two'},{...draft,id:'three',days:['thu']},{...draft,id:'four',enabled:false},{...draft,id:'five',needs_device_confirmation:true}];
 assert.deepEqual(scheduleConflicts(draft,rows).map(row=>row.id),['two']);
 assert.equal(scheduleDraftError(draft),'');assert.match(scheduleDraftError({...draft,days:[]}),/day/);
 assert.match(scheduleDraftError({...draft,action:{type:'preset'}}),/profile/);
});
test('v3 profile layout warning compares the exact current copper and SFP IDs',()=>{
 const scene={version:3,topology:{version:1,model:null,copper_ports:Array.from({length:24},(_,i)=>i+1),sfp_ports:[25,26]}};
 const state={num_ports:8,sfp_ports:[9,10]};assert.match(profileLayoutNotice(scene,state),/24 Ethernet and 2 SFP/);
 assert.equal(profileLayoutNotice(scene,{num_ports:24,sfp_ports:[25,26]}),'');
 scene.topology.sfp_ports=[27,28];assert.notEqual(profileLayoutNotice(scene,{num_ports:24,sfp_ports:[25,26]}),'');
});

test('profile model and legacy layout notices follow backend topology rules',()=>{
 const state={num_ports:8,sfp_ports:[9,10],hardware_profile:{id:'current'}};
 const scene={version:3,topology:{version:1,model:'saved',copper_ports:[1,2,3,4,5,6,7,8],sfp_ports:[9,10]}};
 assert.match(profileLayoutNotice(scene,state),/different switch model/);
 scene.topology.model=null;assert.equal(profileLayoutNotice(scene,state),'');
 assert.equal(profileLayoutNotice({version:2,zones:[],sfp:{}},state),'');
 assert.match(profileLayoutNotice({version:2,zones:[],sfp:{}},{num_ports:24,sfp_ports:[25,26]}),/older profile/);
 assert.match(profileLayoutNotice({version:2,zones:[]},{num_ports:8,sfp_ports:[]}),/older profile/);
 assert.equal(profileLayoutNotice({version:1,effect:'static',params:{}},{num_ports:24,sfp_ports:[25,26]}),'');
 assert.match(profileLayoutNotice({version:1,effect:'static',rules:[{}]},{num_ports:24,sfp_ports:[25,26]}),/older profile/);
});
