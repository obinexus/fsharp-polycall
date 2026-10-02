'use strict';

// npm package integrity: the entry point loads and every path it exports
// exists in the (packed or checked-out) package.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const binding = require('..');
const metadata = require('../package.json');
const manifest = require('../polycall-binding.json');

assert.equal(metadata.name, '@obinexusltd/fsharp-polycall');
assert.equal(metadata.license, 'MIT');
assert.equal(metadata.publishConfig.access, 'public');
assert.equal(metadata.repository.url, 'git+https://github.com/obinexus/fsharp-polycall.git');
assert.equal(manifest.version, metadata.version, 'polycall-binding.json version matches package.json');
assert.equal(manifest.core, 'polycall >= 1.1.0 (binding ABI 1)');
assert.equal(manifest.core_repository, 'https://github.com/obinexus/polycall');

const author = typeof metadata.author === 'string'
  ? metadata.author
  : `${metadata.author?.name} <${metadata.author?.email}>`;
assert.equal(author, 'Nnamdi Michael Okpala <okpalan@protonmail.com>');

const fsproj = fs.readFileSync(binding.fsharpProject, 'utf8');
assert.match(fsproj, new RegExp(`<Version>${metadata.version.replace(/\./g, '\\.')}</Version>`),
  'FSharpPolycall.fsproj <Version> matches package.json');

for (const [name, file] of Object.entries(binding)) {
  if (typeof file !== 'string' || !path.isAbsolute(file)) continue;
  assert.equal(fs.existsSync(file), true, `missing ${name}: ${file}`);
}

console.log('fsharp-polycall npm package test: PASS');
