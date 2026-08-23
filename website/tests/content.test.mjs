import test from 'node:test';
import assert from 'node:assert/strict';
import {
  audiences,
  deploymentOptions,
  hero,
  riskDisclaimer,
  siteLinks
} from '../src/lib/content.js';

test('self-hosted CTA leads to the public installation instructions', () => {
  assert.equal(siteLinks.install, 'https://github.com/UsmanSabir/AgentFox#installation');
  assert.equal(hero.primaryCta.href, siteLinks.install);
});

test('cloud CTA opens the approved early-access email', () => {
  assert.equal(
    siteLinks.earlyAccess,
    'mailto:tradingsmartnow@outlook.com?subject=AgentFox%20Managed%20Cloud%20Early%20Access'
  );
  assert.equal(hero.secondaryCta.href, siteLinks.earlyAccess);
});

test('homepage serves the approved buyer hierarchy', () => {
  assert.equal(audiences.length, 4);
  assert.equal(audiences[0].id, 'new-traders');
  assert.equal(audiences[0].featured, true);
  assert.deepEqual(audiences.slice(1).map(({ id }) => id), ['active-traders', 'developers', 'teams']);
});

test('deployment choices distinguish availability from early access', () => {
  assert.deepEqual(
    deploymentOptions.map(({ id, status }) => ({ id, status })),
    [
      { id: 'self-hosted', status: 'Available now' },
      { id: 'managed-cloud', status: 'Early access' }
    ]
  );
});

test('risk language rejects advice and guaranteed-return framing', () => {
  assert.match(riskDisclaimer, /not investment advice/i);
  assert.match(riskDisclaimer, /no guaranteed returns/i);
});
