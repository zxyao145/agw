#!/usr/bin/env python3
"""验证站点产物和跨站链接。Check builds, cross-site links, and translations."""
import argparse
from datetime import date
from html.parser import HTMLParser
from pathlib import Path
from urllib.parse import unquote, urljoin, urlsplit
import re
import sys

class Page(HTMLParser):
    def __init__(self, file):
        super().__init__(convert_charrefs=True)
        self.ids = set()
        self.links = []
        self.alternates = {}
        self.canonicals = []
        self.refresh = None
        self.landing = False
        self.heading_depth = 0
        self.heading_title_seen = False
        self.heading_dates = []
        self.feed(file.read_text())

    def handle_starttag(self, tag, attributes):
        attrs = dict(attributes)
        if tag == 'div':
            if 'data-td-landing' in attrs:
                self.landing = True
            if 'td-page-heading' in attrs.get('class', '').split():
                self.heading_depth = 1
            elif self.heading_depth:
                self.heading_depth += 1
        if self.heading_depth and tag == 'h1':
            self.heading_title_seen = True
        if self.heading_depth and self.heading_title_seen and tag == 'time':
            self.heading_dates.append(attrs.get('datetime', ''))
        if attrs.get('id'):
            self.ids.add(attrs['id'])
        for key in ('href', 'src', 'poster'):
            if attrs.get(key):
                self.links.append(attrs[key])
        if tag == 'link' and attrs.get('hreflang'):
            self.alternates[attrs['hreflang']] = attrs.get('href', '')
        if tag == 'link' and attrs.get('rel') == 'canonical':
            self.canonicals.append(attrs.get('href', ''))
        if tag == 'meta' and attrs.get('http-equiv', '').lower() == 'refresh':
            self.refresh = attrs.get('content')

    def handle_endtag(self, tag):
        if tag == 'div' and self.heading_depth:
            self.heading_depth -= 1

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('directory', nargs='?')
parser.add_argument('--site', choices=('home', 'docs'), default='docs')
parser.add_argument('--base-url')
parser.add_argument('--peer-directory', help='Build output of the other site')
args = parser.parse_args()
site_urls = {'home': 'https://agw-ai.dev/', 'docs': 'https://docs.agw-ai.dev/'}
base_url = (args.base_url or site_urls[args.site]).rstrip('/') + '/'
root = Path(args.directory or f'public/{args.site}').resolve()
site = Path(__file__).resolve().parents[1]
repo = site.parent
content = site / 'home/content' if args.site == 'home' else site / 'content'
base = urlsplit(base_url)
peer = 'docs' if args.site == 'home' else 'home'
peer_root = Path(args.peer_directory).resolve() if args.peer_directory else root.parent / peer
peer_base = urlsplit(site_urls[peer])
errors = []
pages = {file: Page(file) for file in root.rglob('*.html')}
if not pages:
    sys.exit(f'No generated HTML found in {root}')
peer_pages = {file: Page(file) for file in peer_root.rglob('*.html')}
if not peer_pages:
    sys.exit(f'No generated {peer} HTML found in {peer_root}; build both sites before checking')
targets = {base.netloc: (base, root, pages), peer_base.netloc: (peer_base, peer_root, peer_pages)}
checked = 0
cross_site = 0
for file, page in pages.items():
    relative = file.relative_to(root).as_posix()
    page_url = urljoin(base_url, relative.removesuffix('index.html'))
    expected_canonical = urljoin(base_url, relative.removesuffix('index.html').replace('_print/', '', 1))
    if args.site == 'docs' and relative in ('index.html', 'zh/index.html'):
        expected_canonical = urljoin(page_url, 'docs/')
    canonical_urls = set(page.canonicals)
    if not canonical_urls or any(urlsplit(url).netloc != base.netloc for url in canonical_urls):
        errors.append(f'{relative}: expected canonical URLs on {base.netloc}')
    elif (page.refresh is None or relative in ('index.html', 'zh/index.html')) and canonical_urls != {expected_canonical}:
        errors.append(f'{relative}: expected canonical URL {expected_canonical}')
    for link in page.links:
        target = urlsplit(urljoin(page_url, link))
        if target.scheme not in ('http', 'https') or target.netloc not in targets:
            continue
        target_base, target_root, target_pages = targets[target.netloc]
        decoded = unquote(target.path)
        if not decoded.startswith(target_base.path):
            errors.append(f'{relative}: URL escapes base path: {link}')
            continue
        dest = target_root / decoded[len(target_base.path):]
        if dest.is_dir():
            dest /= 'index.html'
        if not dest.is_file():
            errors.append(f'{relative}: missing target: {link}')
        elif target.fragment and dest in target_pages and unquote(target.fragment) not in target_pages[dest].ids:
            errors.append(f'{relative}: missing anchor: {link}')
        checked += 1
        if target.netloc == peer_base.netloc:
            cross_site += 1

