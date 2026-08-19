import { mount } from 'svelte';
import './app.css';
import TradingDashboard from './TradingDashboard.svelte';
import { setInjectedApiKey } from './api';

// The host frames this page and posts the management API key and its current theme once the frame
// loads. Accepting it is belt-and-braces: being same-origin, api.ts can already read the key from the
// shared sessionStorage. The message also carries the theme, which sessionStorage does not.
//
// Origin is checked because a framed page can be messaged by anyone; only our own origin is trusted.
window.addEventListener('message', (event) => {
  if (event.origin !== window.location.origin) return;
  const data = event.data;
  if (!data || data.type !== 'agentfox:context') return;

  if (typeof data.apiKey === 'string') setInjectedApiKey(data.apiKey);
  if (data.theme === 'light' || data.theme === 'dark') {
    document.documentElement.dataset.theme = data.theme;
    window.dispatchEvent(new CustomEvent('agentfox:themechange', { detail: data.theme }));
  }
});

export default mount(TradingDashboard, { target: document.getElementById('app')! });
