import http from 'node:http';
import { readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const root = fileURLToPath(new URL('.', import.meta.url));
const port = Number(process.env.PORT || 4173);
const backendPort = Number(process.env.EE_BACKEND_PORT || 8787);
const types = { '.html': 'text/html; charset=utf-8', '.css': 'text/css; charset=utf-8', '.mjs': 'text/javascript; charset=utf-8', '.svg': 'image/svg+xml', '.png': 'image/png', '.ico': 'image/x-icon' };
const server = http.createServer(async (req, res) => {
  try {
    if (![`127.0.0.1:${port}`, `localhost:${port}`].includes(req.headers.host)) { res.writeHead(403); res.end(); return; }
    if (req.url.startsWith('/api/')) {
      if (req.headers.origin && ![`http://127.0.0.1:${port}`,`http://localhost:${port}`].includes(req.headers.origin)) { res.writeHead(403); res.end(); return; }
      const upstream = http.request({hostname:'127.0.0.1',port:backendPort,path:req.url,method:req.method,headers:req.headers},response => {res.writeHead(response.statusCode,response.headers);response.pipe(res);});
      upstream.setTimeout(30000,() => upstream.destroy());
      upstream.on('error',() => {if (!res.headersSent) res.writeHead(503,{'Content-Type':'application/json'});res.end(JSON.stringify({detail:{code:'unavailable'}}));});
      req.pipe(upstream);return;
    }
    if (!['GET', 'HEAD'].includes(req.method)) { res.writeHead(405); res.end(); return; }
    const pathname = decodeURIComponent(new URL(req.url, 'http://localhost').pathname);
    const relative = pathname === '/' ? 'index.html' : pathname.slice(1);
    if (!(relative === 'index.html' || relative.startsWith('src/') || relative.startsWith('assets/'))) { res.writeHead(404); res.end('Not found'); return; }
    const file = path.resolve(root, relative);
    if (!file.startsWith(root) || !types[path.extname(file)]) { res.writeHead(404); res.end('Not found'); return; }
    const body = await readFile(file);
    res.writeHead(200, { 'Content-Type': types[path.extname(file)], 'Cache-Control': 'no-store', 'X-Content-Type-Options': 'nosniff', 'Referrer-Policy': 'no-referrer', 'Content-Security-Policy': "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self' http://127.0.0.1:* http://localhost:* ws://127.0.0.1:* ws://localhost:*; frame-ancestors 'none'" });
    res.end(req.method === 'HEAD' ? undefined : body);
  } catch { res.writeHead(404); res.end('Not found'); }
});
server.on('upgrade',(req,socket,head) => {
  if (![`127.0.0.1:${port}`, `localhost:${port}`].includes(req.headers.host) || req.url !== '/api/ws' || (req.headers.origin && ![`http://127.0.0.1:${port}`,`http://localhost:${port}`].includes(req.headers.origin))) {socket.destroy();return;}
  const upstream = http.request({hostname:'127.0.0.1',port:backendPort,path:'/api/ws',headers:req.headers});
  upstream.on('upgrade',(response,remote,remoteHead) => {
    socket.write(`HTTP/1.1 101 Switching Protocols\r\n${Object.entries(response.headers).map(([key,value]) => `${key}: ${value}`).join('\r\n')}\r\n\r\n`);
    if (head.length) remote.write(head);if (remoteHead.length) socket.write(remoteHead);
    remote.pipe(socket);socket.pipe(remote);remote.on('error',() => socket.destroy());socket.on('error',() => remote.destroy());
  });
  upstream.on('error',() => socket.destroy());upstream.on('response',() => socket.destroy());upstream.end();
});
server.listen(port, '127.0.0.1', () => console.log(`Etherlighting Evolved · http://127.0.0.1:${port}`));
