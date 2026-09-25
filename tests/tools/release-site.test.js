'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const crypto = require('node:crypto');
const release = require('../../tools/release-site');

test('X.Y.Z の数値要素を桁数に依存せず比較する', () => {
  assert.equal(release.compareVersions('1.9.99', '1.10.0'), -1);
  assert.equal(release.compareVersions('1.10.0', '1.10.0'), 0);
  assert.equal(release.compareVersions('12.0.0', '2.99.99'), 1);
  assert.equal(release.isValidVersion('1.2.3'), true);
  assert.equal(release.isValidVersion('1.2'), false);
});

test('更新情報に schema、許可ホストの URL、SHA-256 を含める', () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'screen-recorder-release-'));
  try {
    const zip = path.join(dir, 'release.zip');
    fs.writeFileSync(zip, 'dummy archive');
    const manifest = release.buildUpdateManifest('0.0.1', zip, '2026-09-26');
    assert.equal(manifest.schema, 1);
    assert.equal(new URL(manifest.latest.url).hostname, 'ktysne.info');
    assert.equal(manifest.latest.url, 'https://ktysne.info/screen-recorder/archives/ScreenRecorder-0.0.1-win-x64.zip');
    assert.match(manifest.latest.sha256, /^[a-f0-9]{64}$/);
    assert.equal(manifest.latest.sha256, crypto.createHash('sha256').update('dummy archive').digest('hex'));
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('転送項目は update.json を最後にする', () => {
  const names = release.buildUploadItems({ version: '1.2.3', out: 'build/release' }).map(item => item.name);
  assert.deepEqual(names, ['ScreenRecorder-1.2.3-win-x64.zip', 'manual.html', 'license.html', 'index.html', 'update.json']);
});

test('版から配布 zip の URL を組み立てる', () => {
  assert.equal(release.downloadUrlOf('2.3.4'), 'https://ktysne.info/screen-recorder/archives/ScreenRecorder-2.3.4-win-x64.zip');
});

test('公開中の update.json が 404 の場合は初回公開として扱う', async () => {
  const result = await release.checkPublishedVersion('0.0.1', async () => ({ status: 404 }));
  assert.deepEqual(result, { firstRelease: true });
});

test('公開中の版より小さい版と同じ版を拒否する', async () => {
  const fetchImpl = async () => ({ ok: true, json: async () => ({ schema: 1, latest: { version: '1.2.0' } }) });
  await assert.rejects(release.checkPublishedVersion('1.1.99', fetchImpl), /より大きくありません/);
  await assert.rejects(release.checkPublishedVersion('1.2.0', fetchImpl), /より大きくありません/);
});

test('ffmpeg の版の情報は HTML としてエスケープして載せる', () => {
  assert.equal(release.ffmpegBuildInfoFrom('ffmpeg version n7.1 <lgpl> & "shared"'), 'ffmpeg version n7.1 &lt;lgpl&gt; &amp; &quot;shared&quot;');
});

test('GPL か nonfree の成分を含む ffmpeg のビルドは拒否する', () => {
  assert.throws(() => release.ffmpegBuildInfoFrom('configuration: --enable-gpl --enable-libx264'), /--enable-gpl/);
  assert.throws(() => release.ffmpegBuildInfoFrom('configuration: --enable-nonfree'), /--enable-nonfree/);
});

test('LGPL v3 として構成したビルドは受け付ける', () => {
  assert.match(release.ffmpegBuildInfoFrom('configuration: --enable-version3 --enable-shared'), /--enable-version3/);
});

test('ffmpeg の版の情報が無いときは、その旨を載せる', () => {
  assert.equal(release.ffmpegBuildInfoFrom(''), '同梱の ffmpeg の版の情報はありません。');
});
