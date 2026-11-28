# Studio wiki

The Docusaurus website publishes to `https://darbotlm.github.io/yumlog/`.
The product homepage and 66 `/wiki/<section>/<layer>/` routes reuse the Studio
section descriptors in the root `_config.yml` and source `content/*.mdx`
articles. Each card has its own rendered HTML, title and canonical URL. The top navigation keeps
Website and Wiki deck separate, with full-text search immediately after
Website and a shared Agent Card Size control on wiki pages.
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

Selecting a section opens its Title card. Its six authored layers are Title,
Overview, Instructions, Format and Outputs, Agents and Tools, SDK and Examples.
Front shows human-readable content. Back offers Adaptive Card 1.5, AgentCard,
History and Framework tabs; `?side=back&tab=agentcard` can link directly to one.
`/wiki/`, the former section-query URLs, `/wiki/<section>/`, old heading
anchors and `?face=paperboy` remain supported as redirects. Bare root visits
open the product website. These cards are documentation, not running agents. The website
does not contact desktop services, capture media or invoke tools.

Search loads the generated `search-index.json` from this site only when
used. It covers all eleven articles, generated installation commands, and
published developer guides. Queries stay in the browser. Arrow keys, Enter,
Escape and normal link navigation are supported. Agent Card Size defaults
to Wide; an explicit selection persists only in the current browser session
and does not change native Studio settings. Wide mode uses half the normal
top spacing above the card at each breakpoint.

Wiki cards fit the available viewport. Long front or back content flows into
measured content pages inside its authored layer. The Agent Card layer selector
always has exactly six entries; Previous page / Next page never create new
semantic layers. Text, lists, tables and code are not truncated. Resizing or
changing Agent Card Size recalculates only the content pages. Heading links and
keyboard focus reveal their corresponding page. Printing includes the current face's full content.
Short or narrow viewports replace the full section dock with compact section
and developer-guide selectors. Product pages and standalone developer guides
retain their normal document layout.

The selected section automatically expands its six cards in the wiki
sidebar. Expand all stays expanded when switching sections;
Collapse all and individual toggles are also available. Only the sidebar
scrolls when expanded content exceeds its height. Compact layouts expose the
same accordion through the Sections button without moving the Agent Card.

## Machine-readable cards

`cards/index.json` lists every layer, its canonical URL, exported JSON URLs
and SHA-256 digests. Per-layer exports under `cards/<section>/` include the
complete flipcard envelope, Adaptive Card 1.5 payload, AgentCard 3.1 descriptor,
source history and framework metadata. Schemas are served under `schemas/`.
The build validates every envelope and its nested card payloads with AJV;
unsupported Markdown constructs or unassigned headings fail rather than
silently dropping content. See `schemas/README.md` for pinned schema provenance.

The existing eleven articles remain the authored source. The card generator
assigns their named subsections to the fixed semantic layers; release-specific
installation content comes from the verified release metadata. Source history
is the latest five documentation/generator commits available in the checkout,
not a runtime event log. Actions fetches up to 50 commits and explicitly reports
shallow history. Source hashes, build revision and working-tree status distinguish
published source from local edits. Download descriptors do not grant tool access.

Product screenshots must be supplied and approved for publication. Do not
substitute annotated website screenshots or fabricated media for native app
captures. Use an isolated browser context for viewport QA; do not resize the
operator's browser.