sources = list(content.rglob('*.zh.md'))
for source in sources:
    english = source.with_name(source.name.replace('.zh.md', '.en.md'))
    if not english.exists():
        errors.append(f'{source}: missing English translation')
        continue
    for document in (source, english):
        if 'docs' in document.relative_to(content).parts:
            lastmod = re.search(r'^lastmod: (.+)$', document.read_text(), re.M)
            try:
                if not lastmod or not re.fullmatch(r'\d{4}-\d{2}-\d{2}', lastmod[1]):
                    raise ValueError('missing or invalid date')
                date.fromisoformat(lastmod[1])
            except ValueError:
                errors.append(f'{document}: lastmod must be a valid YYYY-MM-DD revision date')
    for field in ('translationKey', 'weight'):
        zh = re.search(rf'^{field}: (.+)$', source.read_text(), re.M)
        en = re.search(rf'^{field}: (.+)$', english.read_text(), re.M)
        if not zh or not en or zh[1] != en[1]:
            errors.append(f'{source}: mismatched {field}')
    for text in (source.read_text(), english.read_text()):
        for path in re.findall(r'https://github.com/zxyao145/agw/(?:blob|tree)/main/([^\s)]+)', text):
            if not (repo / unquote(path)).exists():
                errors.append(f'{source}: missing repository reference: {path}')
    content_path = source.relative_to(content).as_posix().removesuffix('.zh.md')
    route = content_path.removesuffix('_index').rstrip('/')
    zh_file = root / 'zh' / route / 'index.html'
    en_file = root / route / 'index.html'
    for document, file in ((source, zh_file), (english, en_file)):
        if route == 'docs' or route.startswith('docs/'):
            lastmod = re.search(r'^lastmod: (.+)$', document.read_text(), re.M)
            if file in pages and (not lastmod or pages[file].heading_dates != [lastmod[1]]):
                errors.append(f'{file}: expected one revision date below the page heading')
    if not route and args.site == 'docs':
        index = root / 'docs' / 'index.html'
        expected = pages[index].alternates if index in pages else {}
        for prefix in ('', 'zh/'):
            entry = root / prefix / 'index.html'
            if entry not in pages:
                errors.append(f'Missing documentation entry page: {entry}')
            elif pages[entry].alternates != expected:
                errors.append(f'{entry}: entry redirect must declare the documentation index alternates')
        continue
    for file, expected in [(zh_file, en_file), (en_file, zh_file)]:
        if file not in pages:
            errors.append(f'Missing translated page: {file}')
            continue
        alternate_urls = {unquote(u).rstrip('/') for u in pages[file].alternates.values()}
        expected_url = urljoin(base_url, expected.relative_to(root).as_posix().removesuffix('index.html'))
        if expected_url.rstrip('/') not in alternate_urls:
            errors.append(f'{file}: missing counterpart alternate: {expected_url}')

english_sources = list(content.rglob('*.en.md'))
if len(english_sources) != len(sources):
    errors.append('Chinese and English source counts differ')
for lang in ('zh', 'en'):
    indexes = list(root.glob(f'offline-search-index.{lang}.*.json'))
    if args.site == 'docs' and (len(indexes) != 1 or indexes[0].stat().st_size < 100):
        errors.append(f'Missing or duplicate search index: {lang}')
    if args.site == 'home' and indexes:
        errors.append(f'Homepage site must not contain a documentation search index: {lang}')
for prefix in ('', 'zh/'):
    homepage = pages.get(root / prefix / 'index.html')
    if homepage is None:
        errors.append(f'Missing homepage: {prefix}')
        continue
    if args.site == 'home':
        if not homepage.landing:
            errors.append(f'{prefix}index.html: missing homepage content')
        if (root / prefix / 'docs').exists():
            errors.append(f'Homepage build contains documentation: {prefix}docs')
    elif homepage.refresh != f'0; url={base.path}{prefix}docs/':
        errors.append(f'{prefix}index.html: expected redirect to the documentation index')
if not cross_site:
    errors.append(f'Missing navigation to the {peer} site')
if errors:
    print('\n'.join(sorted(set(errors))))
    sys.exit(1)
print(f'PASS ({args.site}): {len(pages)} HTML pages, {checked} references including {cross_site} cross-site links, {len(sources)} translation pairs.')
