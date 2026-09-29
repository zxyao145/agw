# AGW sites / 首页与文档站

两个独立构建的 Hugo + [Oink v1.0.0](https://github.com/pgsty/oink/tree/v1.0.0) 站点，共用主题、静态资源和工具链：

- `https://agw-ai.dev/`：首页，中文入口为 `/zh/`。内容位于 `home/content/`，后续其他介绍页面也在这个目录维护。
- `https://docs.agw-ai.dev/`：文档站，根路径进入 `/docs/`，中文入口 `/zh/` 进入 `/zh/docs/`。文章继续使用 `/docs/...` 和 `/zh/docs/...` 路径。

Two independently built bilingual sites share the theme and toolchain. The home site supports future introduction pages in `home/content/`; the documentation site owns `content/` and its search indexes. English is the default language and Chinese lives under `/zh/`. Neither build depends on the application client workspace.

## 工具链 / Toolchain

- Hugo **Extended 0.165.0**
- Go **1.27.0**
- Python 3 for the optional build/link verifier

Install Hugo Extended and Go using their official distributions. Theme versions and checksums are pinned in `go.mod` and `go.sum`. `scripts/hugo.sh <home|docs>` uses system Go when available, or the ignored local installation at `.cache/go`. It selects the site's configuration and a site-local Hugo cache. Preview uses separate module and resource caches.

当前工作环境已在 `.cache/go` 放置 Go 1.27.0（macOS arm64）。它不进入 Git，新 checkout 需要安装 Go；站点源码与平台无关。首次模块解析需要网络，下载后可复用缓存。

## 本地预览 / Preview

From the repository root:

```bash
cd site
./scripts/hugo.sh home server --bind 127.0.0.1 --port 1313 \
  --baseURL http://localhost:1313/ --disableFastRender --destination .cache/preview/home
```

首页预览地址为 <http://localhost:1313/> 或 <http://localhost:1313/zh/>。在另一个终端从 `site/` 启动文档预览：

```bash
./scripts/hugo.sh docs server --bind 127.0.0.1 --port 1314 \
  --baseURL http://localhost:1314/ --disableFastRender --destination .cache/preview/docs
```

文档预览地址为 <http://localhost:1314/> 或 <http://localhost:1314/zh/>。跨站导航使用正式域名。预览产物位于 `.cache/preview/`。

Preview files are isolated from the production output. Stop the server with Ctrl+C. No application Server, model credentials, npm, or database initialization is needed.

## 构建与验证 / Build and verify

From `site/`, build both sites with their configured production URLs:

```bash
./scripts/hugo.sh home --cleanDestinationDir --gc --minify \
  --environment production --printPathWarnings --panicOnWarning
./scripts/hugo.sh docs --cleanDestinationDir --gc --minify \
  --environment production --printPathWarnings --panicOnWarning
python3 scripts/check-site.py public/home --site home
python3 scripts/check-site.py public/docs --site docs
```

The home output is `public/home/`; documentation output is `public/docs/`. Each directory is deployed separately. Canonical URLs, language alternates, and sitemaps use the corresponding domain. For a documentation mirror under a URL prefix:

```bash
./scripts/hugo.sh docs --cleanDestinationDir --gc --minify \
  --environment production --printPathWarnings --panicOnWarning \
  --destination .cache/github-pages --baseURL https://zxyao145.github.io/agw/
python3 scripts/check-site.py .cache/github-pages --site docs \
  --base-url https://zxyao145.github.io/agw/ --peer-directory public/home
```

The host must serve directory `index.html` files and use the appropriate generated 404 page. Publish production output with its configured domain.

检查脚本读取两个站点的产物，检查站内和跨站链接、资源与锚点、canonical URL、译文对应页面、Markdown 元数据配对、仓库源码引用、文档修订日期与搜索索引。首页产物会检查内容隔离，文档入口会检查跳转目标。

Build both sites before running the verifier. It resolves cross-domain links against the other site's local output and checks the target file and anchor. Use `--peer-directory` when that output is outside the default sibling directory. Browser interaction checks cover navigation, language switching, and documentation search.

## 发布 / Deployment

Cloudflare Pages 使用两个项目：`agw-home` 发布 `public/home/` 并绑定 `agw-ai.dev`；`agw-docs` 发布 `public/docs/` 并绑定 `docs.agw-ai.dev`。首次发布前，在 Cloudflare 创建对应项目，并通过每个项目的 **Custom domains** 添加域名、完成 DNS 配置。具体步骤见 [Cloudflare custom domains](https://developers.cloudflare.com/pages/configuration/custom-domains/)。

`.github/workflows/site.yml` validates both production builds on pull requests, pushes, and manual runs. A manual run on `main` deploys both directories to their respective Cloudflare Pages projects using the existing `CLOUDFLARE_API_TOKEN` and `CLOUDFLARE_ACCOUNT_ID` secrets. Pushes to `main` also publish the documentation mirror to GitHub Pages. Domain names and output directories are defined in the checked-in Hugo configuration.

## 内容维护 / Content maintenance

- `content/docs/{start,features,guides,operations,development}`: task-oriented Markdown, independent of internal repository docs.
- `page.zh.md` and `page.en.md`: adjacent translations with the same `translationKey`, `weight`, and relative route. `_index` files define section roots.
- `home/content/`: homepage metadata and future introduction pages, built only for `agw-ai.dev`.
- `home/data/home/zh.yaml` and `home/data/home/en.yaml`: translated homepage data using Oink sections. Keep the same capabilities and links in both languages.
- `hugo.yaml`: documentation domain, navigation, locales, outputs, theme module, and shared branding. `github_subdir: site` accounts for the monorepo source location.
- `home/hugo.yaml`: home domain, content/data directories, navigation, output directory, and `github_subdir: site/home`. The script loads it together with `hugo.yaml`.
- `content/_index.*.md` and `layouts/redirect.html`: language-specific documentation entry redirects.
- `assets/icons/logo.svg` and `static/favicon.svg`: AGW branding copied from the existing Web icon, without importing its build.

新增文章时创建一对 Markdown，包含 `title`、`description`、`weight`、`translationKey`、`lastmod`，并提供前提、步骤、预期结果及限制。站内链接使用 `relref`，例如：

```markdown
[Projects]({{< relref "/docs/guides/projects" >}})
```

Each documentation translation uses the same reference, which Hugo resolves in the current language. Homepage links to documentation use absolute URLs: `https://docs.agw-ai.dev/docs/...` for English and `https://docs.agw-ai.dev/zh/docs/...` for Chinese. New introduction pages use links within `home/content/` and remain part of the home build.

每篇文档（包括 `_index` 栏目页）使用 `lastmod: YYYY-MM-DD` 记录最近一次内容修订日期，标题下显示“最近更新”或“Last updated”。修改正文时更新对应语言的日期；仅重建站点或调整样式时不改日期。日期由作者明确维护，不使用构建时间、文件修改时间或 Git 检出时间；漏填会导致构建或检查失败。打印版也显示日期。

Each document, including section indexes, declares `lastmod: YYYY-MM-DD`. Update it when that language’s content changes, not when rebuilding or changing styles. The heading and print views display this explicit revision date. Dates do not depend on build time, filesystem timestamps, or Git checkout history. Missing dates fail the build or verifier.

面向使用者的文档先说明用途与场景，再给出前提、步骤和结果判断。首次出现的技术名词应简要解释，并保留界面标签便于查找。开发规则放在开发指南，部署细节放在运维指南；示例不得暗示未实现的功能。

Explain purpose before steps, define unfamiliar terms, and give readers a way to check the result. Keep UI labels recognizable, developer rules in Development, and deployment details in Operations. Examples must describe supported behavior.

Technical articles end with implementation references; feature pages may link to their detailed guides instead. Treat repository code, tests, and current rules as authoritative. Update both translations in the same change. Do not bulk-copy internal docs, reviews, ADRs, or agent instructions into the public site.

| Public section | Primary sources |
| --- | --- |
| Getting started | Root README, Setup/Auth implementations, provider and agent UI |
| User guide | Module READMEs and code for Agents.Execution, Jobs, Files, Tools, Integrations; client packages |
| Operations | Host configuration, `deploy/`, Auth/Setup, execution persistence |
| Development | `AGENTS.md`, `docs/human/` rules, Development/Architecture guides, client package scripts |

站点只介绍当前受支持的功能、行为与操作步骤，不记录已退役的机制、历史实现，也不加入针对旧方案的提醒。中英文文档遵守同一规则。

Describe currently supported features, behavior, and procedures only. Omit retired mechanisms, implementation history, and warnings about obsolete approaches in both languages.

## 主题更新与视觉检查 / Theme updates and visual QA

Keep `go.mod` and `go.sum` together. To intentionally update Oink, run `./scripts/hugo.sh docs mod get github.com/pgsty/oink@VERSION` with the chosen explicit version. Review upstream changes, notices, and checksums, then rerun both strict production builds, the subpath build, and browser QA. Do not replace the pin with `latest`, commit local module replacements, or copy the whole theme into the repository.

Desktop/mobile QA should cover both homepages and representative docs: language switching stays on the matching article; Chinese/English search returns relevant results; dark mode persists; mobile navigation and section trees work; code copies correctly; tables and Mermaid diagrams render; keyboard focus is visible; no horizontal page overflow. Check generated Markdown, print views, 404, canonical URLs, and repository actions as well.

Logo retains its original blue. The theme accent is a slightly darker `#3155d9` because the original `#436cf7` did not meet the theme's light-canvas text contrast check. Fonts, search, and diagram runtimes use Oink's local assets. No analytics, comments, or third-party assistant links are enabled.

Generated `public/`, `resources/`, `.cache/`, and `.hugo_build.lock` are ignored. Theme licensing is documented in `THIRD-PARTY-NOTICES.md` and `LICENSE-OINK`. Changes to this site do not require application builds or EF migrations.

## 界面截图

`static/images/screenshots/` 保存通过 Computer 从实际 AGW Desktop 和 Server Setup 界面截取的 PNG。`desktop-chat.png` 截取于 2026-09-25，中英文 README 共用的 `../medias/desktop-chat.png` 使用同一张图片。中英文页面共用原图，各自维护 alt 和图注；Desktop 截图不代表 Mobile 界面。表单使用未保存的展示内容，示例路径需由读者替换；工作流截图仅展示编辑器。

更新界面说明时同步复核截图，在对应操作步骤后用 Markdown 图片及 `{caption="图注"}` 插入。截图应避开 Token、密钥及私人内容，不为取图执行任务或提交示例配置。保留原始截图，使用主题的点击放大功能查看细节。技术架构和流量继续使用 Mermaid 图，避免用无关界面替代解释。
