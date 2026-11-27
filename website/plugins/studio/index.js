const fs = require('node:fs/promises');
const path = require('node:path');
const {loadRelease} = require('./release');

function readableMarkdown(text) {
  return text.replace(/^---\r?\n[\s\S]*?\r?\n---\r?\n/, '')
    .replace(/^import .+$/gm, '')
    .replace(/\[([^\]]+)\]\([^)]+\)/g, '$1')
    .replace(/^```[^\r\n]*|^#{1,6}\s+|[*`]/gm, '')
    .replace(/\s+/g, ' ').trim();
}

module.exports = function studioPlugin(context) {
  const {siteDir, siteConfig} = context;
  const assets = siteConfig.customFields.studio.assets;
  let searchIndex = [];
  return {
    name: 'yumlog-studio',
    getPathsToWatch: () => [path.join(siteDir, 'content', '*.mdx')],
    async loadContent() {
      const [release, articles] = await Promise.all([
        loadRelease(),
        Promise.all(assets.map(async asset => ({
          title: `${asset.title} Wiki deck`,
          url: `${siteConfig.baseUrl}wiki/${asset.id}/`,
          text: `${asset.summary} ${readableMarkdown(await fs.readFile(path.join(siteDir, 'content', `${asset.id}.mdx`), 'utf8'))} ${[asset.role, ...asset.inputs, ...asset.outputs, ...asset.actions].join(' ')}`,
        }))),
      ]);
      const installation = articles.find(article => article.url.endsWith('/settings/'));
      installation.text += ` Download Yumlog ${release.version}. Windows x64 ZIP, MSIX, SHA-256 checksum, release manifest. ${release.installCommand.replace(/\s+/g, ' ')}`;
      return {release, articles};
    },
    async contentLoaded({content, actions}) {
      actions.setGlobalData({release: content.release});
      for (const asset of assets) {
        actions.addRoute({
          path: `${siteConfig.baseUrl}wiki/${asset.id}/`,
          component: '@site/src/components/WikiPage',
          exact: true,
          modules: {section: await actions.createData(`${asset.id}.json`, JSON.stringify({id: asset.id}))},
        });
      }
    },
    async allContentLoaded({allContent}) {
      const docs = allContent['docusaurus-plugin-content-docs']?.default?.loadedVersions || [];
      const guides = docs.flatMap(version => version.docs).filter(doc => !doc.draft && !doc.unlisted);
      searchIndex = [
        ...allContent['yumlog-studio'].default.articles,
        ...await Promise.all(guides.map(async doc => ({
          title: doc.title,
          url: doc.permalink.endsWith('/') ? doc.permalink : `${doc.permalink}/`,
          text: readableMarkdown(await fs.readFile(path.resolve(siteDir, doc.source.replace(/^@site\//, '')), 'utf8')),
        }))),
      ];
    },
    async postBuild({outDir}) {
      await fs.writeFile(path.join(outDir, 'search-index.json'), JSON.stringify(searchIndex));
    },
  };
};
