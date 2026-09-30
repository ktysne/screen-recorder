'use strict';

const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const crypto = require('node:crypto');
const { execFileSync } = require('node:child_process');
const readline = require('node:readline/promises');

const ROOT = path.resolve(__dirname, '..');
const BASE_URL = 'https://ktysne.info/screen-recorder';
const GITHUB_REPOSITORY = 'ktysne/screen-recorder';
const REMOTE_ROOT_DEFAULT = '/ktysne.info/screen-recorder';
const DEFAULT_OUT = 'build/release';
const PAGE_NAMES = ['index.html', 'manual.html', 'license.html'];
const SITE_ASSET_NAMES = ['app-icon-256.png'];
const FORBIDDEN_FFMPEG_CONFIGURATIONS = ['--enable-gpl', '--enable-nonfree'];
const UPLOADING_SUFFIX = '.uploading';
const PREVIOUS_SUFFIX = '.previous';
const RELEASE_VERIFICATION_ATTEMPTS = 6;
const RELEASE_VERIFICATION_INTERVAL_MS = 5000;

function isValidVersion(version) {
  return /^\d+\.\d+\.\d+$/.test(version);
}

function compareVersions(left, right) {
  const a = left.split('.').map(BigInt);
  const b = right.split('.').map(BigInt);
  for (let i = 0; i < 3; i += 1) {
    if (a[i] < b[i]) return -1;
    if (a[i] > b[i]) return 1;
  }
  return 0;
}

function downloadUrlOf(version) {
  return `${BASE_URL}/archives/ScreenRecorder-${version}-win-x64.zip`;
}

function githubDownloadUrlOf(version) {
  return `https://github.com/${GITHUB_REPOSITORY}/releases/download/v${version}/ScreenRecorder-${version}-win-x64.zip`;
}

// Release には GitHub が「Source code」を自動で付けるので、アプリを使う人が落とすファイルを示す。
function releaseNotesOf(version) {
  return `ダウンロードするファイルは \`${zipFileName(version)}\` です。\n\n`
    + '「Source code」の 2 つは GitHub が自動で付けるソースコードの圧縮ファイルで、アプリは入っていません。';
}

function zipFileName(version) {
  return `ScreenRecorder-${version}-win-x64.zip`;
}

function localDateString(date = new Date()) {
  const twoDigits = value => String(value).padStart(2, '0');
  return `${date.getFullYear()}-${twoDigits(date.getMonth() + 1)}-${twoDigits(date.getDate())}`;
}

function sha256OfFile(zipPath) {
  return crypto.createHash('sha256').update(fs.readFileSync(zipPath)).digest('hex');
}

function buildUpdateManifest(version, sha256, releasedAt = localDateString()) {
  return {
    schema: 2,
    latest: { version, url: githubDownloadUrlOf(version), sha256, releasedAt },
  };
}

function buildLegacyUpdateManifest(version, sha256, releasedAt = localDateString()) {
  return {
    schema: 1,
    latest: { version, url: downloadUrlOf(version), sha256, releasedAt },
  };
}

function serializeUpdateManifest(manifest) {
  return `${JSON.stringify(manifest, null, 2)}\n`;
}

function renderTemplate(template, values) {
  const rendered = template.replace(/\{\{([A-Z0-9_]+)\}\}/g, (token, key) => {
    if (!Object.hasOwn(values, key)) throw new Error(`未対応の差し込み欄です: ${token}`);
    return values[key];
  });
  const remaining = rendered.match(/\{\{[^{}]+\}\}/g);
  if (remaining) throw new Error(`差し込み欄が残っています: ${[...new Set(remaining)].join(', ')}`);
  return rendered;
}

function escapeHtml(text) {
  return text.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
}

function ffmpegBuildInfoFrom(versionText) {
  const text = String(versionText ?? '').trim();
  if (!text) throw new Error('同梱する ffmpeg のバージョンの情報(ffmpeg -version の出力)が必要です');
  const forbidden = FORBIDDEN_FFMPEG_CONFIGURATIONS.filter(flag => text.includes(flag));
  if (forbidden.length > 0) throw new Error(`同梱できない ffmpeg のビルドです(${forbidden.join(', ')})。LGPL の shared ビルドを使ってください`);
  if (!text.includes('--enable-shared')) throw new Error('同梱できない ffmpeg のビルドです(--enable-shared がありません)。LGPL の shared ビルドを使ってください');
  return escapeHtml(text);
}

