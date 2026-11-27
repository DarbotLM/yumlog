import React from 'react';
import {useLocation} from '@docusaurus/router';
import useBaseUrl from '@docusaurus/useBaseUrl';
import {useWikiPreferences} from './WikiPreferences';

export default function CardSize({mobile}) {
  const {pathname} = useLocation();
  const root = useBaseUrl('/wiki/');
  const {size, setSize} = useWikiPreferences();
  if (pathname !== root.slice(0, -1) && !pathname.startsWith(root)) return null;
  const control = <label className="header-card-size">Agent Card Size
    <select value={size} onChange={event => setSize(event.target.value)}>
      <option value="compact">Compact</option><option value="studio">Studio</option><option value="wide">Wide</option>
    </select></label>;
  return mobile ? <li className="menu__list-item">{control}</li> : <div className="navbar__item">{control}</div>;
}
