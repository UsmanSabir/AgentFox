import { Marked } from 'marked';
import DOMPurify from 'dompurify';
import { browser } from '$app/environment';

// GFM (tables, strikethrough, autolinks) with single-newline line breaks,
// which matches how chat/LLM output is typically written.
const marked = new Marked({ gfm: true, breaks: true });

let hooksInstalled = false;

function installHooks() {
  if (hooksInstalled || !browser) return;
  hooksInstalled = true;
  // Open links in a new tab and strip referrer/opener leakage.
  DOMPurify.addHook('afterSanitizeAttributes', (node) => {
    if (node.tagName === 'A' && node.getAttribute('href')) {
      node.setAttribute('target', '_blank');
      node.setAttribute('rel', 'noopener noreferrer');
    }
  });
}

/**
 * Render markdown (e.g. model output) to sanitized HTML.
 * Sanitization only runs in the browser; during SSR/prerender chat content
 * is always empty, so the raw pass never reaches a real user.
 */
export function renderMarkdown(src: string | null | undefined): string {
  const html = marked.parse(src ?? '', { async: false }) as string;
  if (!browser) return html;
  installHooks();
  return DOMPurify.sanitize(html);
}
