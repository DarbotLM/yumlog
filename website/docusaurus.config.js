const fs = require('node:fs');
const path = require('node:path');
const YAML = require('yaml');

const root = path.resolve(__dirname, '..');
const wiki = YAML.parse(fs.readFileSync(path.join(root, '_config.yml'), 'utf8')).wiki;
const expected = ['video', 'screenshot', 'audio', 'transcript', 'agents', 'voices', 'insights', 'bitcube', 'library', 'harvest', 'settings'];
if (JSON.stringify(wiki.assets.map(a => a.id)) !== JSON.stringify(expected)) {
  throw new Error('The wiki must expose the 11 Studio sections in their declared order.');
}

// Keep source/component references out of Docusaurus's client-side route space.
function sourceLinks() {
  return tree => {
    const visit = node => {
      if (node.type === 'link' && /^(?:\.\.\/)+(?:apps|launchers|config|Skills)\//.test(node.url)) {
        node.url = 'https://github.com/DarbotLM/yumlog/blob/main/' + node.url.replace(/^(?:\.\.\/)+/, '');
      }
      (node.children || []).forEach(visit);
    };
    visit(tree);
  };
}

module.exports = {
  title: 'Yumlog Studio',
  tagline: 'The Studio, explained. Source-linked guides for every card.',
  url: 'https://darbotlm.github.io',
  baseUrl: '/yumlog/',
  organizationName: 'DarbotLM',
  projectName: 'yumlog',
  favicon: 'yls-header-icon.svg',
  trailingSlash: true,
  onBrokenLinks: 'throw',
  markdown: { format: 'detect', hooks: { onBrokenMarkdownLinks: 'throw' } },
  staticDirectories: ['../apps/Yumlog.Native/Studio/branding'],
  customFields: { studio: { schema: wiki.schema, sourceBase: wiki.sourceBase, assets: wiki.assets } },
  presets: [['classic', {
    docs: {
      path: '../docs',
      routeBasePath: 'docs',
      sidebarPath: false,
      beforeDefaultRemarkPlugins: [sourceLinks],
      editUrl: ({docPath}) => `https://github.com/DarbotLM/yumlog/edit/main/docs/${docPath}`,
    },
    blog: false,
    theme: { customCss: './src/css/studio.css' },
  }]],
  plugins: ['./plugins/studio', ['@docusaurus/plugin-client-redirects', {
    fromExtensions: ['html'],
  }]],
  themeConfig: {
    colorMode: { defaultMode: 'dark', disableSwitch: true, respectPrefersColorScheme: false },
    navbar: {
      title: 'Yumlog Studio',
      logo: { src: 'yls-header-icon.svg', alt: 'YLS' },
      items: [
        { to: '/wiki/', label: 'Wiki deck', position: 'left' },
        { to: '/', label: 'Website', position: 'left', exact: true },
        { type: 'search', position: 'left', className: 'site-search-container' },
        { type: 'custom-card-size', position: 'right' },
        { href: 'https://github.com/DarbotLM/yumlog/releases/latest', label: 'Install Studio', position: 'right' },
        { href: 'https://github.com/DarbotLM/yumlog', label: 'GitHub', position: 'right' },
      ],
    },
    prism: { additionalLanguages: ['powershell', 'csharp', 'fsharp', 'json'] },
    footer: { style: 'dark', copyright: 'Yumlog Studio documentation. No local services are contacted by this website.' },
  },
};
