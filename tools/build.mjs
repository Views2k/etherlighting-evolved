import { mkdir,readdir,writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { createHash } from 'node:crypto';
import { readFile } from 'node:fs/promises';

const root = fileURLToPath(new URL('../',import.meta.url));
const destination = path.join(root,'dist');
const files = ['index.html'];
for (const folder of ['assets','src']) {
  for (const entry of await readdir(path.join(root,folder),{withFileTypes:true})) {
    if (entry.isFile() && /\.(mjs|css|svg|png|ico)$/.test(entry.name)) files.push(`${folder}/${entry.name}`);
  }
}
const manifest = {};
const sources = new Map(await Promise.all(files.map(async file => [file,await readFile(path.join(root,file))])));
const hash = createHash('sha256');
for (const file of [...files].sort()) hash.update(file).update(sources.get(file));
const version = hash.digest('hex').slice(0,16);
for (const file of files) {
  const target = path.join(destination,file);
  await mkdir(path.dirname(target),{recursive:true});
  let content = sources.get(file);
  if (/\.(html|mjs|css|svg)$/.test(file)) content = content.toString('utf8');
  if (file.endsWith('.mjs')) content = content.replace(/(from\s+|import\s+)(['"])(\.\.?\/[^'"]+\.mjs)\2/g,`$1$2$3?v=${version}$2`);
  if (file.endsWith('.mjs')) content = content.replace(/(["'])(\/assets\/[^"'?]+\.(?:png|ico|svg))\1/g,`$1$2?v=${version}$1`);
  if (file === 'index.html') content = content.replace(/((?:src|href)="\/(?:src|assets)\/[^"?]+)(")/g,`$1?v=${version}$2`);
  await writeFile(target,content);
  manifest[file] = createHash('sha256').update(content).digest('hex');
}
await writeFile(path.join(destination,'manifest.json'),JSON.stringify(manifest,null,2) + '\n');
console.log(`Built ${files.length} frontend files in dist/. No dependencies or network access required.`);
