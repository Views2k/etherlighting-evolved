import {realpathSync} from 'node:fs';
import {pathToFileURL} from 'node:url';

// Node names the entry module by its real path, after it resolves symbolic links and junctions. Comparing the path
// as typed would make a script started through a link skip its own command and exit 0.
export function isEntryScript(moduleUrl) {
  return Boolean(process.argv[1])&&pathToFileURL(realpathSync(process.argv[1])).href===moduleUrl;
}
