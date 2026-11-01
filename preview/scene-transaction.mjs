import {sceneFingerprint} from './secondary-model.mjs';

export async function commitScene(before,scene,send,{preserveUnchanged=true}={}) {
  try {
    return await send('/api/scene','PUT',{scene,expected_revision:before.revision,
      preserve_unchanged:preserveUnchanged,confirm_output:true});
  } catch(error) {
    // A lost reply does not mean the accepted configuration was rolled back.
    if(error.uncertain||error.code==='output_unconfirmed'||error.code==='scene_changed') {
      try {
        const current=await send('/api/scene');
        if(sceneFingerprint(current.scene)===sceneFingerprint(scene))error.acceptedScene=current;
      } catch { /* Keep the original confirmation error. */ }
    }
    throw error;
  }
}
