const fs = require('node:fs/promises');
const path = require('node:path');
const {execFileSync} = require('node:child_process');
const {createHash} = require('node:crypto');
const Slugger = require('github-slugger');
const Ajv = require('ajv');
const addFormats = require('ajv-formats');

const layerDefinitions = [
  {id: 'title', value: 'Title'},
  {id: 'overview', value: 'Overview'},
  {id: 'instructions', value: 'Instructions'},
  {id: 'format-outputs', value: 'Format and Outputs'},
  {id: 'agents-tools', value: 'Agents and Tools'},
  {id: 'sdk-examples', value: 'SDK and Examples'},
];
const headingLayers = {
  Instructions: 'instructions',
  'Tools and outputs': 'format-outputs',
  'Tools and artifacts': 'format-outputs',
  'Tools and workspace configuration': 'format-outputs',
  'Four layers': 'format-outputs',
  'Tools and integration': 'agents-tools',
  'Tools, agents and SDK': 'agents-tools',
  'Agents, SDK and Paperboy': 'agents-tools',
  'Agents, SDK and privacy': 'agents-tools',
  'SDK, framework and Paperboy': 'sdk-examples',
  'Release downloads': 'format-outputs',
  'Verify and install the ZIP': 'instructions',
  'Installation example': 'sdk-examples',
  'MSIX alternative': 'instructions',
};
const text = value => ({type: 'text', value});
const paragraph = value => ({type: 'paragraph', children: [text(value)]});
const heading = (id, value) => ({type: 'heading', id, depth: 3, children: [text(value)]});
const plain = nodes => nodes.map(node => node.value || (node.children ? plain(node.children) : '')).join('');
const hash = value => createHash('sha256').update(value).digest('hex');

function releaseMarkdown(release) {
  return `### Release downloads

| Download | Contents | Bytes |
|---|---|---|
| [Windows x64 ZIP](${release.zip.url}) | ${release.componentCount} application components | ${release.zip.size} |
| [ZIP checksum](${release.checksum.url}) | SHA-256 | ${release.checksum.size} |
| [Release manifest](${release.manifest.url}) | Component versions and digests | ${release.manifest.size} |
| [Native Studio MSIX](${release.msix.url}) | Version ${release.msixVersion} | ${release.msix.size} |

[Release ${release.version}](${release.url}). The checksum and manifest bytes match their GitHub digests; the ZIP checksum matches its published digest. The website build does not revalidate application binaries or Windows signatures.

### Verify and install the ZIP

Use the matching release's commands on the SDK and Examples card. Run them yourself in a directory with sufficient space; downloads, yumlog-package and yumlog-source should be new directories. Verify the ZIP before extraction.

Review the [versioned production installer](${release.installer}). It creates user-local versioned binaries and preserves valid existing native configuration. Use \`-NoUserIntegration\` if you do not want shortcuts.

### Installation example

\`\`\`powershell
${release.installCommand}
\`\`\`

### MSIX alternative

Check package identity, signature and publisher before installation. Never disable signature checks or trust a certificate just to make installation succeed. The [versioned MSIX installer](${release.msixInstaller}) describes package checks. Native MSIX does not include the separate Paperboy, APIM and Widget Studio executables.
`;
}

function normalizeUrl(value, siteUrl) {
  const url = new URL(value, siteUrl);
  if (!['http:', 'https:'].includes(url.protocol)) throw new Error(`Unsupported card link protocol: ${url.protocol}`);
  return url.href;
}

function inline(nodes, siteUrl) {
  return nodes.map(node => {
    if (['text', 'inlineCode'].includes(node.type)) return {type: node.type, value: node.value};
    if (node.type === 'break') return {type: 'break'};
    if (['strong', 'emphasis', 'delete', 'link'].includes(node.type)) {
      return {type: node.type, children: inline(node.children, siteUrl),
        ...(node.type === 'link' ? {url: normalizeUrl(node.url, siteUrl)} : {})};
    }
    throw new Error(`Unsupported inline card content: ${node.type}`);
  });
}

