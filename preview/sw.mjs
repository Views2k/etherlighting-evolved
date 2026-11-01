const CACHE_PREFIX = 'ee-pwa-static-';
const CACHE_NAME = `${CACHE_PREFIX}v1`;
const OFFLINE_URL = new URL('/preview/offline.html?ee-static=v1',self.location.origin).href;
const OFFLINE_HTML = `<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><meta name="theme-color" content="#1b1923"><title>Reconnect · Etherlighting Evolved</title>
<style>html{color-scheme:dark;background:#1b1923;color:#f4f1f7;font:16px/1.5 system-ui,-apple-system,sans-serif}body{min-height:100svh;box-sizing:border-box;margin:0;display:grid;place-items:center;padding:24px;background:radial-gradient(ellipse at 15% 10%,#20283c,transparent 65%),radial-gradient(ellipse at 90% 80%,#302034,transparent 65%)}main{width:min(100%,420px)}p{color:#c6bed1}h1{font-size:26px;line-height:1.2;letter-spacing:-.025em}a{display:inline-flex;align-items:center;justify-content:center;min-height:44px;padding:0 20px;border-radius:9px;background:#694bb1;color:white;text-decoration:none;font-weight:600;margin-top:12px}a:hover{background:#795ac1}a:focus-visible{outline:3px solid #b9caff;outline-offset:4px}</style></head>
<body><main><p>Etherlighting Evolved</p><h1>Reconnect to your workspace</h1><p>Make sure Etherlighting Evolved is running, then reconnect and try again.</p><a href="/">Try again</a></main></body></html>`;
const offlineResponse = () => new Response(OFFLINE_HTML, {status:503,headers:{
  'Content-Type':'text/html; charset=utf-8',
  'Cache-Control':'no-store',
  'Content-Security-Policy':"default-src 'none'; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'",
  'Referrer-Policy':'no-referrer',
  'X-Content-Type-Options':'nosniff'
}});

self.addEventListener('install',event => {
  event.waitUntil(caches.open(CACHE_NAME).then(cache => cache.put(OFFLINE_URL,offlineResponse())));
});

self.addEventListener('activate',event => {
  event.waitUntil(caches.keys().then(keys => Promise.all(keys
    .filter(key => key.startsWith(CACHE_PREFIX) && key !== CACHE_NAME)
    .map(key => caches.delete(key)))));
});

self.addEventListener('fetch',event => {
  const request = event.request, url = new URL(request.url);
  if (request.method !== 'GET' || request.mode !== 'navigate' || url.origin !== self.location.origin
      || !['/','/preview/index.html'].includes(url.pathname)) return;
  event.respondWith(fetch(request).then(response => response.status >= 500 ? fallback() : response, fallback));
});

async function fallback() {
  try { return await (await caches.open(CACHE_NAME)).match(OFFLINE_URL) || offlineResponse(); }
  catch { return offlineResponse(); }
}
