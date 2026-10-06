// Build-time only: npm ci --prefix tools && node tools/render-icons.cjs
const fs = require('node:fs');
const path = require('node:path');
const { Resvg } = require('@resvg/resvg-js');
for (const name of ['codex', 'gemini', 'claude']) {
  const base = path.join(__dirname, '..', 'assets', name);
  fs.writeFileSync(base + '.png', new Resvg(fs.readFileSync(base + '.svg'), {
    fitTo: { mode: 'width', value: 128 }
  }).render().asPng());
}
