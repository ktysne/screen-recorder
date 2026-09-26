'use strict';

const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const readline = require('node:readline/promises');

const ROOT = path.resolve(__dirname, '..');
const BASE_URL = 'https://ktysne.info/screen-recorder';
const REMOTE_ROOT_DEFAULT = '/ktysne.info/screen-recorder';
const DEFAULT_OUT = 'build/release';
const PAGE_NAMES = ['index.html', 'manual.html', 'license.html'];
// ページが相対パスで参照する画像。site/assets から出力先の assets へ写し、公開先でも同じ位置に置く。
const SITE_ASSET_NAMES = ['app-icon-256.png'];

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

function zipFileName(version) {
  return `ScreenRecorder-${version}-win-x64.zip`;
}

function localDateString(date = new Date()) {
  const twoDigits = value => String(value).padStart(2, '0');
  return `${date.getFullYear()}-${twoDigits(date.getMonth() + 1)}-${twoDigits(date.getDate())}`;
}

function buildUpdateManifest(version, zipPath, releasedAt = localDateString()) {
  const sha256 = crypto.createHash('sha256').update(fs.readFileSync(zipPath)).digest('hex');
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

const FORBIDDEN_FFMPEG_CONFIGURATIONS = ['--enable-gpl', '--enable-nonfree'];
const UPLOADING_SUFFIX = '.uploading';
const PREVIOUS_SUFFIX = '.previous';
// basic-ftp の FileType でファイルを表す値。
const REMOTE_TYPE_FILE = 1;

function escapeHtml(text) {
  return text.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
}

// 同梱の ffmpeg の `ffmpeg -version` の出力を license.html へ載せる。
// LGPL の動的リンクで同梱する条件(GPL と nonfree を含まない shared ビルド)を満たさないものは止める。
function ffmpegBuildInfoFrom(versionText) {
  const text = String(versionText ?? '').trim();
  if (!text) throw new Error('同梱する ffmpeg の版の情報(ffmpeg -version の出力)が必要です');
  const forbidden = FORBIDDEN_FFMPEG_CONFIGURATIONS.filter((flag) => text.includes(flag));
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

function generateFiles({ version, zip, out, releasedAt, ffmpegInfo }) {
  if (!isValidVersion(version)) throw new Error(`版は X.Y.Z 形式で指定してください: ${version}`);
  const zipPath = path.resolve(ROOT, zip);
  if (!fs.existsSync(zipPath)) throw new Error(`zip が見つかりません: ${zipPath}`);
  const output = path.resolve(ROOT, out);
  fs.mkdirSync(output, { recursive: true });
  const manifest = buildUpdateManifest(version, zipPath, releasedAt);
  const values = {
    VERSION: version,
    DOWNLOAD_URL: downloadUrlOf(version),
    ZIP_NAME: zipFileName(version),
    RELEASED_AT: manifest.latest.releasedAt,
    FFMPEG_BUILD_INFO: readFfmpegBuildInfo(ffmpegInfo),
  };
  writeSite(output, values);
  fs.writeFileSync(path.join(output, 'update.json'), serializeUpdateManifest(manifest), 'utf8');
  return { output, manifest };
}

function generatePages({ version, out, releasedAt = localDateString(), ffmpegInfo }) {
  if (!isValidVersion(version)) throw new Error(`版は X.Y.Z 形式で指定してください: ${version}`);
  const output = path.resolve(ROOT, out);
  fs.mkdirSync(output, { recursive: true });
  const values = { VERSION: version, DOWNLOAD_URL: downloadUrlOf(version), ZIP_NAME: zipFileName(version), RELEASED_AT: releasedAt, FFMPEG_BUILD_INFO: readFfmpegBuildInfo(ffmpegInfo) };
  writeSite(output, values);
  return output;
}

function buildUploadItems({ version, out = DEFAULT_OUT, zip }) {
  const output = path.resolve(ROOT, out);
  const zipPath = path.resolve(ROOT, zip || path.join(out, zipFileName(version)));
  return [
    { name: zipFileName(version), localPath: zipPath, remoteDir: 'archives' },
    ...SITE_ASSET_NAMES.map(name => ({ name, localPath: path.join(output, 'assets', name), remoteDir: 'assets' })),
    { name: 'manual.html', localPath: path.join(output, 'manual.html'), remoteDir: '' },
    { name: 'license.html', localPath: path.join(output, 'license.html'), remoteDir: '' },
    { name: 'index.html', localPath: path.join(output, 'index.html'), remoteDir: '' },
    { name: 'update.json', localPath: path.join(output, 'update.json'), remoteDir: '' },
  ];
}

function verifyReleaseInputs(version, items) {
  const zip = items.find(item => item.name === zipFileName(version));
  const manifestItem = items.find(item => item.name === 'update.json');
  if (!zip || !manifestItem) throw new Error('zip または update.json がアップロード対象にありません');
  const manifest = JSON.parse(fs.readFileSync(manifestItem.localPath, 'utf8'));
  const sha256 = crypto.createHash('sha256').update(fs.readFileSync(zip.localPath)).digest('hex');
  if (manifest.schema !== 1 || manifest.latest?.version !== version || manifest.latest?.url !== downloadUrlOf(version) || manifest.latest?.sha256 !== sha256) {
    throw new Error('update.json の版、URL、SHA-256 がアップロードする zip と一致しません。先に release:generate を実行してください');
  }
  return sha256;
}

// 転送の直前に公開中の版と比べ、古い成果物で公開版を巻き戻さない。
// 同じ版は同じ成果物(SHA-256 が一致)の再試行だけを許し、公開中の zip はサーバ上で同じ大きさなら送り直さない。
function decideUploadAgainstPublished(version, localSha256, published) {
  if (!published) return { skipZip: false };
  const order = compareVersions(version, published.version);
  if (order < 0) throw new Error(`公開中の版 ${published.version} のほうが新しいため、${version} は転送しません`);
  if (order === 0 && published.sha256 !== localSha256) {
    throw new Error(`公開中の ${version} と zip の SHA-256 が一致しないため、転送しません`);
  }
  return { skipZip: order === 0 };
}

// update.json を一時名から公開名へ切り替える。上書きの改名を許さないサーバでは、公開中のものを退避名へ移してから改名し、
// 失敗したら退避名を戻す。公開名へ直接書き込むと、途中で切れたときに壊れた update.json が公開されるため行わない。
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

function isSameRemoteFile(entries, name, size) {
  return entries.some(entry => entry.name === name && entry.type === REMOTE_TYPE_FILE && entry.size === size);
}

async function fetchPublishedLatest(fetchImpl = fetch) {
  const response = await fetchImpl(`${BASE_URL}/update.json`, { signal: AbortSignal.timeout(15000), cache: 'no-store' });
  if (response.status === 404) return null;
  if (!response.ok) throw new Error(`公開中の update.json を取得できません (HTTP ${response.status})`);
  const manifest = await response.json();
  if (manifest?.schema !== 1 || !/^\d+\.\d+\.\d+$/.test(manifest?.latest?.version || '')) {
    throw new Error('公開中の update.json の形式が不正です');
  }
  return { version: manifest.latest.version, sha256: manifest.latest.sha256 };
}

async function checkPublishedVersion(version, fetchImpl = fetch) {
  const response = await fetchImpl(`${BASE_URL}/update.json`, { signal: AbortSignal.timeout(15000) });
  if (response.status === 404) return { firstRelease: true };
  if (!response.ok) throw new Error(`公開中の update.json を取得できません (HTTP ${response.status})`);
  const manifest = await response.json();
  if (manifest?.schema !== 1 || !/^\d+\.\d+\.\d+$/.test(manifest?.latest?.version || '')) {
    throw new Error('公開中の update.json の形式が不正です');
  }
  if (compareVersions(version, manifest.latest.version) <= 0) {
    throw new Error(`新しい版 ${version} は公開中の版 ${manifest.latest.version} より大きくありません`);
  }
  return { firstRelease: false, publishedVersion: manifest.latest.version };
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
  const envConfig = configFromEnvironment(env);
  const config = { ...fileConfig, ...envConfig };
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

async function uploadFiles(options) {
  const items = buildUploadItems(options);
  for (const item of items) {
    if (!fs.existsSync(item.localPath)) throw new Error(`アップロード対象がありません: ${item.localPath}`);
    item.size = fs.statSync(item.localPath).size;
    if (item.size === 0) throw new Error(`アップロード対象が空です: ${item.name}`);
  }
  const localSha256 = verifyReleaseInputs(options.version, items);
  const { skipZip } = decideUploadAgainstPublished(options.version, localSha256, await fetchPublishedLatest());
  const config = loadConfig(options.config);
  const targets = buildRemoteTargets(items, config.remoteRoot);
  console.log(`接続先: ${config.host}:${config.port}`);
  console.log('転送順:');
  for (const item of targets) console.log(`  ${item.name} (${item.size} bytes)`);
  if (options.dryRun) return;
  if (!options.yes) {
    const rl = readline.createInterface({ input: process.stdin, output: process.stdout });
    try {
      if (!/^y(es)?$/i.test((await rl.question('転送しますか? (y/N): ')).trim())) return;
    } finally {
      rl.close();
    }
  }

  const { Client } = require('basic-ftp');
  const client = new Client(30000);
  try {
    await client.access({ host: config.host, port: config.port, user: config.user, password: config.password, secure: config.secure });
    for (const item of targets) {
      await client.ensureDir(`${config.remoteRoot}/${item.remoteDir}`.replace(/\/$/, ''));
      if (skipZip && item.name === zipFileName(options.version) && isSameRemoteFile(await client.list(), item.name, item.size)) {
        console.log(`${item.name} は同じ内容で公開済みのため送りません。`);
        continue;
      }
      // update.json は利用者の更新確認が読むので、途中で切れても壊れた内容を公開しないよう一時名で送ってから改名する。
      const uploadName = item.name === 'update.json' ? `${item.name}${UPLOADING_SUFFIX}` : item.name;
      await client.uploadFrom(item.localPath, uploadName);
      const entries = await client.list();
      const remote = entries.find(entry => entry.name === uploadName);
      if (!remote || remote.size !== item.size) {
        throw new Error(`${item.name} の転送後サイズが一致しません (local=${item.size}, remote=${remote?.size ?? 'missing'})`);
      }
      if (uploadName !== item.name) {
        await replaceRemoteFile(client, uploadName, item.name, entries.some(entry => entry.name === item.name));
      }
      console.log(`転送・照合完了: ${item.name}`);
    }
  } finally {
    client.close();
  }
}

function parseArgs(argv) {
  const command = argv[0];
  if (!['generate', 'generate-pages', 'check-version', 'upload'].includes(command)) throw new Error('generate / generate-pages / check-version / upload のいずれかを指定してください');
  const options = { command, version: null, out: DEFAULT_OUT, zip: null, config: null, yes: false, dryRun: false };
  for (let i = 1; i < argv.length; i += 1) {
    const arg = argv[i];
    if (arg === '--yes') options.yes = true;
    else if (arg === '--dry-run') options.dryRun = true;
    else if (['--version', '--zip', '--out', '--config', '--ffmpeg-info'].includes(arg)) {
      if (!argv[i + 1]) throw new Error(`${arg} に値が必要です`);
      options[{ '--version': 'version', '--zip': 'zip', '--out': 'out', '--config': 'config', '--ffmpeg-info': 'ffmpegInfo' }[arg]] = argv[++i];
    } else throw new Error(`不明なオプション: ${arg}`);
  }
  if (!options.version || !isValidVersion(options.version)) throw new Error('--version は X.Y.Z 形式で指定してください');
  if (command === 'generate' && !options.zip) throw new Error('generate には --zip が必要です');
  if (command === 'generate-pages' && options.zip) throw new Error('generate-pages では --zip を指定できません');
  return options;
}

async function main() {
  try {
    const options = parseArgs(process.argv.slice(2));
    if (options.command === 'generate') {
      const { output, manifest } = generateFiles(options);
      console.log(`生成しました: ${output}`);
      console.log(`ダウンロード URL: ${manifest.latest.url}`);
    } else if (options.command === 'generate-pages') {
      console.log(`ページを生成しました: ${generatePages(options)}`);
    } else if (options.command === 'check-version') {
      const result = await checkPublishedVersion(options.version);
      console.log(result.firstRelease ? '初回公開として続行できます。' : `公開中の版 ${result.publishedVersion} より新しい版です。`);
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
  isValidVersion, compareVersions, downloadUrlOf, zipFileName, localDateString, buildUpdateManifest,
  serializeUpdateManifest, renderTemplate, generateFiles, generatePages, buildUploadItems,
  verifyReleaseInputs, checkPublishedVersion, configFromEnvironment, buildRemoteTargets,
  ffmpegBuildInfoFrom, decideUploadAgainstPublished, replaceRemoteFile, isSameRemoteFile,
};
