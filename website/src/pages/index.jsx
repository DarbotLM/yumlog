import React, {useEffect, useState} from 'react';
import Layout from '@theme/Layout';
import Link from '@docusaurus/Link';
import useBaseUrl from '@docusaurus/useBaseUrl';
import useDocusaurusContext from '@docusaurus/useDocusaurusContext';
import {useHistory, useLocation} from '@docusaurus/router';
import Glyph from '../components/StudioGlyph';

const release = 'https://github.com/DarbotLM/yumlog/releases';
const downloads = `${release}/download/v0.7.1`;
const workflow = [
  {number: '01', title: 'Capture the source.', section: 'video',
    description: 'Record a walkthrough, import a video, or collect approved screenshots. Keep the original media as the starting point.'},
  {number: '02', title: 'Find the meaning.', section: 'insights',
    description: 'Extract audio and frames, attach transcripts, and inspect source-linked evidence. Review what the tools know and what they do not.'},
  {number: '03', title: 'Deliver the evidence.', section: 'harvest',
    description: 'Package the recording, derived artifacts and Paperboy card together. Give the next person something they can verify.'},
];

export default function Website() {
  const {siteConfig} = useDocusaurusContext();
  const history = useHistory();
  const location = useLocation();
  const wiki = useBaseUrl('/wiki/');
  const logo = useBaseUrl('/yls-header-icon.svg');
  const [selected, setSelected] = useState('video');
  const assets = siteConfig.customFields.studio.assets;
  const current = assets.find(asset => asset.id === selected);
  const guide = id => `${wiki}?section=${id}&face=wiki`;

  useEffect(() => {
    const query = new URLSearchParams(location.search);
    const legacy = new URLSearchParams(location.hash.slice(1));
    if (query.has('section') || query.has('face') || legacy.has('section') || legacy.has('view')) {
      history.replace(wiki + location.search + location.hash);
    }
  }, [history, location.search, location.hash, wiki]);

  return <Layout title="Capture, understand, deliver"
    description="Yumlog is a local-first Windows media toolkit. Record, inspect and deliver source-linked evidence with an eleven-mode Studio and developer tools.">
    <main className="product-site">
      <nav className="product-nav" aria-label="Website sections">
        <span>YUMLOG / WINDOWS STUDIO</span>
        <div><a href="#workflow">Workflow</a><a href="#studio">Studio</a>
          <a href="#developers">Developers</a><a href="#downloads">Downloads</a></div>
      </nav>

      <section className="product-hero" aria-labelledby="product-title">
        <div>
          <p className="wiki-eyebrow">YOUR MEDIA. YOUR MACHINE. YOUR EVIDENCE.</p>
          <h1 id="product-title">Capture the moment.<br/><span>Keep the evidence.</span></h1>
          <p className="product-lead">A Windows workspace for recording, understanding and
            delivering media. From the first frame to the final handoff, keep every
            insight connected to its source.</p>
          <div className="product-actions">
            <a className="product-button product-button-primary" href="#downloads">Get Yumlog 0.7.1</a>
            <Link className="product-button" to={wiki}>Explore the Wiki deck</Link>
          </div>
          <p className="product-caption">Local-first. Windows x64. Source available on GitHub.</p>
        </div>
        <div className="product-evidence" aria-label="Source-linked media workflow">
          <div className="product-evidence-header"><img src={logo} alt="" width="36" height="36"/>
            <span>Yumlog Studio<small>ONE SOURCE. CONNECTED OUTPUTS.</small></span></div>
          <div className="product-source"><Glyph name="video"/><div><strong>Original recording</strong>
            <span>Preserve the media, time range and provenance.</span></div></div>
          <div className="product-derived">
            <Link to={guide('screenshot')}><Glyph name="image"/><strong>Frames</strong><span>See the moment</span></Link>
            <Link to={guide('transcript')}><Glyph name="text"/><strong>Transcript</strong><span>Follow the words</span></Link>
            <Link to={guide('insights')}><Glyph name="chart"/><strong>Evidence</strong><span>Resolve the claim</span></Link>
          </div>
          <Link className="product-handoff" to={guide('harvest')}><Glyph name="box"/>
            <span><strong>Paperboy handoff</strong><small>Source + artifacts + a versioned card</small></span>
            <span>Read the guide</span></Link>
          <p className="product-caption">Workflow illustration, not a live capture or analysis.</p>
        </div>
      </section>

      <section className="product-section" id="workflow" aria-labelledby="workflow-title">
        <div className="product-section-heading"><p className="wiki-eyebrow">FROM CAPTURE TO CONTEXT</p>
          <h2 id="workflow-title">Less lost context. More useful handoffs.</h2>
          <p>For walkthroughs, technical investigations and media workflows where the source matters.</p></div>
        <div className="product-workflow">{workflow.map(step =>
          <article key={step.number}><span className="product-number">{step.number}</span>
            <h3>{step.title}</h3><p>{step.description}</p>
            <Link to={guide(step.section)}>Explore this workflow</Link></article>)}</div>
      </section>

      <section className="product-section" id="studio" aria-labelledby="studio-title">
        <div className="product-section-heading"><p className="wiki-eyebrow">ELEVEN MODES. ONE STUDIO.</p>
          <h2 id="studio-title">Choose your next step.</h2>
          <p>Explore the tools, then flip into the Wiki deck for instructions, examples and integration details.</p></div>
        <div className="product-explorer">
          <div className="product-mode-buttons" role="group" aria-label="Explore Studio capabilities">
            {assets.map(asset => <button key={asset.id} type="button" aria-pressed={selected === asset.id}
              aria-controls="product-mode-detail" onClick={() => setSelected(asset.id)}>
              <Glyph name={asset.glyph}/>{asset.title}</button>)}
          </div>
          <div className="product-mode-detail" id="product-mode-detail" aria-live="polite">
            <div className="product-mode-heading"><Glyph name={current.glyph}/>
              <span className="wiki-eyebrow">{current.role}</span></div>
            <h3>{current.title}</h3><p>{current.summary}</p>
            <div className="product-output-label">WORK WITH</div>
            <ul className="product-output-list">{current.outputs.map(output => <li key={output}>{output}</li>)}</ul>
            <Link className="product-button" to={guide(current.id)}>Open {current.title} Wiki deck</Link>
          </div>
        </div>
      </section>

      <section className="product-section product-principles" aria-labelledby="principles-title">
        <div className="product-section-heading"><p className="wiki-eyebrow">EVIDENCE OVER ASSERTION</p>
          <h2 id="principles-title">Make the result inspectable.</h2></div>
        <div className="product-workflow">
          <article><h3>Keep the source.</h3><p>Frames, audio, captions and semantic cards retain their
            connection to the original. Review derived artifacts before sharing.</p></article>
          <article><h3>Resolve the citation.</h3><p>An MDKG sidecar can tie a runbook step to a
            timestamp and source value. Verify the claim rather than trusting a summary.</p></article>
          <article><h3>Choose what runs.</h3><p>Capture needs consent. OCR and speech need compatible
            runtimes and models. Downloads and configured connectors can use the network.</p></article>
        </div>
      </section>

      <section className="product-section" id="developers" aria-labelledby="developers-title">
        <div className="product-section-heading"><p className="wiki-eyebrow">BUILD AROUND THE EVIDENCE</p>
          <h2 id="developers-title">Tools for people. Contracts for agents.</h2>
          <p>Use the native CLI, versioned schemas and explicit protocol boundaries.
            Discovery is not permission, and a declaration is not an executable backend.</p></div>
        <div className="product-developer-grid">
          <Link to="/docs/NATIVE-CLI/"><span className="product-number">CLI</span><h3>Automate the workflow</h3>
            <p>Recording, analysis, transcription and cited runbooks with explicit inputs and outputs.</p><span>Native CLI reference</span></Link>
          <Link to="/docs/SDK-FRAMEWORK/"><span className="product-number">SDK</span><h3>Use versioned contracts</h3>
            <p>Evidence formats, extension manifests and integration boundaries for your own tools.</p><span>SDK &amp; framework guide</span></Link>
          <Link to="/docs/AGENT-PLUGIN/"><span className="product-number">MCP</span><h3>Connect an agent</h3>
            <p>Paperboy stdio and HTTP interfaces with declared capabilities and distinct trust boundaries.</p><span>Agent Plugin &amp; MCP guide</span></Link>
        </div>
      </section>

      <section className="product-section" id="downloads" aria-labelledby="downloads-title">
        <div className="product-section-heading"><p className="wiki-eyebrow">START WITH THE PUBLISHED BUILD</p>
          <h2 id="downloads-title">Get Yumlog for Windows.</h2>
          <p>Release 0.7.1. Choose the full toolkit or the native Studio package.
            Review the installation guide and verify downloads before running them.</p></div>
        <div className="product-downloads">
          <article><span className="product-number">COMPLETE TOOLKIT</span><h3>Windows x64 suite</h3>
            <p>Studio, native CLI, Paperboy Desktop, Paperboy MCP, APIM Service and Widget Studio.
              Self-contained application binaries, with FFmpeg and FFprobe for native media tools.</p>
            <p className="product-caption">ZIP / approximately 801 MB / six components</p>
            <a className="product-button product-button-primary" href={`${downloads}/Yumlog-0.7.1-win-x64.zip`}>Download Windows suite</a>
            <div className="product-download-meta"><a href={`${downloads}/Yumlog-0.7.1-win-x64.zip.sha256`}>SHA-256 checksum</a>
              <a href={`${downloads}/release-manifest.json`}>Release manifest</a></div></article>
          <article><span className="product-number">NATIVE STUDIO</span><h3>Windows MSIX</h3>
            <p>The native Studio in a Windows application package. Check the package identity,
              signature and publisher before installing. Separate suite executables are not included.</p>
            <p className="product-caption">MSIX / approximately 269 MB / version 0.7.1.0</p>
            <a className="product-button" href={`${downloads}/DarbotLabs.Yumlog.Native_0.7.1.0_x64.msix`}>Download Native Studio</a>
            <div className="product-download-meta"><Link to={guide('settings')}>Installation instructions</Link>
              <a href={`${release}/tag/v0.7.1`}>Release notes</a></div></article>
        </div>
        <p className="product-download-note">Optional speech and OCR models have separate requirements.
          No installer runs from this page. <a href={`${release}/latest`}>Check for newer releases</a>.</p>
      </section>
      <footer className="product-closing"><img src={logo} alt="YLS" width="42" height="42"/>
        <div><strong>Make the next handoff a better one.</strong><span>Capture. Understand. Verify. Deliver.</span></div>
        <Link className="product-button" to={wiki}>Open the Wiki deck</Link></footer>
    </main>
  </Layout>;
}