function readFfmpegBuildInfo(ffmpegInfo) {
  if (!ffmpegInfo) throw new Error('--ffmpeg-info に ffmpeg -version の出力のファイルを指定してください');
  return ffmpegBuildInfoFrom(fs.readFileSync(path.resolve(ROOT, ffmpegInfo), 'utf8'));
}

function writeSite(output, values) {
  for (const name of PAGE_NAMES) {
    const source = path.join(ROOT, 'site', name.replace('.html', '.template.html'));
    fs.writeFileSync(path.join(output, name), renderTemplate(fs.readFileSync(source, 'utf8'), values), 'utf8');
  }
  fs.mkdirSync(path.join(output, 'assets'), { recursive: true });
  for (const name of SITE_ASSET_NAMES) {
    fs.copyFileSync(path.join(ROOT, 'site', 'assets', name), path.join(output, 'assets', name));
  }
}

function generateFiles({ version, zip, out, releasedAt, ffmpegInfo, legacySite = false }) {
  if (!isValidVersion(version)) throw new Error(`バージョンは X.Y.Z 形式で指定してください: ${version}`);
  const zipPath = path.resolve(ROOT, zip);
  if (!fs.existsSync(zipPath)) throw new Error(`zip が見つかりません: ${zipPath}`);
  const output = path.resolve(ROOT, out);
  fs.mkdirSync(output, { recursive: true });
  const sha256 = sha256OfFile(zipPath);
  const manifest = buildUpdateManifest(version, sha256, releasedAt);
  const legacyManifest = legacySite ? buildLegacyUpdateManifest(version, sha256, manifest.latest.releasedAt) : null;
  const values = {
    VERSION: version,
    DOWNLOAD_URL: githubDownloadUrlOf(version),
    ZIP_NAME: zipFileName(version),
    RELEASED_AT: manifest.latest.releasedAt,
    FFMPEG_BUILD_INFO: readFfmpegBuildInfo(ffmpegInfo),
  };
  writeSite(output, values);
  fs.writeFileSync(path.join(output, 'update-v2.json'), serializeUpdateManifest(manifest), 'utf8');
  const legacyPath = path.join(output, 'update.json');
  if (legacyManifest) fs.writeFileSync(legacyPath, serializeUpdateManifest(legacyManifest), 'utf8');
  else if (fs.existsSync(legacyPath)) fs.rmSync(legacyPath);
  return { output, manifest, legacyManifest };
}

function generatePages({ version, out, releasedAt = localDateString(), ffmpegInfo }) {
  if (!isValidVersion(version)) throw new Error(`バージョンは X.Y.Z 形式で指定してください: ${version}`);
  const output = path.resolve(ROOT, out);
  fs.mkdirSync(output, { recursive: true });
  const values = { VERSION: version, DOWNLOAD_URL: githubDownloadUrlOf(version), ZIP_NAME: zipFileName(version), RELEASED_AT: releasedAt, FFMPEG_BUILD_INFO: readFfmpegBuildInfo(ffmpegInfo) };
  writeSite(output, values);
  return output;
}

function buildUploadItems({ version, out = DEFAULT_OUT, zip, legacySite = false }) {
  const output = path.resolve(ROOT, out);
  const items = [];
  if (legacySite) {
    items.push({ name: zipFileName(version), localPath: path.resolve(ROOT, zip || path.join(out, zipFileName(version))), remoteDir: 'archives' });
  }
  items.push(
    ...SITE_ASSET_NAMES.map(name => ({ name, localPath: path.join(output, 'assets', name), remoteDir: 'assets' })),
    { name: 'manual.html', localPath: path.join(output, 'manual.html'), remoteDir: '' },
    { name: 'license.html', localPath: path.join(output, 'license.html'), remoteDir: '' },
    { name: 'index.html', localPath: path.join(output, 'index.html'), remoteDir: '' },
  );
  if (legacySite) items.push({ name: 'update.json', localPath: path.join(output, 'update.json'), remoteDir: '' });
  items.push({ name: 'update-v2.json', localPath: path.join(output, 'update-v2.json'), remoteDir: '' });
  return items;
}

