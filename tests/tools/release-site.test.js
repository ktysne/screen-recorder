'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const crypto = require('node:crypto');
const release = require('../../tools/release-site');

function temporaryDirectory() {
  return fs.mkdtempSync(path.join(os.tmpdir(), 'screen-recorder-release-test-'));
}

function responseWithBody(body, status = 200) {
  const bytes = Buffer.from(body);
  return { status, ok: status >= 200 && status < 300, async arrayBuffer() { return bytes; } };
}

function updateManifest(version, schema = 2) {
  const url = schema === 2 ? release.githubDownloadUrlOf(version) : release.downloadUrlOf(version);
  return { schema, latest: { version, url, sha256: 'a'.repeat(64), releasedAt: '2026-09-30' } };
}

function manifestFetch(responses) {
  const calls = [];
  return {
    calls,
    async fetch(url) {
      calls.push(url);
      const response = responses[url];
      if (typeof response === 'function') return response();
      if (!response) return { status: 404, ok: false };
      return { status: 200, ok: true, async json() { return response; } };
    },
  };
}

function ghStub(releases = []) {
  const calls = [];
  return {
    calls,
    runGh(args) {
      calls.push(args);
      if (args[0] === 'release' && args[1] === 'list') return JSON.stringify(releases);
      return '';
    },
  };
}

test('X.Y.Z の数値要素を桁数に依存せず比較する', () => {
  assert.equal(release.compareVersions('1.9.99', '1.10.0'), -1);
  assert.equal(release.compareVersions('1.10.0', '1.10.0'), 0);
  assert.equal(release.compareVersions('12.0.0', '2.99.99'), 1);
  assert.equal(release.isValidVersion('1.2.3'), true);
  assert.equal(release.isValidVersion('1.2'), false);
});

