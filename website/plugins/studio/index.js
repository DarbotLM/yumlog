const fs = require('node:fs/promises');
const path = require('node:path');
const {loadRelease} = require('./release');
const {buildSemanticCards} = require('./semantic-cards');

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
  let exports = {};
  return {
    name: 'yumlog-studio',
    getPathsToWatch: () => [
      path.join(siteDir, 'content', '*.mdx'), path.join(siteDir, 'schemas', '*.json'),
      path.join(siteDir, '../config/agentcards/ac3.1/agent-card.schema.json'),
    ],
    async loadContent() {
      const release = await loadRelease();
      const cards = await buildSemanticCards({siteDir, siteConfig, assets, release});
      exports = cards.files;
      return {release, cards};
    },
    async contentLoaded({content, actions}) {
      actions.setGlobalData({
        release: content.release, layerDefinitions: content.cards.layerDefinitions,
        schemas: content.cards.schemas, catalogUrl: `${siteConfig.baseUrl}cards/index.json`,
      });
      for (const model of content.cards.models) {
        const section = await actions.createData(`${model.id}.json`, JSON.stringify(model));
        actions.addRoute({
          path: `${siteConfig.baseUrl}wiki/${model.id}/`,
          component: '@site/src/components/WikiPage',
          exact: true,
          modules: {section},
        });
        for (const layer of model.layers) actions.addRoute({
          path: `${siteConfig.baseUrl}wiki/${model.id}/${layer.id}/`,
          component: '@site/src/components/WikiPage', exact: true,
          modules: {section, layer: await actions.createData(`${model.id}-${layer.id}.json`, JSON.stringify({id: layer.id}))},
        });
      }
    },
    async allContentLoaded({allContent}) {
      const docs = allContent['docusaurus-plugin-content-docs']?.default?.loadedVersions || [];
      const guides = docs.flatMap(version => version.docs).filter(doc => !doc.draft && !doc.unlisted);
      searchIndex = [
        ...allContent['yumlog-studio'].default.cards.searchEntries,
        ...await Promise.all(guides.map(async doc => ({
          title: doc.title,
          url: doc.permalink.endsWith('/') ? doc.permalink : `${doc.permalink}/`,
          text: readableMarkdown(await fs.readFile(path.resolve(siteDir, doc.source.replace(/^@site\//, '')), 'utf8')),
        }))),
      ];
    },
    async postBuild({outDir}) {
      await fs.writeFile(path.join(outDir, 'search-index.json'), JSON.stringify(searchIndex));
      for (const [file, content] of Object.entries(exports)) {
        const target = path.join(outDir, file);
        await fs.mkdir(path.dirname(target), {recursive: true});
        await fs.writeFile(target, content);
      }
      await fs.mkdir(path.join(outDir, 'schemas'), {recursive: true});
      for (const file of ['flipcard.schema.json', 'adaptive-card-1.5.schema.json', 'AdaptiveCards-LICENSE.txt']) {
        await fs.copyFile(path.join(siteDir, 'schemas', file), path.join(outDir, 'schemas', file));
      }
      await fs.copyFile(path.join(siteDir, '../config/agentcards/ac3.1/agent-card.schema.json'),
        path.join(outDir, 'schemas/agent-card-3.1.schema.json'));
    },
  };
};