function verifyReleaseInputs(version, zipPath, out = DEFAULT_OUT, legacySite = false) {
  const resolvedZip = path.resolve(ROOT, zipPath);
  const currentPath = path.join(path.resolve(ROOT, out), 'update-v2.json');
  if (!fs.existsSync(resolvedZip) || !fs.existsSync(currentPath)) throw new Error('zip または update-v2.json がありません。先に release:generate を実行してください');
  const currentManifest = JSON.parse(fs.readFileSync(currentPath, 'utf8'));
  const sha256 = sha256OfFile(resolvedZip);
  if (currentManifest.schema !== 2 || currentManifest.latest?.version !== version || currentManifest.latest?.url !== githubDownloadUrlOf(version) || currentManifest.latest?.sha256 !== sha256) {
    throw new Error('update-v2.json のバージョン、URL、SHA-256 がアップロードする zip と一致しません。先に release:generate を実行してください');
  }
  if (legacySite) {
    const legacyPath = path.join(path.resolve(ROOT, out), 'update.json');
    if (!fs.existsSync(legacyPath)) throw new Error('旧版用 update.json がありません。--legacy-site を付けて release:generate を実行してください');
    const legacyManifest = JSON.parse(fs.readFileSync(legacyPath, 'utf8'));
    if (legacyManifest.schema !== 1 || legacyManifest.latest?.version !== version || legacyManifest.latest?.url !== downloadUrlOf(version) || legacyManifest.latest?.sha256 !== sha256 || legacyManifest.latest?.sha256 !== currentManifest.latest.sha256) {
      throw new Error('update.json と update-v2.json の版または SHA-256 が zip と一致しません。先に release:generate --legacy-site を実行してください');
    }
  }
  return sha256;
}

function decideUploadAgainstPublished(version, localSha256, published) {
  if (!published) return { skipZip: false };
  const order = compareVersions(version, published.version);
  if (order < 0) throw new Error(`公開中のバージョン ${published.version} のほうが新しいため、${version} は転送しません`);
  if (order === 0 && published.sha256 !== localSha256) {
    throw new Error(`公開中の ${version} と zip の SHA-256 が一致しないため、転送しません`);
  }
  return { skipZip: order === 0 };
}

async function replaceRemoteFile(client, temporaryName, finalName, finalExists) {
  try {
    await client.rename(temporaryName, finalName);
    return;
  } catch (renameError) {
    if (!finalExists) throw renameError;
  }
  const previousName = `${finalName}${PREVIOUS_SUFFIX}`;
  await client.rename(finalName, previousName);
  try {
    await client.rename(temporaryName, finalName);
  } catch (error) {
    try {
      await client.rename(previousName, finalName);
    } catch (restoreError) {
      throw new Error(`${finalName} を置き換えられず、元に戻すこともできませんでした。サーバ上の ${previousName} を ${finalName} に改名してください (${restoreError.message})`);
    }
    throw new Error(`${finalName} を置き換えられなかったため、公開中のものを元に戻しました (${error.message})`);
  }
  try { await client.remove(previousName); } catch (error) { console.warn(`${previousName} を消せませんでした (${error.message})`); }
}

async function fetchManifest(url, expectedSchema, fetchImpl) {
  const response = await fetchImpl(url, { signal: AbortSignal.timeout(15000), cache: 'no-store', redirect: 'follow' });
  if (response.status === 404) return null;
  if (!response.ok) throw new Error(`公開中の最新版情報を取得できません (HTTP ${response.status})`);
  const manifest = await response.json();
  const version = manifest?.latest?.version;
  const sha256 = manifest?.latest?.sha256;
  const expectedUrl = expectedSchema === 2 ? githubDownloadUrlOf(version) : downloadUrlOf(version);
  if (manifest?.schema !== expectedSchema || !isValidVersion(version) || manifest.latest?.url !== expectedUrl || !/^[a-f0-9]{64}$/i.test(sha256 || '')) {
    throw new Error(`公開中の schema ${expectedSchema} 最新版情報の形式が不正です`);
  }
  return { version, sha256: sha256.toLowerCase(), source: expectedSchema === 2 ? 'v2' : 'legacy' };
}

