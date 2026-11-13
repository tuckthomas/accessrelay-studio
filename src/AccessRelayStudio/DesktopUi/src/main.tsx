import React from 'react';
import ReactDOM from 'react-dom/client';
import { App } from './App';
import { DetachedGameplayView } from './DetachedGameplayView';
import { initializeTheme } from './theme';
import './styles.css';

initializeTheme();
const detached = new URLSearchParams(location.search).get('surface') === 'gameplay';
if (detached) document.documentElement.dataset.surface = 'gameplay';
const Surface = detached ? DetachedGameplayView : App;
ReactDOM.createRoot(document.getElementById('root')!).render(<React.StrictMode><Surface /></React.StrictMode>);
