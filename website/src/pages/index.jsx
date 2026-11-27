import React, {useEffect, useRef, useState} from 'react';
import Layout from '@theme/Layout';
import Link from '@docusaurus/Link';
import useBaseUrl from '@docusaurus/useBaseUrl';
import useDocusaurusContext from '@docusaurus/useDocusaurusContext';
import {useHistory, useLocation} from '@docusaurus/router';
import {MDXProvider} from '@mdx-js/react';
import MDXComponents from '@theme/MDXComponents';
import Video from '../../content/video.mdx';
import Screenshot from '../../content/screenshot.mdx';
import Audio from '../../content/audio.mdx';
import Transcript from '../../content/transcript.mdx';
import Agents from '../../content/agents.mdx';
import Voices from '../../content/voices.mdx';
import Insights from '../../content/insights.mdx';
import Bitcube from '../../content/bitcube.mdx';
import Library from '../../content/library.mdx';
import Harvest from '../../content/harvest.mdx';
import Settings from '../../content/settings.mdx';

const articles = {video: Video, screenshot: Screenshot, audio: Audio, transcript: Transcript,
  agents: Agents, voices: Voices, insights: Insights, bitcube: Bitcube, library: Library,
  harvest: Harvest, settings: Settings};
const glyphs = {
  video: ['M3 6h12v12H3z', 'm15 10 6-4v12l-6-4'],
  image: ['M3 3h18v18H3z', 'm3 17 6-6 4 4 3-3 5 5', 'M16 7h.01'],
  audio: ['M3 9v6', 'M7 5v14', 'M12 2v20', 'M17 6v12', 'M21 10v4'],
  text: ['M5 2h10l4 4v16H5z', 'M9 10h6', 'M9 14h6', 'M9 18h4'],
  nodes: ['M4 4h5v5H4z', 'M15 15h5v5h-5z', 'M15 4h5v5h-5z', 'M6.5 9v8.5H15', 'M9 6.5h6'],
  mic: ['M9 5a3 3 0 0 1 6 0v7a3 3 0 0 1-6 0z', 'M5 10v2a7 7 0 0 0 14 0v-2', 'M12 19v3'],
  chart: ['M3 3v18h18', 'M7 17v-6', 'M12 17V6', 'M17 17V9'],
  cube: ['m12 2 9 5v10l-9 5-9-5V7z', 'm3 7 9 5 9-5', 'M12 12v10', 'm7.5 4.5 9 5'],
  books: ['M3 3h4v18H3z', 'M10 3h4v18h-4z', 'm17 3 4-1 3 18-4 1z'],
  box: ['M3 8h18v13H3z', 'M2 3h20v5H2z', 'M9 12h6'],
  sliders: ['M4 3v18', 'M12 3v18', 'M20 3v18', 'M1 8h6', 'M9 16h6', 'M17 8h6'],
};
function Glyph({name}) {
  return <svg className="wiki-glyph" viewBox="0 0 26 24" fill="none" stroke="currentColor"
    strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
    {(glyphs[name] || glyphs.video).map(d => <path d={d} key={d}/>)}
  </svg>;
}

