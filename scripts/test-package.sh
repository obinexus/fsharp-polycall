#!/bin/sh
# Pack OBINexus.FSharpPolycall into a LOCAL directory (never published), then
# build and run a clean consumer project that restores it from that directory
# and uses it against the REAL installed libpolycall.
#
#   POLYCALL_LIBRARY=/opt/polycall/lib/libpolycall.so.1 sh scripts/test-package.sh [outdir]
#
# Exit status: 0 = pass, 1 = failure, 77 = SKIP (no .NET 8 SDK or no unzip).
set -u
cd "$(dirname "$0")/.." || exit 1
command -v dotnet >/dev/null 2>&1 || { echo "SKIP: dotnet not found"; exit 77; }
dotnet --list-sdks 2>/dev/null | grep -Eq '^([89]|[1-9][0-9])\.' || { echo "SKIP: no .NET SDK >= 8"; exit 77; }
command -v unzip >/dev/null 2>&1 || { echo "SKIP: unzip not found (needed to inspect the package)"; exit 77; }

OUT=${1:-$(pwd)/build/package}
rm -rf "$OUT"; mkdir -p "$OUT/feed" "$OUT/consumer" "$OUT/packages"
VERSION=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' src/FSharpPolycall/FSharpPolycall.fsproj)

dotnet pack src/FSharpPolycall/FSharpPolycall.fsproj -c Release -o "$OUT/feed" || exit 1
NUPKG="$OUT/feed/OBINexus.FSharpPolycall.$VERSION.nupkg"
[ -f "$NUPKG" ] || { echo "FAIL: $NUPKG was not produced"; exit 1; }
echo "packed: $NUPKG"
# the package must carry the library, its XML docs, README and LICENSE, and the right metadata
LIST=$(unzip -l "$NUPKG")
for f in lib/net8.0/FSharpPolycall.dll lib/net8.0/FSharpPolycall.xml README.md LICENSE OBINexus.FSharpPolycall.nuspec; do
  echo "$LIST" | grep -q "$f" || { echo "FAIL: $f missing from the package"; exit 1; }
done
NUSPEC=$(unzip -p "$NUPKG" OBINexus.FSharpPolycall.nuspec)
for want in "<id>OBINexus.FSharpPolycall</id>" "<version>$VERSION</version>" \
            '<license type="expression">MIT</license>' 'https://github.com/obinexus/fsharp-polycall' \
            'https://github.com/obinexus/polycall' '<readme>README.md</readme>'; do
  echo "$NUSPEC" | grep -qF "$want" || { echo "FAIL: nuspec lacks $want"; echo "$NUSPEC"; exit 1; }
done
echo "package contents and nuspec: PASS"

cat > "$OUT/consumer/nuget.config" <<EOF2
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="$OUT/feed" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
EOF2
cat > "$OUT/consumer/Consumer.fsproj" <<EOF2
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="Program.fs" />
    <PackageReference Include="OBINexus.FSharpPolycall" Version="$VERSION" />
  </ItemGroup>
</Project>
EOF2
cat > "$OUT/consumer/Program.fs" <<'EOF2'
open System
open System.IO
open OBINexus.Polycall

[<EntryPoint>]
let main _ =
    let check what cond = if not cond then failwithf "FAIL: %s" what else printfn "ok   %s" what
    check "ABI 1" (Polycall.abiVersion () = 1)
    printfn "libpolycall %s from %s" (Polycall.version ()) (Polycall.libraryName ())
    let dir = Directory.CreateTempSubdirectory("consumer-").FullName
    let rc = Path.Combine(dir, "consumer-é-polycallrc")
    File.WriteAllText(rc, "log_level=info\n")
    check "run_config strict on a valid file" (Polycall.runConfig rc = 0)
    File.WriteAllText(rc, "tls_enabled=true\ncert_file=/x/c.pem\nkey_file=/x/k.pem\n")
    check "tls_enabled=true is Unsupported" (Polycall.runConfig rc = int PolycallStatus.Unsupported)
    use a = Peer.Open("consumer-a", "127.0.0.1:0")
    use b = Peer.Open("consumer-b", "127.0.0.1:0")
    let payload = Array.init 300 byte
    a.Send(b.Endpoint, payload, "pkg-1", 5000u)
    let m = b.Recv 5000u
    check "payload a -> b" (m.Sender = "consumer-a" && m.MessageId = "pkg-1" && m.Payload = payload)
    b.SendText(a.Endpoint, "back", "pkg-2", 5000u)
    let m = a.Recv 5000u
    check "payload b -> a" (m.Sender = "consumer-b" && m.Text = "back")
    let err = try a.Send("nobody", [| 1uy |], "pkg-3", 1000u); None with :? PolycallException as e -> Some e
    check "PolycallException with status, name and detail"
        (match err with
         | Some e -> e.StatusCode = PolycallStatus.NotFound && e.StatusName = "POLYCALL_E_NOT_FOUND" && e.Detail <> ""
         | None -> false)
    printfn "PACKAGE-CONSUMER PASS"
    0
EOF2
cd "$OUT/consumer" || exit 1
dotnet run -c Release --packages "$OUT/packages" || { echo "FAIL: consumer run"; exit 1; }
echo "consumer restored OBINexus.FSharpPolycall $VERSION from $OUT/feed: PASS"