function blocks(nodes, slugger, siteUrl) {
  return nodes.map(node => {
    if (node.type === 'heading') return {
      type: 'heading', depth: node.depth, id: slugger.slug(plain(node.children)), children: inline(node.children, siteUrl),
    };
    if (node.type === 'paragraph') return {type: 'paragraph', children: inline(node.children, siteUrl)};
    if (node.type === 'code') return {type: 'code', language: node.lang || 'text', value: node.value};
    if (node.type === 'list') return {
      type: 'list', ordered: Boolean(node.ordered), start: node.start || 1,
      items: node.children.map(item => blocks(item.children, slugger, siteUrl)),
    };
    if (node.type === 'table') return {
      type: 'table', rows: node.children.map(row => row.children.map(cell => inline(cell.children, siteUrl))),
    };
    throw new Error(`Unsupported block in semantic card: ${node.type}`);
  });
}

function markdownInline(nodes) {
  return nodes.map(node => {
    if (node.type === 'link') return `[${markdownInline(node.children)}](${node.url})`;
    if (node.type === 'strong') return `**${markdownInline(node.children)}**`;
    if (node.type === 'emphasis') return `*${markdownInline(node.children)}*`;
    if (node.type === 'inlineCode') return `\`${node.value}\``;
    if (node.type === 'break') return '\n';
    return node.value || markdownInline(node.children || []);
  }).join('');
}

function blockText(block) {
  if (block.children) return plain(block.children);
  if (block.type === 'code') return block.value;
  if (block.type === 'list') return block.items.map(items => items.map(blockText).join(' ')).join('\n');
  if (block.type === 'table') return block.rows.map(row => row.map(plain).join(' | ')).join('\n');
  return '';
}

function adaptiveBody(content) {
  return content.flatMap(block => {
    if (block.type === 'table') {
      const headers = block.rows[0].map(plain);
      return [{type: 'FactSet', facts: block.rows.slice(1).map(row => ({
        title: plain(row[0]),
        value: row.slice(1).map((cell, index) => `${headers[index + 1]}: ${markdownInline(cell)}`).join('\n'),
      }))}];
    }
    if (block.type === 'list') return block.items.map((item, index) => ({
      type: 'TextBlock', wrap: true,
      text: `${block.ordered ? `${block.start + index}.` : '-'} ${item.map(blockText).join('\n')}`,
    }));
    return [{
      type: 'TextBlock', wrap: true,
      text: block.type === 'code' ? block.value : markdownInline(block.children),
      ...(block.type === 'heading' ? {weight: 'Bolder', size: 'Medium'} : {}),
      ...(block.type === 'code' ? {fontType: 'Monospace'} : {}),
    }];
  });
}

