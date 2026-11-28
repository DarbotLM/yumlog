import React, {useEffect, useRef, useState} from 'react';
import {useLocation} from '@docusaurus/router';

const gap = 28;

export default function ContentPages({children, title}) {
  const viewport = useRef(null);
  const flow = useRef(null);
  const {hash, key: locationKey} = useLocation();
  const [layout, setLayout] = useState({width: 0, height: 0, labels: [title], headings: []});
  const [layer, setLayer] = useState(0);
  const stride = layout.width + gap;

  useEffect(() => {
    let frame;
    let disposed = false;
    const measure = () => {
      if (disposed) return;
      const width = viewport.current.clientWidth;
      const height = viewport.current.clientHeight;
      if (!width || !height) return;
      flow.current.style.width = `${width}px`;
      flow.current.style.height = `${height}px`;
      flow.current.style.columnWidth = `${width}px`;
      const origin = flow.current.getBoundingClientRect().left;
      const headings = Array.from(flow.current.querySelectorAll('h2,h3,h4')).map(heading => ({
        text: heading.textContent,
        id: heading.id,
        layer: Math.max(0, Math.floor((heading.getBoundingClientRect().left - origin + 1) / (width + gap))),
      }));
      const count = Math.max(1, Math.ceil((flow.current.scrollWidth + gap - 1) / (width + gap)),
        ...headings.map(heading => heading.layer + 1));
      let previous = title;
      const labels = Array.from({length: count}, (_, index) => {
        const heading = headings.find(item => item.layer === index);
        if (heading) previous = heading.text;
        return heading || index === 0 ? previous : `${previous} (continued)`;
      });
      setLayout({width, height, labels, headings});
      setLayer(current => Math.min(current, count - 1));
    };
    const schedule = () => {
      if (disposed) return;
      cancelAnimationFrame(frame);
      frame = requestAnimationFrame(measure);
    };
    const resize = new ResizeObserver(schedule);
    resize.observe(viewport.current);
    // Fragmentation can settle after hydration without changing the viewport.
    if (flow.current.firstElementChild) resize.observe(flow.current.firstElementChild);
    const content = new MutationObserver(schedule);
    content.observe(flow.current, {subtree: true, childList: true, characterData: true, attributes: true, attributeFilter: ['open']});
    document.fonts?.ready.then(schedule);
    schedule();
    return () => {
      disposed = true;
      cancelAnimationFrame(frame);
      resize.disconnect();
      content.disconnect();
    };
  }, [title]);

  useEffect(() => {
    if (!hash || !layout.width) return;
    let id;
    try { id = decodeURIComponent(hash.slice(1)); } catch { return; }
    const target = document.getElementById(id);
    if (!target || !flow.current.contains(target)) return;
    const offset = target.getBoundingClientRect().left - flow.current.getBoundingClientRect().left;
    setLayer(Math.max(0, Math.min(layout.labels.length - 1, Math.floor((offset + 1) / stride))));
  }, [hash, locationKey, layout.width, layout.height, layout.labels.length, stride]);

  const revealFocus = event => {
    const origin = flow.current.getBoundingClientRect().left;
    const rects = Array.from(event.target.getClientRects());
    if (!rects.length || !layout.width) return;
    const pages = rects.map(rect => Math.floor((rect.left - origin + 1) / stride));
    if (!pages.includes(layer)) setLayer(Math.max(0, Math.min(layout.labels.length - 1, pages[0])));
  };
  const change = next => setLayer(Math.max(0, Math.min(layout.labels.length - 1, next)));

  return <div className="card-layers" data-page={layer + 1} data-page-count={layout.labels.length}>
    <div className="layer-window" ref={viewport}>
      <div className="layer-flow" ref={flow} onFocusCapture={revealFocus}
        style={{columnGap: `${gap}px`, transform: `translateX(${-layer * stride}px)`}}>
        {children}
      </div>
    </div>
    <nav className="layer-controls" aria-label={`${title} content pages`}>
      <button type="button" disabled={layer === 0} onClick={() => change(layer - 1)}>Previous page</button>
      <label className="layer-select"><span className="sr-only">Content page</span>
        <select value={layer} onChange={event => change(Number(event.target.value))}>
          {layout.labels.map((label, index) => <option key={index} value={index}>{index + 1}. {label}</option>)}
        </select>
      </label>
      <span className="layer-position" role="status" aria-live="polite">{layer + 1} / {layout.labels.length}</span>
      <button type="button" disabled={layer === layout.labels.length - 1} onClick={() => change(layer + 1)}>Next page</button>
    </nav>
  </div>;
}