test('generate は schema 2 と GitHub Releases URL を作り、配布ページも同じ URL を使う', () => {
  const dir = temporaryDirectory();
  try {
    const zip = path.join(dir, 'release.zip');
    const ffmpegInfo = path.join(dir, 'ffmpeg-version.txt');
    fs.writeFileSync(zip, 'dummy archive');
    fs.writeFileSync(ffmpegInfo, 'ffmpeg version 7.1 configuration: --enable-shared');
    const { output, manifest, legacyManifest } = release.generateFiles({ version: '0.0.1', zip, out: path.join(dir, 'site'), releasedAt: '2026-09-30', ffmpegInfo });
    const expectedUrl = 'https://github.com/ktysne/screen-recorder/releases/download/v0.0.1/ScreenRecorder-0.0.1-win-x64.zip';
    const generated = JSON.parse(fs.readFileSync(path.join(output, 'update-v2.json'), 'utf8'));
    const page = fs.readFileSync(path.join(output, 'index.html'), 'utf8');
    assert.equal(manifest.schema, 2);
    assert.equal(manifest.latest.url, expectedUrl);
    assert.equal(generated.latest.sha256, crypto.createHash('sha256').update('dummy archive').digest('hex'));
    assert.match(generated.latest.sha256, /^[a-f0-9]{64}$/);
    assert.equal(legacyManifest, null);
    assert.equal(fs.existsSync(path.join(output, 'update.json')), false);
    assert.ok(page.includes(expectedUrl));
    assert.equal(release.githubDownloadUrlOf('2.3.4'), 'https://github.com/ktysne/screen-recorder/releases/download/v2.3.4/ScreenRecorder-2.3.4-win-x64.zip');
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('legacy-site は schema 1 も同じ zip の SHA-256 で生成し、update-v2.json だけを最後に送る', () => {
  const dir = temporaryDirectory();
  try {
    const zip = path.join(dir, 'release.zip');
    const ffmpegInfo = path.join(dir, 'ffmpeg-version.txt');
    const output = path.join(dir, 'site');
    fs.writeFileSync(zip, 'same archive');
    fs.writeFileSync(ffmpegInfo, 'ffmpeg version 7.1 configuration: --enable-shared');
    const result = release.generateFiles({ version: '1.2.3', zip, out: output, releasedAt: '2026-09-30', ffmpegInfo, legacySite: true });
    const current = JSON.parse(fs.readFileSync(path.join(output, 'update-v2.json'), 'utf8'));
    const legacy = JSON.parse(fs.readFileSync(path.join(output, 'update.json'), 'utf8'));
    assert.equal(current.schema, 2);
    assert.equal(legacy.schema, 1);
    assert.equal(current.latest.sha256, legacy.latest.sha256);
    assert.equal(legacy.latest.url, 'https://ktysne.info/screen-recorder/archives/ScreenRecorder-1.2.3-win-x64.zip');
    assert.equal(result.legacyManifest.latest.sha256, current.latest.sha256);
    assert.deepEqual(release.buildUploadItems({ version: '1.2.3', out: output, zip, legacySite: true }).map(item => item.name), [
      'ScreenRecorder-1.2.3-win-x64.zip', 'app-icon-256.png', 'manual.html', 'license.html', 'index.html', 'update.json', 'update-v2.json',
    ]);
    assert.deepEqual(release.buildUploadItems({ version: '1.2.3', out: output, zip }).map(item => item.name), [
      'app-icon-256.png', 'manual.html', 'license.html', 'index.html', 'update-v2.json',
    ]);
    release.generateFiles({ version: '1.2.3', zip, out: output, releasedAt: '2026-09-30', ffmpegInfo });
    assert.equal(fs.existsSync(path.join(output, 'update.json')), false);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('版確認は update-v2.json を読み、両方が 404 なら初回公開にする', async () => {
  const fetch = manifestFetch({});
  assert.deepEqual(await release.checkPublishedVersion('0.0.1', { fetchImpl: fetch.fetch }), { firstRelease: true });
  assert.deepEqual(fetch.calls, ['https://ktysne.info/screen-recorder/update-v2.json', 'https://ktysne.info/screen-recorder/update.json']);
});

test('schema 2 がある場合は旧版情報を読まずに版を比較する', async () => {
  const fetch = manifestFetch({ 'https://ktysne.info/screen-recorder/update-v2.json': updateManifest('1.2.3') });
  assert.deepEqual(await release.checkPublishedVersion('1.2.4', { fetchImpl: fetch.fetch }), { firstRelease: false, publishedVersion: '1.2.3' });
  assert.deepEqual(fetch.calls, ['https://ktysne.info/screen-recorder/update-v2.json']);
  await assert.rejects(release.checkPublishedVersion('1.2.3', { fetchImpl: fetch.fetch }), /より大きくありません/);
});

test('update-v2.json が無く update.json だけなら legacy-site を案内して拒否する', async () => {
  const fetch = manifestFetch({ 'https://ktysne.info/screen-recorder/update.json': updateManifest('1.2.3', 1) });
  await assert.rejects(release.checkPublishedVersion('1.2.4', { fetchImpl: fetch.fetch }), /--legacy-site/);
  assert.deepEqual(await release.checkPublishedVersion('1.2.4', { legacySite: true, fetchImpl: fetch.fetch }), { firstRelease: false, publishedVersion: '1.2.3' });
});

test('legacy-site は schema 2 と update.json の両方より新しい版だけを受け付ける', async () => {
  const fetch = manifestFetch({
    'https://ktysne.info/screen-recorder/update-v2.json': updateManifest('1.2.3'),
    'https://ktysne.info/screen-recorder/update.json': updateManifest('1.2.4', 1),
  });
  await assert.rejects(release.checkPublishedVersion('1.2.4', { legacySite: true, fetchImpl: fetch.fetch }), /旧版用 update\.json/);
  assert.deepEqual(await release.checkPublishedVersion('1.2.5', { legacySite: true, fetchImpl: fetch.fetch }), { firstRelease: false, publishedVersion: '1.2.3' });
});

test('配布ページが参照するアイコンを出力先の assets に置く', () => {
  const dir = temporaryDirectory();
  try {
    const ffmpegInfo = path.join(dir, 'ffmpeg-version.txt');
    fs.writeFileSync(ffmpegInfo, 'ffmpeg version 7.1 configuration: --enable-shared');
    const output = release.generatePages({ version: '1.2.3', out: path.join(dir, 'site'), releasedAt: '2026-09-27', ffmpegInfo });
    const index = fs.readFileSync(path.join(output, 'index.html'), 'utf8');
    const referenced = [...index.matchAll(/(?:src|href)="(assets\/[^\"]+)"/g)].map(match => match[1]);
    assert.ok(referenced.length > 0);
    for (const asset of referenced) assert.ok(fs.existsSync(path.join(output, asset)), asset);
    assert.ok(index.includes(release.githubDownloadUrlOf('1.2.3')));
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('公開中のバージョンより古い版と異なる SHA-256 の同版再送を拒否する', () => {
  assert.throws(() => release.decideUploadAgainstPublished('0.1.0', 'a'.repeat(64), { version: '0.2.0', sha256: 'b'.repeat(64) }), /新しい/);
  assert.throws(() => release.decideUploadAgainstPublished('0.2.0', 'a'.repeat(64), { version: '0.2.0', sha256: 'b'.repeat(64) }), /一致しない/);
  assert.deepEqual(release.decideUploadAgainstPublished('0.2.0', 'a'.repeat(64), { version: '0.2.0', sha256: 'a'.repeat(64) }), { skipZip: true });
});

test('FTP 計画は update-v2.json を最後にし、通常発行では zip と update.json を含めない', () => {
  const regular = release.buildUploadItems({ version: '1.2.3', out: 'build/release' }).map(item => item.name);
  const legacy = release.buildUploadItems({ version: '1.2.3', out: 'build/release', legacySite: true }).map(item => item.name);
  assert.deepEqual(regular, ['app-icon-256.png', 'manual.html', 'license.html', 'index.html', 'update-v2.json']);
  assert.deepEqual(legacy, ['ScreenRecorder-1.2.3-win-x64.zip', 'app-icon-256.png', 'manual.html', 'license.html', 'index.html', 'update.json', 'update-v2.json']);
});

test('GitHub 認証とタグ一覧を確認し、既存 Release の同じ zip を再利用する', async () => {
  const body = 'published archive';
  const localSha = crypto.createHash('sha256').update(body).digest('hex');
  const gh = ghStub([{ tagName: 'v1.2.3', isDraft: false }]);
  const urls = [];
  let creates = 0;
  const result = await release.uploadRelease({ version: '1.2.3', zipPath: 'local.zip', localSha256: localSha }, {
    runGh(args) { const value = gh.runGh(args); if (args[1] === 'create') creates += 1; return value; },
    async fetchImpl(url, options) { urls.push([url, options.redirect]); return responseWithBody(body); },
    wait: async () => {},
  });
  assert.deepEqual(result, { created: false, verified: true });
  assert.deepEqual(gh.calls.slice(0, 2), [
    ['auth', 'status'],
    ['release', 'list', '--repo', 'ktysne/screen-recorder', '--json', 'tagName,isDraft'],
  ]);
  assert.equal(urls[0][0], release.githubDownloadUrlOf('1.2.3'));
  assert.equal(urls[0][1], 'follow');
  assert.equal(creates, 0);
});

test('既存 Release の zip が一致しない場合は作成と削除を行わない', async () => {
  const gh = ghStub([{ tagName: 'v1.2.3', isDraft: false }]);
  let fetches = 0;
  let waits = 0;
  await assert.rejects(release.uploadRelease({ version: '1.2.3', zipPath: 'local.zip', localSha256: 'a'.repeat(64) }, {
    runGh: gh.runGh,
    async fetchImpl() { fetches += 1; return responseWithBody('different archive'); },
    async wait() { waits += 1; },
  }), /SHA-256 が一致しません/);
  assert.equal(fetches, 1);
  assert.equal(waits, 0);
  assert.equal(gh.calls.some(args => args[1] === 'create' || args[1] === 'delete'), false);
});

test('既存 Release に同名 zip が無い場合は作成と削除を行わず停止する', async () => {
  const gh = ghStub([{ tagName: 'v1.2.3', isDraft: false }]);
  await assert.rejects(release.uploadRelease({ version: '1.2.3', zipPath: 'local.zip', localSha256: 'a'.repeat(64) }, {
    runGh: gh.runGh,
    async fetchImpl() { return { status: 404, ok: false }; },
  }), /ScreenRecorder-1\.2\.3-win-x64\.zip を取得できませんでした/);
  assert.equal(gh.calls.some(args => args[1] === 'create' || args[1] === 'delete'), false);
});

test('既存の下書き Release は削除コマンドを案内して停止する', async () => {
  const gh = ghStub([{ tagName: 'v1.2.3', isDraft: true }]);
  let fetches = 0;
  await assert.rejects(release.uploadRelease({ version: '1.2.3', zipPath: 'local.zip', localSha256: 'a'.repeat(64) }, {
    runGh: gh.runGh,
    async fetchImpl() { fetches += 1; return responseWithBody('archive'); },
  }), /gh release delete v1\.2\.3 --repo ktysne\/screen-recorder --yes/);
  assert.equal(fetches, 0);
  assert.equal(gh.calls.some(args => args[1] === 'create' || args[1] === 'delete'), false);
});

test('Release 作成は --verify-tag を使い、別名 zip を所定名で写し、タグを削除しない', async () => {
  const dir = temporaryDirectory();
  try {
    const zipPath = path.join(dir, 'custom-name.zip');
    const body = 'release archive';
    fs.writeFileSync(zipPath, body);
    const localSha256 = crypto.createHash('sha256').update(body).digest('hex');
    const gh = ghStub();
    let copiedAsset;
    const result = await release.uploadRelease({ version: '1.2.3', zipPath, localSha256 }, {
      runGh(args) {
        if (args[1] === 'create') {
          copiedAsset = args[3];
          assert.equal(path.basename(copiedAsset), 'ScreenRecorder-1.2.3-win-x64.zip');
          assert.equal(fs.readFileSync(copiedAsset, 'utf8'), body);
        }
        return gh.runGh(args);
      },
      async fetchImpl() { return responseWithBody(body); },
      wait: async () => {},
    });
    const createArgs = gh.calls.find(args => args[1] === 'create');
    assert.deepEqual(createArgs.slice(0, 4), ['release', 'create', 'v1.2.3', copiedAsset]);
    assert.ok(createArgs.includes('--verify-tag'));
    assert.ok(createArgs.includes('--title'));
    assert.ok(createArgs.includes('ScreenRecorder v1.2.3'));
    const notes = createArgs[createArgs.indexOf('--notes') + 1];
    assert.match(notes, /^ダウンロードするファイルは `ScreenRecorder-1\.2\.3-win-x64\.zip` です。/);
    assert.match(notes, /「Source code」.*アプリは入っていません。/);
    assert.equal(gh.calls.some(args => args[1] === 'delete' || args.includes('--cleanup-tag')), false);
    assert.deepEqual(result, { created: true, verified: true });
    assert.equal(fs.existsSync(path.dirname(copiedAsset)), false);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('一時 zip の後始末に失敗しても警告だけで公開を続ける', async () => {
  const dir = temporaryDirectory();
  try {
    const zipPath = path.join(dir, 'custom-name.zip');
    const body = 'release archive';
    fs.writeFileSync(zipPath, body);
    const localSha256 = crypto.createHash('sha256').update(body).digest('hex');
    const gh = ghStub();
    const warnings = [];
    const result = await release.uploadRelease({ version: '1.2.3', zipPath, localSha256 }, {
      runGh: gh.runGh,
      async fetchImpl() { return responseWithBody(body); },
      wait: async () => {},
      warn(message) { warnings.push(message); },
      fileOps: {
        createTempDirectory() { const temp = path.join(dir, 'release-temp'); fs.mkdirSync(temp); return temp; },
        copyFile: fs.copyFileSync,
        removeTempDirectory() { throw new Error('locked'); },
      },
    });
    assert.deepEqual(result, { created: true, verified: true });
    assert.equal(warnings.length, 1);
    assert.match(warnings[0], /削除できませんでした/);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('公開 URL の取得は 5 秒間隔で最大 6 回再試行する', async () => {
  const waits = [];
  let fetches = 0;
  const body = 'ready';
  const sha = crypto.createHash('sha256').update(body).digest('hex');
  await release.verifyPublicAsset('https://github.com/example/file', sha, {
    async fetchImpl() {
      fetches += 1;
      return fetches < 3 ? { status: 404, ok: false } : responseWithBody(body);
    },
    async wait(duration) { waits.push(duration); },
  });
  assert.equal(fetches, 3);
  assert.deepEqual(waits, [5000, 5000]);

  fetches = 0;
  await assert.rejects(release.verifyPublicAsset('https://github.com/example/file', sha, {
    async fetchImpl() { fetches += 1; return responseWithBody('wrong'); },
    async wait() {},
  }), /SHA-256 が一致しません/);
  assert.equal(fetches, 6);
});

test('照合に失敗したときは今回作った Release だけを削除し、タグ削除を指定しない', async () => {
  const dir = temporaryDirectory();
  try {
    const zipPath = path.join(dir, 'ScreenRecorder-1.2.3-win-x64.zip');
    fs.writeFileSync(zipPath, 'local');
    const gh = ghStub();
    await assert.rejects(release.uploadRelease({ version: '1.2.3', zipPath, localSha256: 'a'.repeat(64) }, {
      runGh: gh.runGh,
      async fetchImpl() { return responseWithBody('different'); },
      wait: async () => {},
    }), /この実行で作成した Release v1\.2\.3 を削除しました/);
    assert.deepEqual(gh.calls.at(-1), ['release', 'delete', 'v1.2.3', '--repo', 'ktysne/screen-recorder', '--yes']);
    assert.equal(gh.calls.some(args => args.includes('--cleanup-tag')), false);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('dry-run は Release 作成・削除と FTP クライアント生成を行わず送信計画を返す', async () => {
  const dir = temporaryDirectory();
  try {
    const zipPath = path.join(dir, 'ScreenRecorder-1.2.3-win-x64.zip');
    const output = path.join(dir, 'site');
    fs.mkdirSync(path.join(output, 'assets'), { recursive: true });
    fs.writeFileSync(zipPath, 'local zip');
    for (const name of ['app-icon-256.png', 'manual.html', 'license.html', 'index.html']) fs.writeFileSync(path.join(name === 'app-icon-256.png' ? path.join(output, 'assets') : output, name), 'site file');
    const sha = crypto.createHash('sha256').update(fs.readFileSync(zipPath)).digest('hex');
    fs.writeFileSync(path.join(output, 'update-v2.json'), JSON.stringify({ schema: 2, latest: { version: '1.2.3', url: release.githubDownloadUrlOf('1.2.3'), sha256: sha } }));
    const gh = ghStub();
    const logs = [];
    const fetch = manifestFetch({});
    const result = await release.uploadFiles({ version: '1.2.3', zip: zipPath, out: output, dryRun: true }, {
      runGh: gh.runGh,
      fetchImpl: fetch.fetch,
      loadConfig: () => ({ host: 'ftp.example', port: 21, remoteRoot: '/site' }),
      createFtpClient() { throw new Error('FTP client must not be created'); },
      log(message) { logs.push(message); },
    });
    assert.deepEqual(result.targets.map(item => item.name), ['app-icon-256.png', 'manual.html', 'license.html', 'index.html', 'update-v2.json']);
    assert.equal(gh.calls.some(args => args[1] === 'create' || args[1] === 'delete'), false);
    assert.ok(logs.some(message => message.includes('update-v2.json')));
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('upload は同じ版と SHA-256 の legacy-site 再試行を許す', async () => {
  const dir = temporaryDirectory();
  try {
    const version = '1.2.3';
    const body = 'retry archive';
    const zipPath = path.join(dir, release.zipFileName(version));
    const output = path.join(dir, 'site');
    const assetDirectory = path.join(output, 'assets');
    fs.mkdirSync(assetDirectory, { recursive: true });
    fs.writeFileSync(zipPath, body);
    for (const name of ['app-icon-256.png', 'manual.html', 'license.html', 'index.html']) {
      fs.writeFileSync(path.join(name === 'app-icon-256.png' ? assetDirectory : output, name), 'site file');
    }
    const sha256 = crypto.createHash('sha256').update(body).digest('hex');
    const current = { schema: 2, latest: { version, url: release.githubDownloadUrlOf(version), sha256 } };
    const legacy = { schema: 1, latest: { version, url: release.downloadUrlOf(version), sha256 } };
    fs.writeFileSync(path.join(output, 'update-v2.json'), JSON.stringify(current));
    fs.writeFileSync(path.join(output, 'update.json'), JSON.stringify(legacy));
    const gh = ghStub([{ tagName: `v${version}`, isDraft: false }]);
    const result = await release.uploadFiles({ version, zip: zipPath, out: output, legacySite: true, dryRun: true }, {
      runGh: gh.runGh,
      async fetchImpl(url) {
        if (url === release.githubDownloadUrlOf(version)) return responseWithBody(body);
        if (url.endsWith('/update-v2.json')) return { status: 404, ok: false };
        if (url.endsWith('/update.json')) return { status: 200, ok: true, async json() { return legacy; } };
        throw new Error(`unexpected URL: ${url}`);
      },
      loadConfig: () => ({ host: 'ftp.example', port: 21, remoteRoot: '/site' }),
      createFtpClient() { throw new Error('FTP client must not be created'); },
      log() {},
    });
    assert.equal(result.release.verified, true);
    assert.equal(result.targets[0].name, release.zipFileName(version));
    assert.equal(result.targets.at(-2).name, 'update.json');
    assert.equal(result.targets.at(-1).name, 'update-v2.json');
    assert.equal(gh.calls.some(args => args[1] === 'create' || args[1] === 'delete'), false);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('コマンドラインは legacy-site を受け取り、generate-pages では拒否する', () => {
  assert.equal(release.parseArgs(['generate', '--version', '1.2.3', '--zip', 'archive.zip', '--legacy-site']).legacySite, true);
  assert.throws(() => release.parseArgs(['generate-pages', '--version', '1.2.3', '--legacy-site']), /generate-pages/);
});

test('ffmpeg のバージョン情報を HTML 用にエスケープする', () => {
  assert.equal(release.ffmpegBuildInfoFrom('ffmpeg version n7.1 <lgpl> & "x" --enable-shared'), 'ffmpeg version n7.1 &lt;lgpl&gt; &amp; &quot;x&quot; --enable-shared');
  assert.throws(() => release.ffmpegBuildInfoFrom(''), /バージョンの情報/);
  assert.throws(() => release.ffmpegBuildInfoFrom('configuration: --enable-gpl --enable-libx264'), /--enable-gpl/);
  assert.throws(() => release.ffmpegBuildInfoFrom('configuration: --enable-nonfree'), /--enable-nonfree/);
  assert.throws(() => release.ffmpegBuildInfoFrom('configuration: --enable-static'), /--enable-shared/);
});
