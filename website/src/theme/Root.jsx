import React from 'react';
import {WikiPreferences} from '../components/WikiPreferences';

export default function Root({children}) {
  return <WikiPreferences>{children}</WikiPreferences>;
}
