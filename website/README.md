# Studio wiki

The Docusaurus website publishes to `https://darbotlm.github.io/yumlog/`.
The product homepage and eleven `/wiki/<section>/` routes reuse the Studio
section descriptors in the root `_config.yml`. Each `content/*.mdx` article
has its own rendered HTML, title and canonical URL. The top navigation keeps
Website and Wiki deck separate, with full-text search immediately after
Website and a shared Card size control on wiki pages.
Root `docs/` files remain developer guides, including redirects from their
previous `.html` URLs. Root Jekyll templates are not used by this build.

## Build

Use Node 24.15 or newer within the package's declared range and npm 12.2.3.
The Actions workflow pins Node 24 and npm 12.2.3.

```powershell
Set-Location .\website
npm ci --ignore-scripts
npm run build
```

No install lifecycle scripts are needed. The production build rejects broken
internal links. Source links outside the docs directory are rewritten to
GitHub before Markdown link validation.

The build reads the latest stable release from GitHub. It verifies the
checksum and manifest asset bytes against their GitHub digests, checks the
manifest version, and matches the ZIP checksum to its GitHub digest. Missing
assets, malformed metadata, network failures and mismatches fail the build.
The large ZIP/MSIX binaries and Windows signatures are not revalidated.
`GITHUB_TOKEN` is optional locally and is supplied to the build by Actions;
it is sent only to the GitHub metadata API, never included in generated data.

Three overrides pin patched transitive dependencies: tinypool 2.1.2,
serialize-javascript 7.1.3, and sockjs's uuid 11.1.1. These exceed the parent
packages' declared major ranges, so production SSG/bundling and the sockjs
CommonJS UUID consumer must remain compatible when updating the lockfile.
Remove overrides only when upstream ranges include fixed versions.

The lockfile uses portable `registry.npmjs.org` package URLs and preserves
package integrity hashes. npm maps this canonical registry to the configured
proxy during installation. Do not disable `allow-remote` or `allow-git` to
repair a proxy whose metadata advertises a different tarball prefix. Use
npm's documented `replace-registry-host` full-prefix mapping when generating
a new lockfile, then preserve portable registry URLs for public CI.

## Publication

`.github/workflows/pages.yml` builds pull requests without deployment.
Only trusted main builds, main workflow dispatches, or release publication
and edit events can deploy through the `github-pages` environment. Release
events build the current main website, not the release tag's older source.
The uploaded artifact is **only** `website/build`;
release packages, local media, models and native build outputs are excluded.
The checkout step does not download Git LFS payloads.

Selecting a dock or bottom tab opens `/wiki/<id>/`. The optional `face`
query selects Tool view or Paperboy details. `/wiki/`, the former section-query
URLs at both root and `/wiki/`, and legacy section hashes redirect to the
canonical section URL. Bare root visits open the product website. The other
faces are documentation overviews, not live application controls. The website
does not contact desktop services, capture media or invoke tools.

Search loads the generated `search-index.json` from this site only when
used. It covers all eleven articles, generated installation commands, and
published developer guides. Queries stay in the browser. Arrow keys, Enter,
Escape and normal link navigation are supported. Card size persists only in
the current browser session and does not change native Studio settings.

Product screenshots must be supplied and approved for publication. Do not
substitute annotated website screenshots or fabricated media for native app
captures. Use an isolated browser context for viewport QA; do not resize the
operator's browser.
