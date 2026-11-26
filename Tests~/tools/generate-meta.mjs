// Writes the Unity .meta file of every package file and folder that has none.
// A package installed from a git URL is immutable: Unity ignores files without
// a .meta file, so the package must ship them. Folders that end in "~"
// (Samples~, Tests~) and dotfiles are not imported and get no .meta file.
//
//   node "Tests~/tools/generate-meta.mjs"          # write missing files
//   node "Tests~/tools/generate-meta.mjs" --check  # list missing files, exit 1
//
// The GUID is the MD5 of the package path, so it is stable across machines.
import { createHash } from 'node:crypto';
import { existsSync, readdirSync, statSync, writeFileSync } from 'node:fs';
import { dirname, join, relative } from 'node:path';
import { fileURLToPath } from 'node:url';

export const packageRoot = join(dirname(fileURLToPath(import.meta.url)), '..', '..');

/** Every path in the package that Unity imports, relative to the package root. */
export function importedPaths(folder = packageRoot) {
  const out = [];
  for (const name of readdirSync(folder).sort()) {
    if (name.startsWith('.') || name.endsWith('~') || name.endsWith('.meta')) continue;
    const path = join(folder, name);
    out.push(relative(packageRoot, path));
    if (statSync(path).isDirectory()) out.push(...importedPaths(path));
  }
  return out;
}

const guid = path => createHash('md5').update(`dev.manalens.sdk/${path}`).digest('hex');

function importer(path) {
  if (statSync(join(packageRoot, path)).isDirectory()) return 'folderAsset: yes\nDefaultImporter:\n  externalObjects: {}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n';
  if (path.endsWith('.cs')) return 'MonoImporter:\n  externalObjects: {}\n  serializedVersion: 2\n  defaultReferences: []\n  executionOrder: 0\n  icon: {instanceID: 0}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n';
  if (path.endsWith('.asmdef')) return 'AssemblyDefinitionImporter:\n  externalObjects: {}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n';
  if (path === 'package.json') return 'PackageManifestImporter:\n  externalObjects: {}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n';
  return 'TextScriptImporter:\n  externalObjects: {}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n';
}

export const metaContent = path => `fileFormatVersion: 2\nguid: ${guid(path)}\n${importer(path)}`;

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  const missing = importedPaths().filter(path => !existsSync(join(packageRoot, `${path}.meta`)));
  if (process.argv.includes('--check')) {
    if (missing.length) { console.error(`Missing .meta files:\n${missing.join('\n')}`); process.exit(1); }
  } else {
    for (const path of missing) writeFileSync(join(packageRoot, `${path}.meta`), metaContent(path));
    console.log(`Wrote ${missing.length} .meta files`);
  }
}