async function readPublishedState({ legacySite = false, fetchImpl = fetch } = {}) {
  const current = await fetchManifest(`${BASE_URL}/update-v2.json`, 2, fetchImpl);
  if (current) {
    const legacy = legacySite ? await fetchManifest(`${BASE_URL}/update.json`, 1, fetchImpl) : null;
    return { current, legacy };
  }
  const legacy = await fetchManifest(`${BASE_URL}/update.json`, 1, fetchImpl);
  return { current: legacy, legacy };
}

// build-package.bat が版を尋ねる前に表示する公開中の版。どちらの最新版情報も無ければ null。
async function publishedVersionOf(fetchImpl = fetch) {
  return (await readPublishedState({ fetchImpl })).current?.version ?? null;
}

async function checkPublishedVersion(version, { legacySite = false, fetchImpl = fetch } = {}) {
  const published = await readPublishedState({ legacySite, fetchImpl });
  if (!published.current) return { firstRelease: true };
  if (published.current.source === 'legacy' && !legacySite) {
    throw new Error('公開中の update-v2.json がなく旧版用 update.json だけがあります。旧版利用者へ更新を届けるため --legacy-site を付けてください');
  }
  if (compareVersions(version, published.current.version) <= 0) {
    throw new Error(`新しいバージョン ${version} は公開中のバージョン ${published.current.version} より大きくありません`);
  }
  if (legacySite && published.legacy && compareVersions(version, published.legacy.version) <= 0) {
    throw new Error(`新しいバージョン ${version} は旧版用 update.json のバージョン ${published.legacy.version} より大きくありません`);
  }
  return { firstRelease: false, publishedVersion: published.current.version };
}

function configFromEnvironment(env) {
  const prefix = 'SCREENRECORDER_FTP_';
  const keys = { host: 'HOST', port: 'PORT', user: 'USER', password: 'PASSWORD', remoteRoot: 'REMOTE_ROOT' };
  const config = {};
  for (const [key, suffix] of Object.entries(keys)) {
    if (env[prefix + suffix]) config[key] = env[prefix + suffix];
  }
  if (env[prefix + 'SECURE']) config.secure = env[prefix + 'SECURE'].toLowerCase() !== 'false';
  return config;
}

function loadConfig(configPath, env = process.env) {
  const filePath = path.resolve(ROOT, configPath || 'tools/deploy.config.json');
  let fileConfig = {};
  if (fs.existsSync(filePath)) fileConfig = JSON.parse(fs.readFileSync(filePath, 'utf8'));
  const config = { ...fileConfig, ...configFromEnvironment(env) };
  for (const key of ['host', 'user', 'password']) {
    if (!config[key]) throw new Error(`FTP 設定 ${key} がありません。設定ファイルまたは SCREENRECORDER_FTP_* 環境変数を確認してください。`);
  }
  config.port = Number(config.port || 21);
  config.remoteRoot = (config.remoteRoot || REMOTE_ROOT_DEFAULT).replace(/\/+$/, '');
  config.secure = config.secure !== false;
  return config;
}

function buildRemoteTargets(items, remoteRoot) {
  return items.map(item => ({
    ...item,
    remotePath: `${remoteRoot}/${item.remoteDir ? `${item.remoteDir}/` : ''}${item.name}`,
  }));
}

function runGh(args) {
  return execFileSync('gh', args, { cwd: ROOT, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] });
}

function parseReleaseList(output) {
  const releases = JSON.parse(output);
  if (!Array.isArray(releases)) throw new Error('GitHub Release 一覧の形式が不正です');
  return releases;
}

function responseError(response, url) {
  return new Error(`GitHub Releases の zip を取得できません (HTTP ${response.status}, ${url})`);
}

async function fetchPublicAssetSha256(url, fetchImpl) {
  const response = await fetchImpl(url, { redirect: 'follow', signal: AbortSignal.timeout(60000) });
  if (!response.ok) throw responseError(response, url);
  return crypto.createHash('sha256').update(Buffer.from(await response.arrayBuffer())).digest('hex');
}

