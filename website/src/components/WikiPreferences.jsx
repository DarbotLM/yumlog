import React, {createContext, useContext, useEffect, useState} from 'react';

const Preferences = createContext(null);
const sizes = ['compact', 'studio', 'wide'];

export function WikiPreferences({children}) {
  const [size, setSize] = useState('studio');
  useEffect(() => {
    try {
      const saved = sessionStorage.getItem('yumlog.wiki.cardSize');
      if (sizes.includes(saved)) setSize(saved);
    } catch {}
  }, []);
  const changeSize = value => {
    if (!sizes.includes(value)) return;
    setSize(value);
    try { sessionStorage.setItem('yumlog.wiki.cardSize', value); } catch {}
  };
  return <Preferences.Provider value={{size, setSize: changeSize}}>{children}</Preferences.Provider>;
}

export const useWikiPreferences = () => useContext(Preferences);
