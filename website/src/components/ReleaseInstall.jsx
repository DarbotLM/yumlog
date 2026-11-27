import React from 'react';
import CodeBlock from '@theme/CodeBlock';
import {useRelease} from './release';

export default function ReleaseInstall() {
  const release = useRelease();
  return <>
    <h3>Download Yumlog {release.version}</h3>
    <table><thead><tr><th>Download</th><th>Contents</th><th>Bytes</th></tr></thead><tbody>
      <tr><td><a href={release.zip.url}>Full Windows x64 ZIP</a></td><td>{release.componentCount} self-contained components</td><td>{release.zip.size.toLocaleString('en-US')}</td></tr>
      <tr><td><a href={release.checksum.url}>ZIP SHA-256</a></td><td>Checksum for the suite</td><td>{release.checksum.size}</td></tr>
      <tr><td><a href={release.manifest.url}>Release manifest</a></td><td>Component versions and payload hashes</td><td>{release.manifest.size}</td></tr>
      <tr><td><a href={release.msix.url}>Native Studio MSIX</a></td><td>Windows package {release.msixVersion}</td><td>{release.msix.size.toLocaleString('en-US')}</td></tr>
    </tbody></table>
    <p><a href={release.url}>Release notes</a>. Metadata checked at build time on {release.checkedAt.slice(0, 10)}.
      The checksum and manifest bytes match GitHub&apos;s SHA-256 digests; the ZIP checksum matches its published digest.
      Application binaries and signatures are not revalidated by the website build.</p>
    <h3>Instructions: verify and install the ZIP</h3>
    <p>Run these commands yourself in a working directory with sufficient space.
      The downloads, yumlog-package and yumlog-source directories should be new.</p>
    <CodeBlock language="powershell">{release.installCommand}</CodeBlock>
    <p>Review the <a href={release.installer}>versioned production installer</a> before running it.
      It creates user-local versioned binaries and preserves valid existing native configuration.
      Use <code>-NoUserIntegration</code> if you do not want integration shortcuts.</p>
    <h3>MSIX alternative</h3>
    <p>Check package identity, signature and publisher before installation.
      Never disable signature checks or trust a certificate merely to make installation succeed.
      The <a href={release.msixInstaller}>versioned MSIX installer</a> describes package checks.
      Native MSIX does not include the separate Paperboy, APIM and Widget Studio executables.</p>
  </>;
}
