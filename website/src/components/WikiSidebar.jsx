import React, {useEffect, useRef, useState} from 'react';
import Link from '@docusaurus/Link';
import useBaseUrl from '@docusaurus/useBaseUrl';
import {useHistory} from '@docusaurus/router';
import Glyph from './StudioGlyph';
import {useWikiPreferences} from './WikiPreferences';
import {useRelease} from './release';

export default function WikiSidebar({assets, sections, currentId, activeHeading, onNavigate}) {
  const root = useBaseUrl('/wiki/');
  const docBase = useBaseUrl('/docs/');
  const history = useHistory();
  const release = useRelease();
  const {navigation, setNavigation} = useWikiPreferences();
  const [open, setOpen] = useState(false);
  const panel = useRef(null);
  const aside = useRef(null);
  const toggle = useRef(null);
  const expanded = navigation.mode === 'all' ? assets.map(asset => asset.id) :
    navigation.mode === 'selected' ? [currentId] : navigation.cards;

  useEffect(() => {
    setNavigation(value => value.mode === 'all' || value.currentId === currentId ? value :
      {mode: 'selected', cards: [], currentId});
    setOpen(false);
  }, [currentId, setNavigation]);

  useEffect(() => {
    const frame = requestAnimationFrame(() => {
      const row = panel.current.querySelector(`[data-card="${currentId}"]`);
      if (!row || !panel.current.clientHeight) return;
      const bounds = panel.current.getBoundingClientRect();
      const selected = row.getBoundingClientRect();
      const toolbarHeight = panel.current.querySelector('.dock-expansion-controls').clientHeight;
      if (selected.top < bounds.top + toolbarHeight) panel.current.scrollTop += selected.top - bounds.top - toolbarHeight;
      else if (selected.bottom > bounds.bottom) panel.current.scrollTop += selected.bottom - bounds.bottom;
    });
    return () => cancelAnimationFrame(frame);
  }, [currentId, navigation.mode, open]);

  useEffect(() => {
    if (!open) return undefined;
    const dismiss = event => { if (!aside.current.contains(event.target)) setOpen(false); };
    document.addEventListener('pointerdown', dismiss);
    return () => document.removeEventListener('pointerdown', dismiss);
  }, [open]);

  const choose = (id, anchor) => {
    setNavigation(value => value.mode === 'all' ? value : {mode: 'selected', cards: [], currentId: id});
    setOpen(false);
    onNavigate(id, anchor);
  };
  const follow = (event, id, anchor) => {
    if (event.button !== 0 || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) return;
    event.preventDefault();
    choose(id, anchor);
  };
  const toggleCard = id => setNavigation({
    mode: 'manual',
    currentId,
    cards: expanded.includes(id) ? expanded.filter(card => card !== id) : [...expanded, id],
  });
  const href = (id, target = 'title') => `${root}${id}/${target}/`;

  return <aside ref={aside} className="wiki-dock" aria-label="Studio wiki sections" data-open={open}
    onKeyDown={event => {
      if (event.key === 'Escape' && open) { event.preventDefault(); setOpen(false); toggle.current?.focus(); }
    }}>
    <div className="dock-compact-controls">
      <label><span className="sr-only">Wiki section</span>
        <select value={currentId} onChange={event => choose(event.target.value)}>
          {assets.map(asset => <option key={asset.id} value={asset.id}>{asset.title}</option>)}
        </select>
      </label>
      <label><span className="sr-only">Developer guide</span>
        <select value="" onChange={event => { setOpen(false); history.push(`${docBase}${event.target.value}/`); }}>
          <option value="" disabled>Developer guides</option>
          <option value="SDK-FRAMEWORK">SDK &amp; framework</option>
          <option value="AGENT-PLUGIN">Agent Plugin &amp; MCP</option>
          <option value="NATIVE-CLI">Native CLI reference</option>
        </select>
      </label>
      <button ref={toggle} type="button" className="dock-sections-toggle" aria-expanded={open}
        aria-controls="wiki-dock-contents" onClick={() => setOpen(value => !value)}>Sections</button>
    </div>
    <div ref={panel} id="wiki-dock-contents" className="dock-contents">
      <div className="dock-expansion-controls">
        <button type="button" disabled={navigation.mode === 'all'}
          onClick={() => setNavigation({mode: 'all', cards: [], currentId})}>Expand all</button>
        <button type="button" disabled={!expanded.length}
          onClick={() => setNavigation({mode: 'manual', cards: [], currentId})}>Collapse all</button>
      </div>
      <nav aria-label="Choose a Studio wiki">
        {assets.map(asset => <div className="wiki-tree-card" key={asset.id}>
          <div className="wiki-tree-row" data-card={asset.id}>
            <a className="wiki-card-link" href={href(asset.id)} onClick={event => follow(event, asset.id)}
              data-current={currentId === asset.id}
              aria-current={currentId === asset.id && activeHeading === 'title' ? 'page' : undefined}>
              <Glyph name={asset.glyph}/><span>{asset.title}</span>
            </a>
            <button className="wiki-tree-toggle" type="button" aria-expanded={expanded.includes(asset.id)}
              aria-controls={`wiki-sections-${asset.id}`} onClick={() => toggleCard(asset.id)}
              aria-label={`${expanded.includes(asset.id) ? 'Collapse' : 'Expand'} ${asset.title} sections`}>
              <svg viewBox="0 0 16 16" width="16" height="16" fill="none" stroke="currentColor" aria-hidden="true">
                <path d="m6 3 5 5-5 5"/>
              </svg>
            </button>
          </div>
          <ul className="wiki-subsections" id={`wiki-sections-${asset.id}`} hidden={!expanded.includes(asset.id)}>
            {sections[asset.id].map(item => <li key={item.id}>
              <a href={href(asset.id, item.id)} onClick={event => follow(event, asset.id, item.id)}
                aria-current={currentId === asset.id && activeHeading === item.id ? 'page' : undefined}>
                {item.value}
              </a>
            </li>)}
          </ul>
        </div>)}
      </nav>
      <div className="dock-guides">
        <Link to={`${docBase}SDK-FRAMEWORK/`}>SDK &amp; framework</Link>
        <Link to={`${docBase}AGENT-PLUGIN/`}>Agent Plugin &amp; MCP</Link>
        <Link to={`${docBase}NATIVE-CLI/`}>Native CLI reference</Link>
        <a href={`${root.replace(/wiki\/$/, '')}cards/index.json`}>Agent card catalog</a>
      </div>
      <a className="install-studio" href={href('settings', 'instructions')} onClick={event => follow(event, 'settings', 'instructions')}>Install Yumlog {release.version}</a>
    </div>
  </aside>;
}
