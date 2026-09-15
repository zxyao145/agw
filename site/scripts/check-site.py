#!/usr/bin/env python3
"""Check a Hugo build's local URLs, anchors, translations, and source references."""
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
        self.heading_depth = 0
        self.heading_title_seen = False
        self.heading_dates = []
        self.feed(file.read_text())

    def handle_starttag(self, tag, attributes):
        attrs = dict(attributes)
        if tag == 'div':
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

    def handle_endtag(self, tag):
        if tag == 'div' and self.heading_depth:
            self.heading_depth -= 1

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('directory', nargs='?', default='public')
parser.add_argument('--base-url', default='http://localhost:1313/')
args = parser.parse_args()
root = Path(args.directory).resolve()
site = Path(__file__).resolve().parents[1]
repo = site.parent
base = urlsplit(args.base_url.rstrip('/') + '/')
errors = []
pages = {file: Page(file) for file in root.rglob('*.html')}
if not pages:
    sys.exit(f'No generated HTML found in {root}')
checked = 0
for file, page in pages.items():
    relative = file.relative_to(root).as_posix()
    page_url = urljoin(args.base_url.rstrip('/') + '/', relative.removesuffix('index.html'))
    for link in page.links:
        target = urlsplit(urljoin(page_url, link))
        if target.scheme not in ('http', 'https') or target.netloc != base.netloc:
            continue
        decoded = unquote(target.path)
        if not decoded.startswith(base.path):
            errors.append(f'{relative}: URL escapes base path: {link}')
            continue
        dest = root / decoded[len(base.path):]
        if dest.is_dir():
            dest /= 'index.html'
        if not dest.is_file():
            errors.append(f'{relative}: missing target: {link}')
        elif target.fragment and dest in pages and unquote(target.fragment) not in pages[dest].ids:
            errors.append(f'{relative}: missing anchor: {link}')
        checked += 1

sources = list((site / 'content').rglob('*.zh.md'))
for source in sources:
    english = source.with_name(source.name.replace('.zh.md', '.en.md'))
    if not english.exists():
        errors.append(f'{source}: missing English translation')
        continue
    for document in (source, english):
        if 'docs' in document.relative_to(site / 'content').parts:
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
    content_path = source.relative_to(site / 'content').as_posix().removesuffix('.zh.md')
    route = content_path.removesuffix('_index').rstrip('/')
    zh_file = root / route / 'index.html'
    en_file = root / 'en' / route / 'index.html'
    for document, file in ((source, zh_file), (english, en_file)):
        if route == 'docs' or route.startswith('docs/'):
            lastmod = re.search(r'^lastmod: (.+)$', document.read_text(), re.M)
            if file in pages and (not lastmod or pages[file].heading_dates != [lastmod[1]]):
                errors.append(f'{file}: expected one revision date below the page heading')
    for file, expected in [(zh_file, en_file), (en_file, zh_file)]:
        if file not in pages:
            errors.append(f'Missing translated page: {file}')
            continue
        alternate_paths = {unquote(urlsplit(u).path).rstrip('/') for u in pages[file].alternates.values()}
        expected_path = base.path + expected.relative_to(root).as_posix().removesuffix('index.html')
        if expected_path.rstrip('/') not in alternate_paths:
            errors.append(f'{file}: missing counterpart alternate: {expected_path}')

english_sources = list((site / 'content').rglob('*.en.md'))
if len(english_sources) != len(sources):
    errors.append('Chinese and English source counts differ')
for lang in ('zh', 'en'):
    indexes = list(root.glob(f'offline-search-index.{lang}.*.json'))
    if len(indexes) != 1 or indexes[0].stat().st_size < 100:
        errors.append(f'Missing or duplicate search index: {lang}')
if errors:
    print('\n'.join(sorted(set(errors))))
    sys.exit(1)
print(f'PASS: {len(pages)} HTML pages, {checked} local references, {len(sources)} translation pairs, 2 search indexes.')