async function verifyPublicAsset(url, expectedSha256, { fetchImpl = fetch, wait = ms => new Promise(resolve => setTimeout(resolve, ms)), attempts = RELEASE_VERIFICATION_ATTEMPTS, intervalMs = RELEASE_VERIFICATION_INTERVAL_MS } = {}) {
  let lastError = null;
  for (let attempt = 0; attempt < attempts; attempt += 1) {
    try {
      const actualSha256 = await fetchPublicAssetSha256(url, fetchImpl);
      if (actualSha256 === expectedSha256) return actualSha256;
      lastError = new Error(`公開 URL の SHA-256 が一致しません (expected=${expectedSha256}, actual=${actualSha256})`);
    } catch (error) {
      lastError = error;
    }
    if (attempt + 1 < attempts) await wait(intervalMs);
  }
  throw lastError || new Error('公開 URL の SHA-256 を確認できませんでした');
}

function defaultReleaseFileOps() {
  return {
    createTempDirectory: () => fs.mkdtempSync(path.join(os.tmpdir(), 'screen-recorder-release-')),
    copyFile: (source, destination) => fs.copyFileSync(source, destination),
    removeTempDirectory: directory => fs.rmSync(directory, { recursive: true, force: true }),
  };
}

async function uploadRelease({ version, zipPath, localSha256, dryRun = false, beforeCreate }, dependencies = {}) {
  const gh = dependencies.runGh || runGh;
  const fetchImpl = dependencies.fetchImpl || fetch;
  const wait = dependencies.wait || (ms => new Promise(resolve => setTimeout(resolve, ms)));
  const log = dependencies.log || console.log;
  const warn = dependencies.warn || console.warn;
  const fileOps = dependencies.fileOps || defaultReleaseFileOps();
  const tag = `v${version}`;
  const name = zipFileName(version);
  const url = githubDownloadUrlOf(version);

  gh(['auth', 'status']);
  const releases = parseReleaseList(gh(['release', 'list', '--repo', GITHUB_REPOSITORY, '--json', 'tagName,isDraft']));
  const existing = releases.find(release => release.tagName === tag);
  if (existing?.isDraft) {
    throw new Error(`GitHub Release ${tag} が下書きです。手動で削除してから再実行してください: gh release delete ${tag} --repo ${GITHUB_REPOSITORY} --yes`);
  }
  if (existing) {
    let publishedSha;
    try { publishedSha = await fetchPublicAssetSha256(url, fetchImpl); }
    catch (error) { throw new Error(`公開済み Release ${tag} の ${name} を取得できませんでした: ${error.message}`); }
    if (publishedSha !== localSha256) throw new Error(`公開済み Release ${tag} の ${name} は手元の zip と SHA-256 が一致しません`);
    await beforeCreate?.();
    log(`GitHub Release ${tag} と ${name} は同じ SHA-256 で公開済みです。Release の作成を省略します。`);
    return { created: false, verified: true };
  }

  await beforeCreate?.();
  if (dryRun) {
    log(`dry-run: gh release create ${tag} ${zipPath} --repo ${GITHUB_REPOSITORY} --verify-tag --title "ScreenRecorder ${tag}" --notes <${zipFileName(version)} の案内>`);
    return { created: false, verified: false, planned: true };
  }

  let tempDirectory = null;
  let assetPath = zipPath;
  try {
    if (path.basename(zipPath) !== name) {
      tempDirectory = fileOps.createTempDirectory();
      assetPath = path.join(tempDirectory, name);
      fileOps.copyFile(zipPath, assetPath);
    }
    gh(['release', 'create', tag, assetPath, '--repo', GITHUB_REPOSITORY, '--verify-tag', '--title', `ScreenRecorder ${tag}`, '--notes', releaseNotesOf(version)]);
    try {
      await verifyPublicAsset(url, localSha256, { fetchImpl, wait, ...(dependencies.retryOptions || {}) });
    } catch (error) {
      let cleanupError = null;
      try { gh(['release', 'delete', tag, '--repo', GITHUB_REPOSITORY, '--yes']); }
      catch (deleteError) { cleanupError = deleteError; }
      const cleanupNote = cleanupError ? ` Release は削除できませんでした: ${cleanupError.message}` : ` この実行で作成した Release ${tag} を削除しました。`;
      throw new Error(`公開 URL の zip を照合できませんでした。${cleanupNote} ${error.message}`);
    }
    log(`GitHub Release ${tag} の zip を SHA-256 で照合しました。`);
    return { created: true, verified: true };
  } finally {
    if (tempDirectory) {
      try { fileOps.removeTempDirectory(tempDirectory); }
      catch (error) { warn(`一時コピー ${tempDirectory} を削除できませんでした: ${error.message}`); }
    }
  }
}

