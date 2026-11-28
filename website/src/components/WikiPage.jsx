import React, {useEffect, useRef, useState} from 'react';
import Layout from '@theme/Layout';
import useBaseUrl from '@docusaurus/useBaseUrl';
import useDocusaurusContext from '@docusaurus/useDocusaurusContext';
import {useHistory, useLocation} from '@docusaurus/router';
import {MDXProvider} from '@mdx-js/react';
import MDXComponents from '@theme/MDXComponents';
import Glyph from './StudioGlyph';
import {useWikiPreferences} from './WikiPreferences';
import CardLayers from './CardLayers';
import WikiSidebar from './WikiSidebar';
import {releaseSections} from './ReleaseInstall';
import Video, {toc as videoSections} from '../../content/video.mdx';
import Screenshot, {toc as screenshotSections} from '../../content/screenshot.mdx';
import Audio, {toc as audioSections} from '../../content/audio.mdx';
import Transcript, {toc as transcriptSections} from '../../content/transcript.mdx';
import Agents, {toc as agentsSections} from '../../content/agents.mdx';
import Voices, {toc as voicesSections} from '../../content/voices.mdx';
import Insights, {toc as insightsSections} from '../../content/insights.mdx';
import Bitcube, {toc as bitcubeSections} from '../../content/bitcube.mdx';
import Library, {toc as librarySections} from '../../content/library.mdx';
import Harvest, {toc as harvestSections} from '../../content/harvest.mdx';
import Settings, {toc as settingsSections} from '../../content/settings.mdx';

const articles = {video: Video, screenshot: Screenshot, audio: Audio, transcript: Transcript,
  agents: Agents, voices: Voices, insights: Insights, bitcube: Bitcube, library: Library,
  harvest: Harvest, settings: Settings};
const sections = {
  video: videoSections, screenshot: screenshotSections, audio: audioSections,
  transcript: transcriptSections, agents: agentsSections, voices: voicesSections,
  insights: insightsSections, bitcube: bitcubeSections, library: librarySections,
  harvest: harvestSections,
  settings: [settingsSections[0], ...releaseSections, ...settingsSections.slice(1)],
};
export default function StudioWiki({section}) {
  const {siteConfig} = useDocusaurusContext();
  const history = useHistory();
  const location = useLocation();
  const model = siteConfig.customFields.studio;
  const {size} = useWikiPreferences();
  const [activeHeading, setActiveHeading] = useState(null);
  const search = new URLSearchParams(location.search);
  const face = ['tool', 'paperboy'].includes(search.get('face')) ? search.get('face') : 'wiki';
  const pageTitle = useRef(null);
  const root = useBaseUrl('/wiki/');
  const current = model.assets.find(a => a.id === section?.id) || model.assets[0];
  const Article = articles[current.id];
  useEffect(() => {
    if (!section?.id) {
      const query = new URLSearchParams(location.search);
      const legacy = new URLSearchParams(location.hash.slice(1));
      const aliases = {capture: 'video', gallery: 'screenshot', evidence: 'insights',
        protocols: 'agents', reasoning: 'voices', install: 'settings', widgets: 'library'};
      const id = query.get('section') || legacy.get('section') || 'video';
      const target = Object.hasOwn(articles, id) ? id : Object.hasOwn(aliases, id) ? aliases[id] : 'video';
      query.delete('section');
      if (!['tool', 'paperboy'].includes(query.get('face'))) query.delete('face');
      const suffix = query.toString();
      const hash = legacy.has('section') || legacy.has('view') ? '' : location.hash;
      history.replace(`${root}${target}/${suffix ? `?${suffix}` : ''}${hash}`);
    }
  }, [history, root, section?.id, location.search, location.hash]);

  const navigate = (id, nextFace = 'wiki', focus = true, anchor) => {
    history.push(`${root}${id}/${nextFace === 'wiki' ? '' : `?face=${nextFace}`}${anchor ? `#${encodeURIComponent(anchor)}` : ''}`);
    if (focus) requestAnimationFrame(() => pageTitle.current?.focus({preventScroll: true}));
  };
  const contract = {
    schema: model.schema, id: `yumlog.ui.${current.id}`, title: current.title,
    role: current.role, inputs: current.inputs, outputs: current.outputs,
    actions: current.actions, source: model.sourceBase + current.source,
    evidence: 'Documentation descriptor, not a live AgentCard or runtime result.',
  };
  return <Layout title={`${current.title} wiki deck`} description={current.summary}
    wrapperClassName="studio-wiki-page">
    <div className="studio-wiki-shell" data-size={size}>
      <WikiSidebar assets={model.assets} sections={sections} currentId={current.id}
        activeHeading={face === 'wiki' ? activeHeading : null}
        onNavigate={(id, anchor) => navigate(id, 'wiki', true, anchor)}/>
      <main className="wiki-workspace" id="wiki-workspace">
        <article className="wiki-card" aria-label={`${current.title} Studio card`} data-face={face}>
          <header className="wiki-card-header">
            <div className="wiki-card-name"><Glyph name={current.glyph}/><h1 ref={pageTitle} tabIndex="-1">{current.title}</h1></div>
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
            <CardLayers key={`${current.id}-${face}`} title={current.title} onLayerChange={setActiveHeading}>
            {face === 'wiki' && <div className="wiki-article markdown">
              <MDXProvider components={MDXComponents}><Article/></MDXProvider>
            </div>}
            {face === 'tool' && <div className="wiki-overview">
              <h3>{current.summary}</h3>
              <p>This is the capability front of the {current.title} card. The native app performs these operations; this website explains them.</p>
              <div className="contract-grid">{[['Inputs', current.inputs], ['Outputs', current.outputs], ['Actions', current.actions]].map(([label, items]) =>
                <div key={label}><h4>{label}</h4><ul>{items.map(x => <li key={x}><code>{x}</code></li>)}</ul></div>)}</div>
              <button className="wiki-primary" type="button" onClick={() => navigate(current.id)}>Flip to Wiki deck</button>
            </div>}
            {face === 'paperboy' && <div className="wiki-overview">
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
            </CardLayers>
          </section>
        </article>
      </main>
    </div>
  </Layout>;
}
