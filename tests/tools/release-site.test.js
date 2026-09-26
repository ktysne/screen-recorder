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
  assert.equal(release.ffmpegBuildInfoFrom('ffmpeg version n7.1 <lgpl> & "x" --enable-shared'), 'ffmpeg version n7.1 &lt;lgpl&gt; &amp; &quot;x&quot; --enable-shared');
});

test('GPL か nonfree の成分を含む ffmpeg のビルドは拒否する', () => {
  assert.throws(() => release.ffmpegBuildInfoFrom('configuration: --enable-gpl --enable-libx264'), /--enable-gpl/);
  assert.throws(() => release.ffmpegBuildInfoFrom('configuration: --enable-nonfree'), /--enable-nonfree/);
});

test('LGPL v3 として構成したビルドは受け付ける', () => {
  assert.match(release.ffmpegBuildInfoFrom('configuration: --enable-version3 --enable-shared'), /--enable-version3/);
});

test('shared でない ffmpeg のビルドは拒否する', () => {
  assert.throws(() => release.ffmpegBuildInfoFrom('configuration: --enable-static'), /--enable-shared/);
});

test('公開中の版より古い版は転送しない', () => {
  assert.throws(() => release.decideUploadAgainstPublished('0.1.0', 'a'.repeat(64), { version: '0.2.0', sha256: 'b'.repeat(64) }), /新しい/);
});

test('公開中と同じ版は、同じ zip の再試行だけを許し、zip は送り直さない', () => {
  assert.deepEqual(release.decideUploadAgainstPublished('0.2.0', 'a'.repeat(64), { version: '0.2.0', sha256: 'a'.repeat(64) }), { skipZip: true });
  assert.throws(() => release.decideUploadAgainstPublished('0.2.0', 'a'.repeat(64), { version: '0.2.0', sha256: 'b'.repeat(64) }), /一致しない/);
});

test('初回の公開と、公開中より新しい版は zip も含めて転送する', () => {
  assert.deepEqual(release.decideUploadAgainstPublished('0.1.0', 'a'.repeat(64), null), { skipZip: false });
  assert.deepEqual(release.decideUploadAgainstPublished('0.3.0', 'a'.repeat(64), { version: '0.2.0', sha256: 'b'.repeat(64) }), { skipZip: false });
});

function fakeClient({ renameFailures = 0, removeFails = false, uploadFails = false } = {}) {
  const calls = [];
  let renames = 0;
  return {
    calls,
    async rename(from, to) { calls.push(['rename', from, to]); if (renames++ < renameFailures) throw new Error('rename failed'); },
    async remove(name) { calls.push(['remove', name]); if (removeFails) throw new Error('remove failed'); },
    async uploadFrom(local, name) { calls.push(['upload', name]); if (uploadFails) throw new Error('upload failed'); },
  };
}

test('update.json は上書きの改名ができればそれだけで切り替える', async () => {
  const client = fakeClient();
  await release.replaceRemoteFile(client, 'update.json.uploading', 'update.json', 'local', true);
  assert.deepEqual(client.calls, [['rename', 'update.json.uploading', 'update.json']]);
});

test('上書きの改名ができないサーバでは、消してから改名する', async () => {
  const client = fakeClient({ renameFailures: 1 });
  await release.replaceRemoteFile(client, 'update.json.uploading', 'update.json', 'local', true);
  assert.deepEqual(client.calls.map(call => call[0]), ['rename', 'remove', 'rename']);
});

test('消した後の改名にも失敗したら、公開名へ直接送り直して update.json を残す', async () => {
  const client = fakeClient({ renameFailures: 2 });
  await release.replaceRemoteFile(client, 'update.json.uploading', 'update.json', 'local', true);
  assert.deepEqual(client.calls.at(-1), ['upload', 'update.json']);
});

test('送り直しにも失敗したら、手で改名する手順を示して止める', async () => {
  const client = fakeClient({ renameFailures: 2, uploadFails: true });
  await assert.rejects(release.replaceRemoteFile(client, 'update.json.uploading', 'update.json', 'local', true), /改名してください/);
});

test('ffmpeg の版の情報が無いときは生成を止める', () => {
  assert.throws(() => release.ffmpegBuildInfoFrom(''), /版の情報/);
});