async function buildSemanticCards({siteDir, siteConfig, assets, release}) {
  const [{unified}, {default: remarkParse}, {default: remarkGfm}] = await Promise.all([
    import('unified'), import('remark-parse'), import('remark-gfm'),
  ]);
  const parser = unified().use(remarkParse).use(remarkGfm);
  const repoRoot = path.resolve(siteDir, '..');
  const git = args => execFileSync('git', args, {cwd: repoRoot, encoding: 'utf8'}).trim();
  const buildCommit = git(['rev-parse', 'HEAD']);
  const shallow = git(['rev-parse', '--is-shallow-repository']) === 'true';
  const siteUrl = siteConfig.url + siteConfig.baseUrl;
  const schemas = {
    flipcard: `${siteUrl}schemas/flipcard.schema.json`,
    adaptiveCard: `${siteUrl}schemas/adaptive-card-1.5.schema.json`,
    agentCard: `${siteUrl}schemas/agent-card-3.1.schema.json`,
  };
  const adaptiveBytes = await fs.readFile(path.join(siteDir, 'schemas/adaptive-card-1.5.schema.json'));
  if (hash(adaptiveBytes) !== '32c91fb285180a266a55b3475dd0396e63fe079311714d3a14963b41f5e17920') {
    throw new Error('Adaptive Card 1.5 schema does not match its pinned upstream bytes.');
  }
  const adaptiveSchema = JSON.parse(adaptiveBytes.toString('utf8'));
  const agentSchema = JSON.parse(await fs.readFile(path.join(repoRoot, 'config/agentcards/ac3.1/agent-card.schema.json'), 'utf8'));
  const envelopeSchema = JSON.parse(await fs.readFile(path.join(siteDir, 'schemas/flipcard.schema.json'), 'utf8'));
  const ajv = new Ajv({strict: false, allErrors: true});
  addFormats(ajv);
  ajv.addMetaSchema(require('ajv/dist/refs/json-schema-draft-06.json'));
  const adaptiveDefinition = {...adaptiveSchema};
  delete adaptiveDefinition.id;
  ajv.addSchema(adaptiveDefinition, 'http://adaptivecards.io/schemas/adaptive-card.json');
  ajv.addSchema(agentSchema);
  const validate = ajv.compile(envelopeSchema);
  const models = [];
  const files = {};
  const searchEntries = [];

  for (const asset of assets) {
    const sourcePath = `website/content/${asset.id}.mdx`;
    const original = await fs.readFile(path.join(repoRoot, sourcePath), 'utf8');
    const markdown = original.replace(/^import .+;\r?\n/gm, '').replace('<ReleaseInstall />', releaseMarkdown(release));
    const parsed = blocks(parser.parse(markdown).children, new Slugger(), siteUrl);
    const groups = Object.fromEntries(layerDefinitions.map(layer => [layer.id, []]));
    const anchors = {};
    let target = 'overview';
    for (const block of parsed) {
      if (block.type === 'heading') {
        const title = plain(block.children);
        if (asset.id === 'settings' && title === 'Release downloads') block.id = 'download-yumlog';
        if (asset.id === 'settings' && title === 'Verify and install the ZIP') block.id = 'verify-and-install';
        target = block.depth === 2 ? 'overview' : title.startsWith('Example') ? 'sdk-examples' : headingLayers[title];
        if (!target) throw new Error(`Assign an authored card layer for ${asset.id}: ${title}`);
        anchors[block.id] = target;
      }
      groups[target].push(block);
    }
    if (asset.id === 'settings') Object.assign(anchors, {
      'download-yumlog': 'format-outputs', 'verify-and-install': 'instructions', 'msix-alternative': 'instructions',
    });
    groups.title = [
      heading('title', asset.title), paragraph(asset.summary),
      paragraph('Choose a dedicated card below. Each has a readable front and validated, downloadable machine-readable contracts on the back.'),
      {type: 'list', ordered: false, start: 1, items: layerDefinitions.slice(1).map(layer => [{
        type: 'paragraph', children: [{type: 'link', url: `${siteUrl}wiki/${asset.id}/${layer.id}/`, children: [text(layer.value)]}],
      }])},
    ];
    groups['format-outputs'].push(
      heading('declared-inputs-and-outputs', 'Declared inputs and outputs'),
      {type: 'table', rows: [
        [[text('Direction')], [text('Fields')]],
        [[text('Inputs')], [text(asset.inputs.join(', '))]],
        [[text('Outputs')], [text(asset.outputs.join(', '))]],
      ]},
      paragraph('These are the native Studio declaration names. A declared output is not evidence that an operation ran successfully.'),
    );
    anchors['declared-inputs-and-outputs'] = 'format-outputs';
    groups['agents-tools'].push(
      heading('documented-native-actions', 'Documented native actions'),
      paragraph(asset.actions.join(', ')),
      paragraph('Check availability and obtain appropriate authorization in the native application. The website and exported documentation cards do not execute these actions or grant permissions.'),
    );
    anchors['documented-native-actions'] = 'agents-tools';
    groups['sdk-examples'].push(
      paragraph('Use versioned evidence and explicit integration boundaries. Read the reference guides before adapting these examples.'),
      {type: 'paragraph', children: [
        {type: 'link', url: `${siteUrl}docs/SDK-FRAMEWORK/`, children: [text('SDK and framework')]}, text(' | '),
        {type: 'link', url: `${siteUrl}docs/NATIVE-CLI/`, children: [text('Native CLI')]}, text(' | '),
        {type: 'link', url: `${siteUrl}docs/AGENT-PLUGIN/`, children: [text('Agent Plugin and MCP')]},
      ]},
    );
    const historyPaths = [sourcePath, '_config.yml', 'website/plugins/studio/semantic-cards.js', 'website/schemas/flipcard.schema.json'];
    const log = git(['log', '-5', '--format=%H%x09%cI%x09%s', '--', ...historyPaths]);
    const history = {
      scope: 'Most recent documentation/card-generator source commits, not runtime events. This is a bounded history, not a complete audit trail.',
      limit: 5, shallow,
      entries: log ? log.split('\n').map(line => {
        const [commit, date, ...subject] = line.split('\t');
        return {commit, date, subject: subject.join('\t'), url: `https://github.com/DarbotLM/yumlog/commit/${commit}`};
      }) : [],
    };
    const source = {
      path: sourcePath, sha256: hash(original), buildCommit,
      workingTreeModified: Boolean(git(['status', '--porcelain', '--', ...historyPaths])),
      releaseVersion: release.version,
    };
    const model = {id: asset.id, title: asset.title, summary: asset.summary, anchors, layers: []};
    for (const layer of layerDefinitions) {
      const content = groups[layer.id];
      if (!content.length) throw new Error(`Missing authored content for ${asset.id}/${layer.id}`);
      const id = `yumlog.wiki.${asset.id}.${layer.id}`;
      const url = `${siteUrl}wiki/${asset.id}/${layer.id}/`;
      const name = `${asset.title}: ${layer.value}`;
      const adaptiveCard = {
        $schema: 'http://adaptivecards.io/schemas/adaptive-card.json', type: 'AdaptiveCard', version: '1.5',
        body: [{type: 'TextBlock', text: name, size: 'Large', weight: 'Bolder', wrap: true}, ...adaptiveBody(content)],
        actions: [{type: 'Action.OpenUrl', title: 'Open this documentation card', url}],
      };
      const agentCard = {
        kind: 'CopilotStudioAgentCard', schemaVersion: '3.1', name, id,
        description: `Documentation descriptor for ${name}. Not a deployed agent.`,
        language: 'en-US',
        knowledge: [{kind: 'PublicWebsite', name, url, description: `Source-linked content for the ${layer.value} documentation card.`}],
        instructions: 'Use the cited Yumlog documentation to explain this layer. Preserve provenance and limitations. Do not claim a native operation ran, fabricate observations, or infer permission from this descriptor.',
        metadata: {documentationOnly: 'true', layerUrl: url, adaptiveCardVersion: '1.5', sourcePath,
          flipcardUrl: `${siteUrl}cards/${asset.id}/${layer.id}.flipcard.json`},
      };
      const framework = {
        documentationOnly: true, adaptiveCardVersion: '1.5', agentCardVersion: '3.1',
        inputs: asset.inputs, outputs: asset.outputs, documentedActions: asset.actions,
        references: [`https://github.com/DarbotLM/yumlog/blob/main/${asset.source}`,
          `${siteUrl}docs/SDK-FRAMEWORK/`, `${siteUrl}docs/AGENT-PLUGIN/`, ...Object.values(schemas)],
      };
      const card = {
        kind: 'YumlogWikiFlipCard', schemaVersion: '1.0', id, section: asset.id, layer: layer.id, name, url,
        front: {title: name, blocks: content}, back: {adaptiveCard, agentCard, history, framework}, source,
      };
      if (adaptiveCard.version !== '1.5' || !validate(card)) {
        throw new Error(`Invalid card ${id}: ${ajv.errorsText(validate.errors, {separator: '\n'})}`);
      }
      const exports = {flipcard: card, adaptiveCard, agentCard, history, framework};
      const downloads = {};
      for (const [type, value] of Object.entries(exports)) {
        const file = `cards/${asset.id}/${layer.id}.${type}.json`;
        const bytes = JSON.stringify(value, null, 2) + '\n';
        files[file] = bytes;
        downloads[type] = {url: siteUrl + file, sha256: hash(bytes)};
      }
      model.layers.push({id: layer.id, value: layer.value, card, downloads});
      searchEntries.push({title: name, url: new URL(url).pathname, text: content.map(blockText).join(' ')});
    }
    models.push(model);
  }
  const catalog = {
    kind: 'YumlogWikiCardCatalog', schemaVersion: '1.0', schemas, sourceCommit: buildCommit,
    documentationOnly: true,
    sections: models.map(model => ({
      id: model.id, title: model.title,
      layers: model.layers.map(layer => ({id: layer.id, title: layer.value, url: layer.card.url, exports: layer.downloads})),
    })),
  };
  files['cards/index.json'] = JSON.stringify(catalog, null, 2) + '\n';
  return {models, files, catalog, searchEntries, layerDefinitions, schemas};
}

module.exports = {buildSemanticCards, layerDefinitions};
