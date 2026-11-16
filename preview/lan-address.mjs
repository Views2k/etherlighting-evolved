// Shared by the browser workspace and the LAN gateway, so both read addresses, pairing codes and device names the
// same way.

// The canonical dotted form of an address in 10.0.0.0/8, 172.16.0.0/12 or 192.168.0.0/16, or null. Node reports an
// IPv4 connection on a dual-stack socket in its ::ffff: mapped form.
export function privateIPv4(text) {
  if (typeof text !== 'string') return null;
  const address = text.replace(/^::ffff:/i, '');
  const parts = /^(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})$/.exec(address)?.slice(1);
  if (!parts || parts.some(part => String(Number(part)) !== part || Number(part) > 255)) return null;
  const [first, second] = parts.map(Number);
  return first === 10 || first === 172 && second >= 16 && second <= 31 || first === 192 && second === 168 ? address : null;
}

export function loopbackHost(hostname) {
  return ['127.0.0.1', 'localhost', '[::1]'].includes(hostname);
}

// A workspace page opened at the address LAN access shows: plain HTTP to a private IPv4 literal with its port.
export function lanPage(location) {
  return location.protocol === 'http:' && privateIPv4(location.hostname) === location.hostname && location.port !== '';
}

// The six digits of a pairing code as typed, which may group them with spaces or hyphens, or null.
export function pairingCode(text) {
  const digits = typeof text === 'string' ? text.replace(/[ -]/g, '') : '';
  return /^\d{6}$/.test(digits) ? digits : null;
}

// A paired device's name, trimmed: 1 to 40 characters without control, format or separator characters, or null.
export function deviceName(text) {
  if (typeof text !== 'string') return null;
  const name = text.trim(), length = [...name].length;
  return length >= 1 && length <= 40 && !/[\p{Cc}\p{Cf}\p{Zl}\p{Zp}]/u.test(name) ? name : null;
}
