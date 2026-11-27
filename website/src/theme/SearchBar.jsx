import React, {useEffect, useId, useMemo, useRef, useState} from 'react';
import Link from '@docusaurus/Link';
import useBaseUrl from '@docusaurus/useBaseUrl';
import {useHistory, useLocation} from '@docusaurus/router';

function search(entries, query) {
  const words = query.toLowerCase().trim().split(/\s+/).filter(Boolean);
  if (!words.length) return [];
  return entries.map(entry => {
    const title = entry.title.toLowerCase();
    const body = entry.text.toLowerCase();
    if (!words.every(word => title.includes(word) || body.includes(word))) return null;
    const score = words.reduce((sum, word) => sum + (title.includes(word) ? 10 : 1), 0);
    const found = body.indexOf(words.find(word => body.includes(word)) || '');
    const start = Math.max(0, found - 55);
    return {...entry, score, snippet: `${start ? '... ' : ''}${entry.text.slice(start, start + 190)}${entry.text.length > start + 190 ? ' ...' : ''}`};
  }).filter(Boolean).sort((a, b) => b.score - a.score || a.title.localeCompare(b.title)).slice(0, 8);
}

export default function SearchBar() {
  const indexUrl = useBaseUrl('/search-index.json');
  const wiki = useBaseUrl('/wiki/');
  const history = useHistory();
  const location = useLocation();
  const id = useId();
  const input = useRef(null);
  const container = useRef(null);
  const [query, setQuery] = useState('');
  const [open, setOpen] = useState(false);
  const [entries, setEntries] = useState(null);
  const [status, setStatus] = useState('idle');
  const [attempt, setAttempt] = useState(0);
  const results = useMemo(() => search(entries || [], query), [entries, query]);
  useEffect(() => { setOpen(false); }, [location.pathname, location.search]);
  useEffect(() => {
    if (!open || entries) return undefined;
    const controller = new AbortController();
    setStatus('loading');
    fetch(indexUrl, {signal: controller.signal})
      .then(response => { if (!response.ok) throw new Error('Search unavailable'); return response.json(); })
      .then(data => {
        if (!Array.isArray(data) || data.some(entry => typeof entry.title !== 'string' ||
            typeof entry.text !== 'string' || typeof entry.url !== 'string' || !entry.url.startsWith(indexUrl.replace(/search-index\.json$/, '')))) {
          throw new Error('Invalid search index');
        }
        setEntries(data); setStatus('ready');
      })
      .catch(error => { if (error.name !== 'AbortError') setStatus('error'); });
    return () => controller.abort();
  }, [open, entries, indexUrl, attempt]);
  const close = () => { setOpen(false); input.current?.focus(); };
  const keys = event => {
    if (event.key === 'Escape') { event.preventDefault(); close(); }
    if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
      const links = Array.from(container.current.querySelectorAll('.search-results a'));
      if (!links.length) return;
      event.preventDefault();
      const current = links.indexOf(document.activeElement);
      const next = event.key === 'ArrowDown' ? current + 1 : current - 1;
      if (next < 0) input.current?.focus();
      else links[Math.min(next, links.length - 1)]?.focus();
    }
  };
  return <div className="header-search" ref={container} onKeyDown={keys}
    onBlur={event => { if (!event.currentTarget.contains(event.relatedTarget)) setOpen(false); }}>
    <form role="search" onSubmit={event => {
      event.preventDefault();
      if (results.length) { history.push(results[0].url); setOpen(false); }
    }}>
      <label className="sr-only" htmlFor={id}>Search website and wiki</label>
      <input id={id} ref={input} type="search" value={query} aria-controls={`${id}-results`}
        autoComplete="off" placeholder="Search guides and commands..."
        onFocus={() => setOpen(true)} onChange={event => { setQuery(event.target.value); setOpen(true); }}/>
    </form>
    {open && <div id={`${id}-results`} className="search-results">
      <p role="status">{status === 'loading' ? 'Loading local search index...' :
        status === 'error' ? 'Search could not load.' :
        !query.trim() ? 'Search all Wiki decks, instructions, commands and developer guides.' :
        results.length ? `${results.length} matching guides` : 'No matching guides. Try another phrase.'}</p>
      {status === 'error' ? <div><button type="button" onClick={() => setAttempt(value => value + 1)}>Retry</button>
        <Link to={wiki} onClick={() => setOpen(false)}>Browse the Wiki deck</Link></div> :
        <ul>{results.map(result => <li key={result.url}><Link to={result.url} onClick={() => setOpen(false)}>
          <strong>{result.title}</strong><span>{result.snippet}</span>
        </Link></li>)}</ul>}
    </div>}
  </div>;
}
