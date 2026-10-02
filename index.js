'use strict';

// @obinexusltd/fsharp-polycall is a source distribution of an F#/.NET
// binding; requiring it from Node.js only locates the packaged files.
const path = require('node:path');

const fromPackageRoot = (...parts) => path.join(__dirname, ...parts);

module.exports = Object.freeze({
  packageName: '@obinexusltd/fsharp-polycall',
  language: 'F#',
  abi: 1,
  fsharpProject: fromPackageRoot('src', 'FSharpPolycall', 'FSharpPolycall.fsproj'),
  nativeDeclarations: fromPackageRoot('src', 'FSharpPolycall', 'Native.fs'),
  fsharpSource: fromPackageRoot('src', 'FSharpPolycall', 'Polycall.fs'),
  peerSource: fromPackageRoot('src', 'FSharpPolycall', 'Peer.fs'),
  cliProject: fromPackageRoot('src', 'FSharpPolycall.Cli', 'FSharpPolycall.Cli.fsproj'),
  testProject: fromPackageRoot('tests', 'FSharpPolycall.Tests', 'FSharpPolycall.Tests.fsproj'),
  config: fromPackageRoot('fsharp-polycallrc'),
  manifest: fromPackageRoot('polycall-binding.json')
});
