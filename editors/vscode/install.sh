#!/usr/bin/env bash
# Packages the Digger extension as a .vsix (no Node or vsce needed, just python3) and installs
# it into every VS Code-family editor whose command line tool is on PATH: code, code-insiders,
# cursor, codium, windsurf. Pass editor commands to choose:  ./install.sh cursor
#
# Alternatives: install the printed .vsix by hand (Extensions view → … → Install from VSIX),
# or, while working on the extension, run "Developer: Install Extension from Location...".
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
version="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["version"])' "$here/package.json")"
vsix="$here/digger-$version.vsix"

python3 - "$here" "$vsix" "$version" <<'EOF'
import os, sys, zipfile
here, vsix, version = sys.argv[1:]
manifest = f"""<?xml version="1.0" encoding="utf-8"?>
<PackageManifest Version="2.0.0" xmlns="http://schemas.microsoft.com/developer/vsx-schema/2011">
  <Metadata>
    <Identity Language="en-US" Id="digger" Version="{version}" Publisher="digger" />
    <DisplayName>Digger (.NET Debugger)</DisplayName>
    <Description xml:space="preserve">Debug .NET (CoreCLR) applications with the Digger debug adapter.</Description>
    <Categories>Debuggers</Categories>
    <Properties>
      <Property Id="Microsoft.VisualStudio.Code.Engine" Value="^1.80.0" />
    </Properties>
  </Metadata>
  <Installation>
    <InstallationTarget Id="Microsoft.VisualStudio.Code" />
  </Installation>
  <Dependencies />
  <Assets>
    <Asset Type="Microsoft.VisualStudio.Code.Manifest" Path="extension/package.json" Addressable="true" />
    <Asset Type="Microsoft.VisualStudio.Services.Content.Details" Path="extension/README.md" Addressable="true" />
  </Assets>
</PackageManifest>
"""
content_types = """<?xml version="1.0" encoding="utf-8"?>
<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
  <Default Extension=".json" ContentType="application/json" />
  <Default Extension=".js" ContentType="application/javascript" />
  <Default Extension=".md" ContentType="text/markdown" />
  <Default Extension=".vsixmanifest" ContentType="text/xml" />
</Types>
"""
with zipfile.ZipFile(vsix, "w", zipfile.ZIP_DEFLATED) as z:
    z.writestr("[Content_Types].xml", content_types)
    z.writestr("extension.vsixmanifest", manifest)
    for name in ("package.json", "extension.js", "README.md"):
        z.write(os.path.join(here, name), "extension/" + name)
EOF
echo "Packaged $vsix"

editors=("$@")
if [ ${#editors[@]} -eq 0 ]; then
  for candidate in code code-insiders cursor codium windsurf; do
    command -v "$candidate" >/dev/null 2>&1 && editors+=("$candidate")
  done
fi

if [ ${#editors[@]} -eq 0 ]; then
  echo "No editor command line found (code, cursor, codium, windsurf); install $vsix from the Extensions view."
  exit 0
fi

for editor in "${editors[@]}"; do
  "$editor" --install-extension "$vsix" --force
done
