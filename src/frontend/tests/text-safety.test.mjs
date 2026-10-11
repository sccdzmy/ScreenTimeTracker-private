import test from 'node:test';
import assert from 'node:assert/strict';
import { escapeHtml, safeColor } from '../src/shared/lib/escape-html.ts';
test('hostile chart names cannot create tooltip markup', () => {
  const value = '<img src=x onerror="alert(1)">&\'test';
  assert.doesNotMatch(escapeHtml(value), /[<>]/);
  assert.match(escapeHtml(value), /&lt;img/);
  assert.match(escapeHtml(value), /&quot;/);
  assert.match(escapeHtml(value), /&#39;/);
});
test('colors have a single fixed CSS grammar', () => {
  assert.equal(safeColor('#123aBC'), '#123aBC');
  for (const color of ['red; background:url(https://evil.example)', '#fff', null, {}]) assert.equal(safeColor(color), '#888888');
});
