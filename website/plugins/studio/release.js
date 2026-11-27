const {createHash} = require('node:crypto');

const repository = 'https://github.com/DarbotLM/yumlog';
const api = 'https://api.github.com/repos/DarbotLM/yumlog/releases/latest';

async function readBytes(url, maximum, authenticated = false) {
  const headers = {Accept: authenticated ? 'application/vnd.github+json' : '*/*'};
  if (authenticated && process.env.GITHUB_TOKEN) {
    headers.Authorization = `Bearer ${process.env.GITHUB_TOKEN}`;
  }
  const response = await fetch(url, {headers, signal: AbortSignal.timeout(20000)});
  if (!response.ok) throw new Error(`Release metadata request failed: HTTP ${response.status}`);
  if (Number(response.headers.get('content-length')) > maximum) {
    await response.body.cancel();
    throw new Error('Release metadata exceeds the size limit.');
  }
  const chunks = [];
  let size = 0;
  for await (const chunk of response.body) {
    size += chunk.length;
    if (size > maximum) throw new Error('Release metadata exceeds the size limit.');
    chunks.push(chunk);
  }
  return Buffer.concat(chunks);
}

async function loadRelease() {
  const release = JSON.parse((await readBytes(api, 1024 * 1024, true)).toString('utf8'));
  if (release.draft || release.prerelease || !/^v\d+\.\d+\.\d+$/.test(release.tag_name)) {
    throw new Error('Expected a published stable Yumlog semantic-version release.');
  }
  const version = release.tag_name.slice(1);
  const downloadRoot = `${repository}/releases/download/${release.tag_name}/`;
  const asset = name => {
    const matches = release.assets.filter(item => item.name === name);
    if (matches.length !== 1) throw new Error(`Release requires exactly one ${name}.`);
    const item = matches[0];
    if (item.browser_download_url !== downloadRoot + name ||
        !Number.isSafeInteger(item.size) || item.size <= 0 ||
        !/^sha256:[a-f0-9]{64}$/.test(item.digest || '')) {
      throw new Error(`Invalid release asset metadata: ${name}`);
    }
    return {name, size: item.size, url: item.browser_download_url, sha256: item.digest.slice(7)};
  };
  const zip = asset(`Yumlog-${version}-win-x64.zip`);
  const checksum = asset(`${zip.name}.sha256`);
  const manifestAsset = asset('release-manifest.json');
  const msixNames = release.assets.map(item => item.name).filter(name =>
    name.startsWith(`DarbotLabs.Yumlog.Native_${version}.`) && /^\S+_\d+\.\d+\.\d+\.\d+_x64\.msix$/.test(name));
  if (msixNames.length !== 1) throw new Error('Release requires one version-matched native MSIX.');
  const msix = asset(msixNames[0]);
  const verifiedText = async item => {
    const bytes = await readBytes(item.url, 1024 * 1024);
    if (bytes.length !== item.size || createHash('sha256').update(bytes).digest('hex') !== item.sha256) {
      throw new Error(`Release asset hash or size mismatch: ${item.name}`);
    }
    return bytes.toString('utf8');
  };
  const [checksumText, manifestText] = await Promise.all([
    verifiedText(checksum), verifiedText(manifestAsset),
  ]);
  const entries = checksumText.trim().split(/\r?\n/).map(line => /^([a-f0-9]{64})\s+\*?(.+)$/i.exec(line));
  const zipEntries = entries.filter(entry => entry && entry[2] === zip.name);
  if (entries.some(entry => !entry) || zipEntries.length !== 1 || zipEntries[0][1].toLowerCase() !== zip.sha256) {
    throw new Error('ZIP checksum does not match the published GitHub asset digest.');
  }
  const manifest = JSON.parse(manifestText);
  if (manifest.schema !== 'urn:yumlog:release:v1' || manifest.version !== version ||
      !Array.isArray(manifest.components) || !manifest.components.length ||
      manifest.components.some(component => typeof component.path !== 'string' || !/^[a-f0-9]{64}$/i.test(component.sha256))) {
    throw new Error('Release manifest version or component metadata is invalid.');
  }
  if (release.html_url !== `${repository}/releases/tag/${release.tag_name}`) {
    throw new Error('Unexpected release page URL.');
  }
  const installCommand = `gh release download ${release.tag_name} --repo DarbotLM/yumlog --dir .\\downloads \`
  --pattern '${zip.name}' --pattern '${checksum.name}'
$zip = '.\\downloads\\${zip.name}'
$expected = '${zip.sha256}'
$actual = (Get-FileHash $zip -Algorithm SHA256).Hash
if ($actual -ne $expected) { throw 'Package checksum mismatch' }
Expand-Archive -LiteralPath $zip -DestinationPath .\\yumlog-package

gh repo clone DarbotLM/yumlog .\\yumlog-source -- --branch ${release.tag_name} --single-branch
.\\yumlog-source\\launchers\\Install-YumlogProduction.ps1 \`
  -PackagePath .\\yumlog-package -ExpectedVersion ${version}`;
  return {
    version, tag: release.tag_name, url: release.html_url,
    checkedAt: new Date().toISOString(), publishedAt: release.published_at,
    zip, checksum, manifest: manifestAsset, msix,
    msixVersion: msix.name.match(/_(\d+\.\d+\.\d+\.\d+)_x64/)[1],
    componentCount: manifest.components.length, installCommand,
    installer: `${repository}/blob/${release.tag_name}/launchers/Install-YumlogProduction.ps1`,
    msixInstaller: `${repository}/blob/${release.tag_name}/launchers/Install-YumlogNativeMsix.ps1`,
  };
}

module.exports = {loadRelease};