async function assertUploadVersion(version, localSha256, legacySite, fetchImpl) {
  const published = await readPublishedState({ legacySite, fetchImpl });
  if (published.current?.source === 'legacy' && !legacySite) {
    throw new Error('公開中の update-v2.json がなく旧版用 update.json だけがあります。旧版利用者へ更新を届けるため --legacy-site を付けてください');
  }
  if (legacySite && published.legacy) {
    const order = compareVersions(version, published.legacy.version);
    if (order < 0 || (order === 0 && published.legacy.sha256 !== localSha256)) {
      throw new Error(`新しいバージョン ${version} は旧版用 update.json の版または SHA-256 と一致しません`);
    }
  }
  decideUploadAgainstPublished(version, localSha256, published.current);
  return published;
}

class UploadDeclinedError extends Error {}

async function askYesNo(question) {
  const rl = readline.createInterface({ input: process.stdin, output: process.stdout });
  try {
    return /^y(es)?$/i.test((await rl.question(question)).trim());
  } finally {
    rl.close();
  }
}

async function uploadFiles(options, dependencies = {}) {
  const items = buildUploadItems(options);
  const zipPath = path.resolve(ROOT, options.zip || path.join(options.out || DEFAULT_OUT, zipFileName(options.version)));
  const localSha256 = verifyReleaseInputs(options.version, zipPath, options.out || DEFAULT_OUT, Boolean(options.legacySite));
  for (const item of items) {
    if (!fs.existsSync(item.localPath)) throw new Error(`アップロード対象がありません: ${item.localPath}`);
    item.size = fs.statSync(item.localPath).size;
    if (item.size === 0) throw new Error(`アップロード対象が空です: ${item.name}`);
  }

  let config;
  let targets;
  let declined = false;
  const log = dependencies.log || console.log;
  const confirm = dependencies.confirm || askYesNo;
  // 確認は Release の作成を含む最初の外部変更より前に行う。断ったら Release も FTP も触らない。
  const release = await uploadRelease({
    version: options.version,
    zipPath,
    localSha256,
    dryRun: Boolean(options.dryRun),
    beforeCreate: async () => {
      await assertUploadVersion(options.version, localSha256, Boolean(options.legacySite), dependencies.fetchImpl || fetch);
      config = (dependencies.loadConfig || loadConfig)(options.config);
      targets = buildRemoteTargets(items, config.remoteRoot);
      log(`接続先: ${config.host}:${config.port}`);
      log('転送順:');
      for (const item of targets) log(`  ${item.name} (${item.size} bytes)`);
      if (options.dryRun || options.yes) return;
      if (!(await confirm('GitHub Release の公開とサイトへの転送を行いますか? (y/N): '))) {
        declined = true;
        throw new UploadDeclinedError();
      }
    },
  }, dependencies).catch(error => {
    if (declined && error instanceof UploadDeclinedError) return null;
    throw error;
  });
  if (declined) return { release, targets, declined: true };
  if (options.dryRun) return { release, targets };

  const createClient = dependencies.createFtpClient || (() => new (require('basic-ftp').Client)(30000));
  const client = createClient();
  try {
    await client.access({ host: config.host, port: config.port, user: config.user, password: config.password, secure: config.secure });
    for (const item of targets) {
      await client.ensureDir(`${config.remoteRoot}/${item.remoteDir}`.replace(/\/$/, ''));
      if (item.name === zipFileName(options.version)) {
        const existing = (await client.list()).find(entry => entry.name === item.name);
        if (existing) {
          // 公開中の zip は退避も差し替えもしない。退避の直後に切断されると旧版の利用者が取れなくなるため。
          // update.json は zip より後に送り、途中で失敗した再送では旧い版を指したまま残るので、zip そのものを取得して比べる。
          let publishedSha256;
          try {
            publishedSha256 = await fetchPublicAssetSha256(downloadUrlOf(options.version), dependencies.fetchImpl || fetch);
          } catch (error) {
            throw new Error(`公開中の archives/${item.name} を取得して確かめられないため止めます: ${error.message}`);
          }
          if (existing.size !== item.size || publishedSha256 !== localSha256) {
            throw new Error(`公開中の archives/${item.name} は手元の zip と一致しないため、差し替えずに止めます`);
          }
          log(`公開中の同じ zip を使います: ${item.name}`);
          continue;
        }
      }
      // 再送が途中で切れても公開中のファイルを壊さないよう、一時名で送って照合してから差し替える。
      const uploadName = `${item.name}${UPLOADING_SUFFIX}`;
      await client.uploadFrom(item.localPath, uploadName);
      const entries = await client.list();
      const remote = entries.find(entry => entry.name === uploadName);
      if (!remote || remote.size !== item.size) {
        throw new Error(`${item.name} の転送後サイズが一致しません (local=${item.size}, remote=${remote?.size ?? 'missing'})`);
      }
      await replaceRemoteFile(client, uploadName, item.name, entries.some(entry => entry.name === item.name));
      log(`転送・照合完了: ${item.name}`);
    }
  } finally {
    client.close();
  }
  return { release, targets };
}

