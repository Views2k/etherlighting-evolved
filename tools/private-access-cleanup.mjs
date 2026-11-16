import path from 'node:path';
import {pathToFileURL} from 'node:url';
import {createPrivateAccessManager, runInstalledTailscale} from '../preview/private-access.mjs';

// The uninstaller runs this as the user who runs EE, after EE has quit. It removes EE's Tailscale Serve route and
// leaves private-access.json alone, so a later installation adds the route again when private access is on. The exit
// code carries the HTTPS port when the removal is unconfirmed, so the installer can name that port without reading
// the output.
export async function removePrivateAccessRoute(directory, run = runInstalledTailscale, {timeoutMs = 30000} = {}) {
  const manager = createPrivateAccessManager({configPath: path.join(directory, 'private-access.json'), runTailscale: run,
    startListener: async () => ({}), stopListener: async () => {}});
  const {route, https_port: port} = await manager.shutdown({timeoutMs});
  if (route === 'none') return {exitCode: 0, message: 'EE has no private access setting for this user.'};
  if (route === 'absent') return {exitCode: 0, message: 'EE has no Tailscale Serve route on this computer.'};
  if (route === 'removed') return {exitCode: 0, message: `EE removed its Tailscale Serve route on port ${port}.`};
  if (route === 'no_tailscale') return {exitCode: 0, message: 'Tailscale is not installed on this computer, so EE has no route to remove.'};
  if (port === null) {
    return {exitCode: 2, message: 'EE could not read its private access setting. Run "tailscale serve status" to review the Tailscale Serve routes on this computer.'};
  }
  return {exitCode: port, message: `EE could not confirm that its Tailscale Serve route on port ${port} was removed. Run "tailscale serve status". If port ${port} forwards to http://127.0.0.1, remove it with "tailscale serve --https=${port} off".`};
}

// The message is written before the exit, so a console or pipe receives all of it.
function finish(exitCode, message) {
  (exitCode === 0 ? process.stdout : process.stderr).write(message + '\n', () => process.exit(exitCode));
}

if (process.argv[1] && pathToFileURL(path.resolve(process.argv[1])).href === import.meta.url) {
  if (process.platform !== 'win32' || !path.isAbsolute(process.env.LOCALAPPDATA ?? '') || process.argv.length > 2) {
    finish(2, 'Run this command without arguments as the user who runs EE on Windows.');
  } else {
    try {
      const {exitCode, message} = await removePrivateAccessRoute(path.join(process.env.LOCALAPPDATA, 'Etherlighting Evolved', 'workspace'));
      finish(exitCode, message);
    } catch {
      finish(2, 'EE could not check its Tailscale Serve route.');
    }
  }
}
