'use strict';

const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.resolve(__dirname, '..');
const DEFAULT_LABELS = path.join(ROOT, 'src', 'ScreenRecorder.App', 'UiLabels.cs');
const DEFAULT_MANUAL = path.join(ROOT, 'site', 'manual.template.html');

function decodeCSharpString(value) {
  return value.replace(/\\(u[\da-fA-F]{4}|U[\da-fA-F]{8}|x[\da-fA-F]{1,4}|[\\"'0abfnrtv])/g, (_, escape) => {
    const named = { '\\': '\\', '"': '"', "'": "'", '0': '\0', a: '\x07', b: '\b', f: '\f', n: '\n', r: '\r', t: '\t', v: '\x0b' };
    if (Object.hasOwn(named, escape)) return named[escape];
    const digits = escape.slice(1);
    return String.fromCodePoint(parseInt(digits, 16));
  });
}

function extractUiLabels(source) {
  const labels = new Set();
  const declaration = /\bconst\s+string\s+\w+\s*=\s*"((?:[^"\\]|\\.)*)"\s*;/g;
  for (const match of source.matchAll(declaration)) labels.add(decodeCSharpString(match[1]));
  return labels;
}

function decodeHtml(text) {
  return text.replace(/&(#x[\da-f]+|#\d+|amp|lt|gt|quot|apos);/gi, (entity, code) => {
    if (code[0] === '#') {
      const value = code[1].toLowerCase() === 'x' ? parseInt(code.slice(2), 16) : parseInt(code.slice(1), 10);
      return String.fromCodePoint(value);
    }
    return ({ amp: '&', lt: '<', gt: '>', quot: '"', apos: "'" })[code.toLowerCase()];
  });
}

function extractManualLabels(source) {
  const visible = source.replace(/<!--[\s\S]*?-->/g, '');
  const labels = [];
  const element = /<([a-z][\w:-]*)\b([^>]*\bdata-ui-label\s*=\s*"([^"]*)"[^>]*)>([\s\S]*?)<\/\1\s*>/gi;
  for (const match of visible.matchAll(element)) {
    const value = decodeHtml(match[4].replace(/<[^>]*>/g, '')).replace(/\s+/g, ' ').trim();
    labels.push({ attribute: decodeHtml(match[3]), text: value });
  }
  return labels;
}

function checkLabels(manualSource, labelsSource) {
  const sourceLabels = extractUiLabels(labelsSource);
  return extractManualLabels(manualSource).filter(label => label.text !== label.attribute || !sourceLabels.has(label.attribute));
}

function main() {
  if (!fs.existsSync(DEFAULT_LABELS) || !fs.existsSync(DEFAULT_MANUAL)) {
    console.error(`[check-manual-labels] 必要なファイルがありません: ${!fs.existsSync(DEFAULT_LABELS) ? DEFAULT_LABELS : DEFAULT_MANUAL}`);
    process.exitCode = 2;
    return;
  }
  const failures = checkLabels(fs.readFileSync(DEFAULT_MANUAL, 'utf8'), fs.readFileSync(DEFAULT_LABELS, 'utf8'));
  if (failures.length) {
    console.error('[check-manual-labels] UiLabels.cs と一致しないラベルがあります:');
    for (const label of failures) console.error(`  data-ui-label="${label.attribute}" 文言="${label.text}"`);
    process.exitCode = 1;
    return;
  }
  console.log('[check-manual-labels] マニュアルのラベルは UiLabels.cs と一致しています。');
}

if (require.main === module) main();

module.exports = { decodeCSharpString, extractUiLabels, extractManualLabels, checkLabels };
