'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const binding = require('..');
const metadata = require('../package.json');

assert.equal(metadata.name, '@obinexusltd/fsharp-polycall');
assert.equal(metadata.license, 'MIT');
assert.equal(metadata.author.name, 'Nnamdi Michael Okpala');
assert.equal(metadata.publishConfig.access, 'public');

for (const [name, file] of Object.entries(binding)) {
  if (name === 'packageName') continue;
  assert.equal(fs.existsSync(file), true, `missing ${name}: ${file}`);
}

console.log('fsharp-polycall npm package test: PASS');