export default function StudioWiki() {
  const {siteConfig} = useDocusaurusContext();
  const history = useHistory();
  const location = useLocation();
  const model = siteConfig.customFields.studio;
  const [selected, setSelected] = useState('video');
  const [face, setFace] = useState('wiki');
  const [query, setQuery] = useState('');
  const [size, setSize] = useState('studio');
  const [dockOpen, setDockOpen] = useState(false);
  const pageTitle = useRef(null);
  const root = useBaseUrl('/');
  const docBase = useBaseUrl('/docs/');
  const current = model.assets.find(a => a.id === selected) || model.assets[0];
  const Article = articles[current.id];
  const visible = model.assets.filter(a =>
    [a.title, a.summary, a.role, ...a.actions, ...a.inputs, ...a.outputs].join(' ').toLowerCase().includes(query.toLowerCase()));

  useEffect(() => {
    const sync = () => {
      const search = new URLSearchParams(location.search);
      const legacy = new URLSearchParams(location.hash.slice(1));
      const aliases = {capture: 'video', gallery: 'screenshot', evidence: 'insights',
        protocols: 'agents', reasoning: 'voices', install: 'settings', widgets: 'library'};
      const id = search.get('section') || legacy.get('section') || 'video';
      setSelected(articles[id] ? id : aliases[id] || 'video');
      const next = search.get('face');
      setFace(['tool', 'paperboy', 'wiki'].includes(next) ? next : 'wiki');
    };
    sync();
  }, [location.search, location.hash]);

  const navigate = (id, nextFace = 'wiki', focus = true) => {
    setSelected(id); setFace(nextFace); setDockOpen(false);
    const url = new URL(root, window.location.origin);
    url.searchParams.set('section', id);
    url.searchParams.set('face', nextFace);
    history.push(url.pathname + url.search);
    if (focus) requestAnimationFrame(() => pageTitle.current?.focus({preventScroll: true}));
  };
  const sectionHref = id => `${root}?section=${id}&face=wiki`;
  const follow = (event, id) => {
    if (event.button !== 0 || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) return;
    event.preventDefault(); navigate(id);
  };
  const contract = {
    schema: model.schema, id: `yumlog.ui.${current.id}`, title: current.title,
    role: current.role, inputs: current.inputs, outputs: current.outputs,
    actions: current.actions, source: model.sourceBase + current.source,
    evidence: 'Documentation descriptor, not a live AgentCard or runtime result.',
  };
  return <Layout title={`${current.title} wiki deck`} description="A full-width wiki deck for every Yumlog Studio card."
    wrapperClassName="studio-wiki-page">
    <div className="studio-wiki-shell" data-size={size}>
      <aside className="wiki-dock" aria-label="Studio wiki sections" data-open={dockOpen}>
        <div className="dock-label"><span>WIKI DECK</span><span>11 SECTIONS</span></div>
        <button className="dock-toggle" type="button" aria-expanded={dockOpen}
          aria-controls="wiki-dock-contents" onClick={() => setDockOpen(!dockOpen)}>Browse Studio sections</button>
        <div id="wiki-dock-contents" className="dock-contents">
          <label className="dock-search">Find a section
            <input type="search" value={query} onChange={e => setQuery(e.target.value)}
              placeholder="Audio, transcript, agents..." />
          </label>
          <nav aria-label="Choose a Studio wiki">
            {visible.map(asset => <a key={asset.id} href={sectionHref(asset.id)}
              aria-current={current.id === asset.id ? 'page' : undefined}
              onClick={e => follow(e, asset.id)}>
              <Glyph name={asset.glyph}/><span>{asset.title}</span>
              <span className="dock-count">{String(model.assets.indexOf(asset) + 1).padStart(2, '0')}</span>
            </a>)}
            {!visible.length && <p className="dock-empty">No section matches. Try a tool or output name.</p>}
          </nav>
          <div className="dock-guides"><span className="dock-label">DEVELOPER GUIDES</span>
            <Link to={`${docBase}SDK-FRAMEWORK`}>SDK &amp; framework</Link>
            <Link to={`${docBase}AGENT-PLUGIN`}>Agent Plugin &amp; MCP</Link>
            <Link to={`${docBase}NATIVE-CLI`}>Native CLI reference</Link>
          </div>
          <a className="install-studio" href={sectionHref('settings')} onClick={e => follow(e, 'settings')}>Install Yumlog 0.7.1</a>
          <p className="dock-note">Source-linked documentation.<br/>No desktop connection.</p>
        </div>
      </aside>
      <main className="wiki-workspace" id="wiki-workspace">
        <div className="wiki-workspace-heading">
          <div><p className="wiki-eyebrow">YUMLOG STUDIO / DOCUMENTATION</p>
            <h1 ref={pageTitle} tabIndex="-1">{current.title}</h1><p>{current.summary}</p></div>
          <label className="wiki-size">Card size <select value={size} onChange={e => setSize(e.target.value)}>
            <option value="compact">Compact</option><option value="studio">Studio</option><option value="wide">Wide</option>
          </select></label>
        </div>
        <article className="wiki-card" aria-label={`${current.title} Studio card`} data-face={face}>
          <header className="wiki-card-header">
            <div className="wiki-card-name"><Glyph name={current.glyph}/><h2>{current.title}</h2></div>
            <div className="face-controls" role="group" aria-label="Studio card face">
              <button type="button" aria-pressed={face === 'tool'} aria-controls="wiki-card-content"
                onClick={() => navigate(current.id, 'tool', false)}>Tool view</button>
              <button type="button" aria-pressed={face === 'paperboy'} aria-controls="wiki-card-content"
                onClick={() => navigate(current.id, 'paperboy', false)}>Paperboy details</button>
              <button type="button" aria-pressed={face === 'wiki'} aria-controls="wiki-card-content"
                onClick={() => navigate(current.id, 'wiki', false)}>Wiki deck</button>
            </div>
          </header>
          <section id="wiki-card-content" className="wiki-face" aria-label={`${current.title} ${face === 'wiki' ? 'wiki deck' : face === 'paperboy' ? 'Paperboy details' : 'tool overview'}`}>
            {face === 'wiki' && <div className="wiki-article markdown">
              <div className="wiki-face-label">BACK OF CARD / WIKI DECK <span>GUIDE, EXAMPLES &amp; INTEGRATION</span></div>
              <MDXProvider components={MDXComponents}><Article/></MDXProvider>
            </div>}
            {face === 'tool' && <div className="wiki-overview">
              <div className="overview-symbol"><Glyph name={current.glyph}/><span>{current.role}</span></div>
              <h3>{current.summary}</h3>
              <p>This is the capability front of the {current.title} card. The native app performs these operations; this website explains them.</p>
              <div className="contract-grid">{[['Inputs', current.inputs], ['Outputs', current.outputs], ['Actions', current.actions]].map(([label, items]) =>
                <div key={label}><h4>{label}</h4><ul>{items.map(x => <li key={x}><code>{x}</code></li>)}</ul></div>)}</div>
              <button className="wiki-primary" type="button" onClick={() => navigate(current.id)}>Flip to Wiki deck</button>
            </div>}
            {face === 'paperboy' && <div className="wiki-overview">
              <div className="wiki-face-label">BACK OF CARD / PAPERBOY DETAILS</div>
              <h3>{current.title} contract</h3>
              <p>Inspect the source-linked descriptor, then open the Wiki deck for instructions. This is not a fabricated capture, analysis, or delivery receipt.</p>
              <dl className="contract-fields"><dt>Role</dt><dd>{current.role}</dd>
                <dt>Inputs</dt><dd>{current.inputs.join(', ')}</dd>
                <dt>Outputs</dt><dd>{current.outputs.join(', ')}</dd>
                <dt>Actions</dt><dd>{current.actions.join(', ')}</dd></dl>
              <details><summary>Documentation card JSON</summary><pre>{JSON.stringify(contract, null, 2)}</pre></details>
              <p><a href={model.sourceBase + current.source}>Read the implementation</a></p>
              <button className="wiki-primary" type="button" onClick={() => navigate(current.id)}>Open Wiki deck</button>
            </div>}
          </section>
          <nav className="wiki-bottom-tabs" aria-label="Studio card sections">
            {model.assets.map(asset => <a key={asset.id} href={sectionHref(asset.id)} onClick={e => follow(e, asset.id)}
              aria-current={current.id === asset.id ? 'page' : undefined}><Glyph name={asset.glyph}/><span>{asset.title}</span></a>)}
          </nav>
        </article>
        <footer className="wiki-workspace-footer"><span role="status">{current.title} / {face === 'wiki' ? 'Wiki deck' : face === 'paperboy' ? 'Paperboy details' : 'Tool view'}</span>
          <span>Documentation, not live telemetry</span></footer>
      </main>
    </div>
  </Layout>;
}
