import {usePluginData} from '@docusaurus/useGlobalData';

export function useRelease() {
  return usePluginData('yumlog-studio').release;
}

export const assetSize = asset => `${(asset.size / 1000000).toFixed(1)} MB`;
