const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const root = path.resolve(__dirname, '../..');
const lucide = require(path.resolve(process.argv[2]));
const names = ['music','audio-lines','list-music','list-ordered','list-plus','play','pause','square','skip-forward','rotate-ccw','plus','minus','x','search','trash-2','arrow-up','arrow-down','chevron-down','chevron-right','copy','maximize','minimize','mic','mic-off','volume-2','volume-x','flame','folder-open','download','package','sparkles','settings','circle-check','check','smartphone'];
const target = path.join(root, 'src/OpenKaraoke.Prototype/Assets/Icons');
fs.mkdirSync(target, {recursive:true});
const entries = [];
for (const name of names) {
  const key = name.replace(/(^|-)([a-z0-9])/g, (_, __, c) => c.toUpperCase());
  const nodes = lucide.icons[key];
  if (!nodes) throw new Error(`Missing official icon ${key}`);
  const elements = nodes.map(([tag, attrs]) => `  <${tag} ${Object.entries(attrs).filter(([key]) => key !== 'key').map(([key,value]) => `${key}="${String(value).replaceAll('&','&amp;').replaceAll('"','&quot;')}"`).join(' ')} />`).join('\n');
  const svg = `<svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">\n${elements}\n</svg>\n`;
  fs.writeFileSync(path.join(target, `${name}.svg`), svg);
  entries.push({name, sha256:crypto.createHash('sha256').update(svg).digest('hex')});
}
fs.writeFileSync(path.join(target,'manifest.json'),JSON.stringify({version:'1.17.0',source:'https://unpkg.com/lucide@1.17.0/dist/umd/lucide.js',license:'ISC / Feather MIT',icons:entries},null,2)+'\n');
console.log(`Bundled ${entries.length} official Lucide icons.`);
