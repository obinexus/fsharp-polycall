'use strict';

const path = require('node:path');

const fromPackageRoot = (...parts) => path.join(__dirname, ...parts);

module.exports = Object.freeze({
  packageName: '@obinexusltd/fsharp-polycall',
  fsharpProject: fromPackageRoot('src', 'FSharpPolycall', 'FSharpPolycall.fsproj'),
  fsharpSource: fromPackageRoot('src', 'FSharpPolycall', 'Polycall.fs'),
  nativeSource: fromPackageRoot('c_src', 'fsharp_polycall.c'),
  nativeHeader: fromPackageRoot('include', 'fsharp_polycall.h'),
  ffiHeader: fromPackageRoot('generated', 'polycall', 'polycall_ffi.h'),
  config: fromPackageRoot('fsharp-polycallrc'),
  manifest: fromPackageRoot('polycall-binding.json'),
  makefile: fromPackageRoot('Makefile')
});
