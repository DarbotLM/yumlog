# Studio wiki

The Docusaurus website publishes to `https://darbotlm.github.io/yumlog/`.
The product homepage and `/wiki/` reuse the Studio section descriptors in the
root `_config.yml`; the eleven `content/*.mdx` articles form the full-width
Wiki deck. The top navigation keeps Website and Wiki deck separate.
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

The lockfile uses portable `registry.npmjs.org` package URLs and preserves
package integrity hashes. npm maps this canonical registry to the configured
proxy during installation. Do not disable `allow-remote` or `allow-git` to
repair a proxy whose metadata advertises a different tarball prefix. Use
npm's documented `replace-registry-host` full-prefix mapping when generating
a new lockfile, then preserve portable registry URLs for public CI.

## Publication

`.github/workflows/pages.yml` builds pull requests without deployment.
Only trusted main builds or a main workflow dispatch can deploy through the
`github-pages` environment. The uploaded artifact is **only** `website/build`;
release packages, local media, models and native build outputs are excluded.
The checkout step does not download Git LFS payloads.

Selecting a dock or bottom tab opens `/wiki/?section=<id>&face=wiki`.
Existing root `?section=...` and legacy `#view=...&section=...` links redirect
to the wiki while preserving their query and fragment. Bare root visits open
the product website. The other
faces are documentation overviews, not live application controls. The website
does not contact desktop services, capture media or invoke tools.