function parseArgs(argv) {
  const command = argv[0];
  if (command === 'published-version') {
    if (argv.length > 1) throw new Error('published-version にはオプションを指定できません');
    return { command };
  }
  if (!['generate', 'generate-pages', 'check-version', 'upload'].includes(command)) throw new Error('published-version / generate / generate-pages / check-version / upload のいずれかを指定してください');
  const options = { command, version: null, out: DEFAULT_OUT, zip: null, config: null, yes: false, dryRun: false, legacySite: false };
  for (let i = 1; i < argv.length; i += 1) {
    const arg = argv[i];
    if (arg === '--yes') options.yes = true;
    else if (arg === '--dry-run') options.dryRun = true;
    else if (arg === '--legacy-site') options.legacySite = true;
    else if (['--version', '--zip', '--out', '--config', '--ffmpeg-info'].includes(arg)) {
      if (!argv[i + 1]) throw new Error(`${arg} に値が必要です`);
      options[{ '--version': 'version', '--zip': 'zip', '--out': 'out', '--config': 'config', '--ffmpeg-info': 'ffmpegInfo' }[arg]] = argv[++i];
    } else throw new Error(`不明なオプション: ${arg}`);
  }
  if (!options.version || !isValidVersion(options.version)) throw new Error('--version は X.Y.Z 形式で指定してください');
  if (command === 'generate' && !options.zip) throw new Error('generate には --zip が必要です');
  if (command === 'generate-pages' && (options.zip || options.legacySite)) throw new Error('generate-pages では --zip と --legacy-site を指定できません');
  return options;
}

async function main() {
  try {
    const options = parseArgs(process.argv.slice(2));
    if (options.command === 'published-version') {
      console.log((await publishedVersionOf()) ?? 'none');
    } else if (options.command === 'generate') {
      const { output, manifest, legacyManifest } = generateFiles(options);
      console.log(`生成しました: ${output}`);
      console.log(`ダウンロード URL: ${manifest.latest.url}`);
      if (legacyManifest) console.log(`旧版 URL: ${legacyManifest.latest.url}`);
    } else if (options.command === 'generate-pages') {
      console.log(`ページを生成しました: ${generatePages(options)}`);
    } else if (options.command === 'check-version') {
      const result = await checkPublishedVersion(options.version, options);
      console.log(result.firstRelease ? '初回公開として続行できます。' : `公開中のバージョン ${result.publishedVersion} より新しいバージョンです。`);
    } else {
      await uploadFiles(options);
    }
  } catch (error) {
    console.error(`エラー: ${error.message}`);
    process.exitCode = 1;
  }
}

if (require.main === module) main();

module.exports = {
  isValidVersion, compareVersions, downloadUrlOf, githubDownloadUrlOf, zipFileName, localDateString,
  buildUpdateManifest, buildLegacyUpdateManifest, serializeUpdateManifest, renderTemplate, generateFiles, generatePages,
  buildUploadItems, verifyReleaseInputs, checkPublishedVersion, publishedVersionOf, readPublishedState, configFromEnvironment,
  buildRemoteTargets, ffmpegBuildInfoFrom, decideUploadAgainstPublished, replaceRemoteFile,
  parseArgs, fetchPublicAssetSha256, verifyPublicAsset, uploadRelease, uploadFiles,
};
