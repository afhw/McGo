function parseGithubHref(value, filename) {
  if (typeof value !== 'string') return undefined;
  try {
    const url = new URL(value);
    if (url.protocol !== 'https:' || url.host !== 'github.com'
      || url.username || url.password || url.search || url.hash) return undefined;
    const asset = url.pathname.match(/^\/[A-Za-z0-9-]+\/[A-Za-z0-9_.-]+\/releases\/download\/[^/]+\/([^/]+)$/);
    if (!asset || /[/\\]/.test(filename) || decodeURIComponent(asset[1]) !== filename) return undefined;
    return url.href;
  } catch {
    return undefined;
  }
}

export function parseReleaseMetadata(manifest, manifestUrl, { installer = false } = {}) {
  if (!manifest || typeof manifest.version !== 'string' || !manifest.version.trim()
    || !Number.isSafeInteger(manifest.size) || manifest.size <= 0
    || typeof manifest.sha256 !== 'string' || !/^[a-f0-9]{64}$/.test(manifest.sha256)
    || typeof manifest.url !== 'string' || !manifest.url.trim()
    || (installer && (manifest.installer !== true || manifest.product !== 'mcgo-winui'))) {
    throw new Error('Invalid release metadata');
  }
  const url = new URL(manifest.url, manifestUrl);
  if (!['https:', 'http:'].includes(url.protocol) || url.username || url.password || url.hash) {
    throw new Error('Invalid release download URL');
  }
  const checksumUrl = new URL(url);
  checksumUrl.pathname += '.sha256';
  const name = decodeURIComponent(url.pathname.split('/').at(-1));
  const githubHref = parseGithubHref(manifest.github_url, name);
  return {
    href: url.href,
    name,
    size: `${(manifest.size / 1024 / 1024).toFixed(1)} MiB`,
    bytes: manifest.size,
    version: manifest.version.trim(),
    sha256: manifest.sha256,
    checksumHref: checksumUrl.href,
    ...(githubHref ? { githubHref } : {}),
  };
}

export async function loadReleaseMetadata(path, options = {}, fetcher = fetch) {
  const response = await fetcher(path, { cache: 'no-store', signal: options.signal });
  if (!response.ok) throw new Error(`Release metadata HTTP ${response.status}`);
  return parseReleaseMetadata(await response.json(), response.url, options);
}
