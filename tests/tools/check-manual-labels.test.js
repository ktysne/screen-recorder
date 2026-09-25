'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const checker = require('../../tools/check-manual-labels');

test('マニュアルの文言が UiLabels の定数と一致する', () => {
  const source = 'internal static class UiLabels { public const string Stop = "録画を停止"; }';
  const manual = '<span data-ui-label="録画を停止">録画を停止</span>';
  assert.deepEqual(checker.checkLabels(manual, source), []);
});

test('一致しないラベルを列挙できる', () => {
  const source = 'public const string Stop = "録画を停止";';
  const manual = '<span data-ui-label="録画を終了">録画を終了</span>';
  assert.deepEqual(checker.checkLabels(manual, source), [{ attribute: '録画を終了', text: '録画を終了' }]);
});

test('C# の Unicode と改行エスケープを復号して照合する', () => {
  const source = String.raw`public const string Capture = "画面\u64AE\u5F71"; public const string Other = "行\nをまたぐ";`;
  assert.equal(checker.decodeCSharpString(String.raw`画面\u64AE\u5F71`), '画面撮影');
  assert.equal(checker.decodeCSharpString(String.raw`行\nをまたぐ`), '行\nをまたぐ');
  const manual = '<span data-ui-label="画面撮影">画面撮影</span>';
  assert.deepEqual(checker.checkLabels(manual, source), []);
});
