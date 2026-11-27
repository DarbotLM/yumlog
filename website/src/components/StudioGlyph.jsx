import React from 'react';

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

export default function StudioGlyph({name}) {
  return <svg className="wiki-glyph" viewBox="0 0 26 24" fill="none" stroke="currentColor"
    strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
    {(glyphs[name] || glyphs.video).map(d => <path d={d} key={d}/>)}
  </svg>;
}
