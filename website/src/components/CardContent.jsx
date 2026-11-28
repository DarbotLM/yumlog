import React from 'react';
import Link from '@docusaurus/Link';
import CodeBlock from '@theme/CodeBlock';
import useDocusaurusContext from '@docusaurus/useDocusaurusContext';

function Inline({nodes, site}) {
  return nodes.map((node, index) => {
    if (node.type === 'text') return <React.Fragment key={index}>{node.value}</React.Fragment>;
    if (node.type === 'inlineCode') return <code key={index}>{node.value}</code>;
    if (node.type === 'break') return <br key={index}/>;
    const content = <Inline nodes={node.children} site={site}/>;
    if (node.type === 'link') {
      const prefix = site.url + site.baseUrl;
      return node.url.startsWith(prefix) ?
        <Link key={index} to={site.baseUrl + node.url.slice(prefix.length)}>{content}</Link> :
        <a key={index} href={node.url}>{content}</a>;
    }
    const Tag = {strong: 'strong', emphasis: 'em', delete: 'del'}[node.type];
    return <Tag key={index}>{content}</Tag>;
  });
}

function Blocks({nodes, site}) {
  return nodes.map((node, index) => {
    if (node.type === 'heading') {
      const Tag = `h${node.depth}`;
      return <Tag id={node.id} key={index}><Inline nodes={node.children} site={site}/></Tag>;
    }
    if (node.type === 'paragraph') return <p key={index}><Inline nodes={node.children} site={site}/></p>;
    if (node.type === 'code') return <CodeBlock key={index} language={node.language}>{node.value}</CodeBlock>;
    if (node.type === 'list') {
      const Tag = node.ordered ? 'ol' : 'ul';
      return <Tag key={index} {...(node.ordered ? {start: node.start} : {})}>
        {node.items.map((item, itemIndex) => <li key={itemIndex}><Blocks nodes={item} site={site}/></li>)}
      </Tag>;
    }
    return <table key={index}><thead><tr>{node.rows[0].map((cell, i) =>
      <th key={i}><Inline nodes={cell} site={site}/></th>)}</tr></thead>
      <tbody>{node.rows.slice(1).map((row, i) => <tr key={i}>{row.map((cell, j) =>
        <td key={j}><Inline nodes={cell} site={site}/></td>)}</tr>)}</tbody></table>;
  });
}

export function CardFront({card}) {
  const {siteConfig} = useDocusaurusContext();
  return <div className="wiki-article markdown" id={card.layer}><Blocks nodes={card.front.blocks} site={siteConfig}/></div>;
}

export function CardBack({card, tab, schemas}) {
  if (tab === 'history') return <div className="wiki-article markdown">
    <h2>Source history</h2><p>{card.back.history.scope}</p>
    <p>Build revision: <a href={`https://github.com/DarbotLM/yumlog/commit/${card.source.buildCommit}`}><code>{card.source.buildCommit.slice(0, 12)}</code></a>.
      {card.source.workingTreeModified && ' This build contains unpublished source changes.'}
      {card.back.history.shallow && ' History is limited to the commits fetched for this build.'}</p>
    <ol>{card.back.history.entries.map(entry => <li key={entry.commit}>
      <p><a href={entry.url}>{entry.subject}</a><br/><time dateTime={entry.date}>{entry.date.slice(0, 10)}</time>
        {' / '}<code>{entry.commit.slice(0, 12)}</code></p>
    </li>)}</ol>
    {!card.back.history.entries.length && <p>No committed source revisions are available for this build.</p>}
    <h3>Source fingerprint</h3><p><code>{card.source.path}</code></p><p><code>{card.source.sha256}</code></p>
  </div>;
  if (tab === 'framework') return <div className="wiki-article markdown">
    <h2>Framework and integration</h2>
    <p>This is a versioned documentation card, not a running agent or an execution grant.
      Adaptive Card renderers handle its 1.5 payload; AgentCard consumers handle the separate 3.1 descriptor.</p>
    <h3>Native contract</h3>
    <table><tbody>{[['Inputs', card.back.framework.inputs], ['Outputs', card.back.framework.outputs],
      ['Documented actions', card.back.framework.documentedActions]].map(([label, values]) =>
      <tr key={label}><th>{label}</th><td>{values.join(', ')}</td></tr>)}</tbody></table>
    <h3>Versioned schemas</h3><ul>
      <li><a href={schemas.adaptiveCard}>Adaptive Card 1.5 schema</a></li>
      <li><a href={schemas.agentCard}>AgentCard 3.1 schema</a></li>
      <li><a href={schemas.flipcard}>Yumlog flipcard envelope 1.0</a></li>
    </ul>
    <h3>References</h3><ul>{card.back.framework.references.slice(0, 3).map(url =>
      <li key={url}><a href={url}>{url}</a></li>)}</ul>
  </div>;
  const adaptive = tab === 'adaptive-card';
  return <div className="wiki-article markdown">
    <h2>{adaptive ? 'Adaptive Card 1.5' : 'AgentCard 3.1'}</h2>
    <p>{adaptive ? 'This payload is validated against the pinned Adaptive Card 1.5 schema.' :
      'This validated AgentCard 3.1 descriptor describes documentation. It is not deployed or authorized to run native tools.'}
      {' '}<a href={adaptive ? schemas.adaptiveCard : schemas.agentCard}>Read the schema</a>.</p>
    <CodeBlock language="json">{JSON.stringify(adaptive ? card.back.adaptiveCard : card.back.agentCard, null, 2)}</CodeBlock>
  </div>;
}
