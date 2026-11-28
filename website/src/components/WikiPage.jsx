import React, {useEffect, useRef} from 'react';
import Layout from '@theme/Layout';
import useBaseUrl from '@docusaurus/useBaseUrl';
import useDocusaurusContext from '@docusaurus/useDocusaurusContext';
import {usePluginData} from '@docusaurus/useGlobalData';
import useIsBrowser from '@docusaurus/useIsBrowser';
import {useHistory, useLocation} from '@docusaurus/router';
import Glyph from './StudioGlyph';
import {useWikiPreferences} from './WikiPreferences';
import WikiSidebar from './WikiSidebar';
import ContentPages from './ContentPages';
import {CardFront, CardBack} from './CardContent';

const tabs = [
  {id: 'adaptive-card', title: 'Adaptive Card 1.5', artifact: 'adaptiveCard'},
  {id: 'agentcard', title: 'AgentCard', artifact: 'agentCard'},
  {id: 'history', title: 'History', artifact: 'history'},
  {id: 'framework', title: 'Framework', artifact: 'framework'},
];
const aliases = {capture: 'video', gallery: 'screenshot', evidence: 'insights', protocols: 'agents',
  reasoning: 'voices', install: 'settings', widgets: 'library'};

export default function WikiPage({section, layer}) {
  const {siteConfig} = useDocusaurusContext();
  const {layerDefinitions, schemas} = usePluginData('yumlog-studio');
  const history = useHistory();
  const location = useLocation();
  const browser = useIsBrowser();
  const {size} = useWikiPreferences();
  const titleRef = useRef(null);
  const root = useBaseUrl('/wiki/');
  const assets = siteConfig.customFields.studio.assets;
  const currentId = section?.id || assets[0].id;
  const currentAsset = assets.find(asset => asset.id === currentId);
  const currentLayer = section?.layers.find(item => item.id === layer?.id) || section?.layers[0];
  const layerId = currentLayer?.id || 'title';
  const query = new URLSearchParams(browser ? location.search : '');
  const side = query.get('side') === 'back' ? 'back' : 'front';
  const activeTab = tabs.find(tab => tab.id === query.get('tab')) || tabs[0];
  const sections = Object.fromEntries(assets.map(asset => [asset.id, layerDefinitions]));

  useEffect(() => {
    const search = new URLSearchParams(location.search);
    const legacy = new URLSearchParams(location.hash.slice(1));
    const requested = search.get('section') || legacy.get('section') || currentId;
    const id = assets.some(asset => asset.id === requested) ? requested :
      Object.hasOwn(aliases, requested) ? aliases[requested] : assets[0].id;
    if (!section) {
      search.delete('section');
      const suffix = search.toString();
      history.replace(`${root}${id}/${suffix ? `?${suffix}` : ''}${location.hash}`);
      return;
    }
    let anchor = '';
    try { anchor = decodeURIComponent(location.hash.slice(1)); } catch {}
    const oldFace = search.get('face');
    const target = Object.hasOwn(section.anchors, anchor) ? section.anchors[anchor] :
      layer?.id || (oldFace === 'tool' ? 'agents-tools' : 'title');
    if (!layer || oldFace || target !== layer.id || search.has('section')) {
      search.delete('face');
      search.delete('section');
      if (oldFace === 'paperboy') { search.set('side', 'back'); search.set('tab', 'agentcard'); }
      const hash = legacy.has('section') || legacy.has('view') ? '' : location.hash;
      const suffix = search.toString();
      history.replace(`${root}${currentId}/${target}/${suffix ? `?${suffix}` : ''}${hash}`);
    }
  }, [assets, currentId, history, layer, location.hash, location.search, root, section]);

  const navigate = (id, target = 'title', nextSide = 'front', tab = 'adaptive-card') => {
    history.push(`${root}${id}/${target}/${nextSide === 'back' ? `?side=back&tab=${tab}` : ''}`);
    requestAnimationFrame(() => titleRef.current?.focus({preventScroll: true}));
  };
  const index = layerDefinitions.findIndex(item => item.id === layerId);
  return <Layout title={`${currentAsset.title}: ${currentLayer?.value || 'Title'}`}
    description={currentAsset.summary} wrapperClassName="studio-wiki-page">
    <div className="studio-wiki-shell" data-size={size}>
      <WikiSidebar assets={assets} sections={sections} currentId={currentId} activeHeading={layerId}
        onNavigate={(id, target) => navigate(id, target || 'title')}/>
      <main className="wiki-workspace" id="wiki-workspace">
        <article className="wiki-card" data-section={currentId} data-semantic-layer={layerId} data-side={side}
          aria-label={`${currentAsset.title} ${currentLayer?.value || 'Title'} Agent Card`}>
          <header className="wiki-card-header">
            <div className="wiki-card-name"><Glyph name={currentAsset.glyph}/><h1 ref={titleRef} tabIndex="-1">{currentAsset.title}</h1></div>
            <div className="semantic-layer-controls" role="group" aria-label="Agent Card layers">
              <button type="button" disabled={index === 0} onClick={() => navigate(currentId, layerDefinitions[index - 1].id)}>Previous card</button>
              <label><span className="sr-only">Agent Card layer</span>
                <select value={layerId} onChange={event => navigate(currentId, event.target.value)}>
                  {layerDefinitions.map(item => <option key={item.id} value={item.id}>{item.value}</option>)}
                </select>
              </label>
              <button type="button" disabled={index === layerDefinitions.length - 1}
                onClick={() => navigate(currentId, layerDefinitions[index + 1].id)}>Next card</button>
            </div>
            <div className="face-controls" role="group" aria-label="Agent Card side">
              <button type="button" aria-pressed={side === 'front'} onClick={() => navigate(currentId, layerId)}>Front</button>
              <button type="button" aria-pressed={side === 'back'} onClick={() => navigate(currentId, layerId, 'back', activeTab.id)}>Back</button>
            </div>
          </header>
          {side === 'back' && <nav className="card-back-tabs" aria-label="Backside views">
            {tabs.map(tab => <button key={tab.id} type="button" aria-pressed={activeTab.id === tab.id}
              onClick={() => navigate(currentId, layerId, 'back', tab.id)}>{tab.title}</button>)}
            {currentLayer && <div className="card-download-links">
              <a href={currentLayer.downloads[activeTab.artifact].url}>Tab JSON</a>
              <a href={currentLayer.downloads.flipcard.url}>Flipcard JSON</a>
            </div>}
          </nav>}
          <section id="wiki-card-content" className="wiki-face" aria-label={`${currentAsset.title} ${currentLayer?.value || 'Title'} ${side}`}>
            {currentLayer && <ContentPages key={`${currentId}-${layerId}-${side}-${activeTab.id}-${location.key || ''}`} title={currentLayer.card.name}>
              {side === 'front' ? <CardFront card={currentLayer.card}/> :
                <CardBack card={currentLayer.card} tab={activeTab.id} schemas={schemas}/>}
            </ContentPages>}
          </section>
        </article>
      </main>
    </div>
  </Layout>;
}
