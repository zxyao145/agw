# AGW website / 首页与文档站

独立的 Hugo + [Oink v1.0.0](https://github.com/pgsty/oink/tree/v1.0.0) 站点。中文首页在 `/`、文档在 `/docs/`；英文对应 `/en/` 和 `/en/docs/`。内容依据当前代码整理，中英文各 35 篇正文，覆盖快速开始、AGW 特点、产品使用、部署运维和开发指南。

An independent bilingual Hugo site. Chinese is the default language; English lives under `/en/`. Each language has 35 articles covering getting started, AGW features, product usage, operations, and development. The site has no dependency on the application client workspace. GitHub Actions builds, validates, and deploys it to GitHub Pages.

## 工具链 / Toolchain

- Hugo **Extended 0.165.0**
- Go **1.27.0**
- Python 3 for the optional build/link verifier

Install Hugo Extended and Go using their official distributions. Theme versions and checksums are pinned in `go.mod` and `go.sum`. `scripts/hugo.sh` uses system Go when available, or the ignored local installation at `.cache/go`. It sets a site-local Hugo cache and does not download or install tools automatically. Preview uses separate module and resource caches so production garbage collection cannot invalidate a running preview.

当前工作环境已在 `.cache/go` 放置 Go 1.27.0（macOS arm64）。它不进入 Git，新 checkout 需要安装 Go；站点源码与平台无关。首次模块解析需要网络，下载后可复用缓存。

## 本地预览 / Preview

From the repository root:

```bash
cd site
./scripts/hugo.sh server --bind 127.0.0.1 --port 1313 \
  --disableFastRender --destination .cache/preview
```

打开 <http://localhost:1313/> 或 <http://localhost:1313/en/>。预览产物放到缓存目录，避免覆盖生产 `public/`。已配置好系统 Go 时，也可直接运行 `hugo server --destination .cache/preview`。

Preview files are isolated from the production output. Stop the server with Ctrl+C. No application Server, model credentials, npm, or database initialization is needed.

## 构建与验证 / Build and verify

From `site/`, build with the local default URL:

```bash
./scripts/hugo.sh --cleanDestinationDir --gc --minify \
  --environment production --printPathWarnings --panicOnWarning
python3 scripts/check-site.py public
```

The static output is `public/`. For a real deployment, supply the actual canonical URL; the repository deliberately defaults to `http://localhost:1313/`, not an invented public domain:

```bash
./scripts/hugo.sh --cleanDestinationDir --gc --minify \
  --environment production --printPathWarnings --panicOnWarning \
  --baseURL "${AGW_SITE_URL:?Set the canonical URL, including any subpath}"
python3 scripts/check-site.py public --base-url "$AGW_SITE_URL"
```

This is a build only, not publication. The host must serve directory `index.html` files and use the appropriate generated 404 page. Never deploy a preview build containing live-reload scripts.

检查脚本只读产物，检查 HTML 本地链接、资源和锚点、译文对应页面、Markdown 元数据配对、仓库源码引用、标题下的修订日期以及两个搜索索引。它不会请求真实模型或外部网站，也不能代替浏览器交互检查。

The verifier checks local links/assets/anchors, translation alternates, paired metadata, repository references, revision dates below headings, and both search indexes. It does not request external websites or replace browser QA. For subpath verification, build into `.cache/subpath` with a base URL containing a prefix, then pass that same URL to the verifier.

## GitHub Pages 自动部署 / Automated deployment

流水线位于 [`.github/workflows/site.yml`](../.github/workflows/site.yml)，名称为 **Site Build and Deploy**。

首次启用：

1. 在仓库 **Settings → Pages → Build and deployment** 中，将 **Source** 设为 **GitHub Actions**。
2. 将站点源码和工作流合并到 `main`。涉及 `site/**` 或该工作流的推送会自动构建和发布；也可在 **Actions → Site Build and Deploy → Run workflow** 中选择 `main` 手动运行。
3. 等待 `build` 和 `deploy` 成功，通过部署任务中的链接访问站点。若 `github-pages` 环境配置了审批规则，需按该规则批准部署。

PR 只构建检查，不读取 Pages 配置、不上传部署产物，也不会发布；手动运行其他分支同样只检查。构建固定使用 Hugo Extended 0.165.0，并校验下载文件的 SHA-256；Go 版本从 `site/go.mod` 读取。Hugo 严格构建及链接、翻译、更新时间检查全部通过后，才上传 `site/public/`，发布任务直接使用这份已验证产物。

流水线将 `baseURL` 固定为 `https://zxyao145.github.io/agw/`，PR 和正式构建使用相同的项目路径，确保链接、资源和搜索索引包含 `/agw/` 前缀。GitHub Pages 保持默认项目站点配置，不设置自定义域名。本地预览仍使用 `hugo.yaml` 中的本机地址，无需修改。

无需配置 PAT 或服务器密钥，发布使用 GitHub 自动提供的 Token。构建只有读取权限；Pages 写入和 OIDC 权限仅授予发布任务。`public/` 不提交到 Git。若读取 Pages 配置时出现 404，先确认上述 Pages Source 设置及仓库的 Pages 可用性。

Enable **Settings → Pages → Source: GitHub Actions**, then merge the site and workflow into `main`. Changes to `site/**` or the workflow trigger deployment; **Run workflow** on `main` can deploy manually. Follow any approval rules configured on the `github-pages` environment.

Pull requests and manual runs on other branches only validate. They do not read Pages configuration, upload a deployment artifact, or deploy. All workflow builds use the fixed canonical URL `https://zxyao145.github.io/agw/`, including its `/agw/` project prefix. Leave the Pages custom domain unset. Strict Hugo and site checks must pass before the verified artifact is uploaded and deployed. Local preview keeps the localhost URL in `hugo.yaml`.

No personal access token or server credentials are needed. Only the deployment job receives Pages write and OIDC permissions. Keep generated `public/` files out of Git. See the [Hugo Pages guide](https://gohugo.io/host-and-deploy/host-on-github-pages/) for GitHub's initial setup.

## 内容维护 / Content maintenance

- `content/docs/{start,features,guides,operations,development}`: task-oriented Markdown, independent of internal repository docs.
- `page.zh.md` and `page.en.md`: adjacent translations with the same `translationKey`, `weight`, and relative route. `_index` files define section roots.
- `data/home/zh.yaml` and `data/home/en.yaml`: translated homepage data using Oink sections. Keep the same capabilities and links in both languages.
- `hugo.yaml`: navigation, locales, outputs, theme module, and repository actions. `github_subdir: site` accounts for the monorepo source location.
- `assets/icons/logo.svg` and `static/favicon.svg`: AGW branding copied from the existing Web icon, without importing its build.

新增文章时创建一对 Markdown，包含 `title`、`description`、`weight`、`translationKey`、`lastmod`，并提供前提、步骤、预期结果及限制。站内链接使用 `relref`，例如：

```markdown
[Projects]({{< relref "/docs/guides/projects" >}})
```

Each translation uses the same reference, which Hugo resolves in the current language. Homepage data links use language-neutral relative routes such as `docs/guides/projects/`, allowing Oink to add the language and deployment prefix. Avoid hard-coded root URLs in new content.

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
| Development | `docs/rules.md`, Development/Architecture/Module Organization guides, client package scripts |

站点只介绍当前受支持的功能、行为与操作步骤，不记录已退役的机制、历史实现，也不加入针对旧方案的提醒。中英文文档遵守同一规则。

Describe currently supported features, behavior, and procedures only. Omit retired mechanisms, implementation history, and warnings about obsolete approaches in both languages.

## 主题更新与视觉检查 / Theme updates and visual QA

Keep `go.mod` and `go.sum` together. To intentionally update Oink, run `./scripts/hugo.sh mod get github.com/pgsty/oink@VERSION` with the chosen explicit version. Review upstream changes, notices, and checksums, then rerun strict root and subpath builds and browser QA. Do not replace the pin with `latest`, commit local module replacements, or copy the whole theme into the repository.

Desktop/mobile QA should cover both homepages and representative docs: language switching stays on the matching article; Chinese/English search returns relevant results; dark mode persists; mobile navigation and section trees work; code copies correctly; tables and Mermaid diagrams render; keyboard focus is visible; no horizontal page overflow. Check generated Markdown, print views, 404, canonical URLs, and repository actions as well.

Logo retains its original blue. The theme accent is a slightly darker `#3155d9` because the original `#436cf7` did not meet the theme's light-canvas text contrast check. Fonts, search, and diagram runtimes use Oink's local assets. No analytics, comments, or third-party assistant links are enabled.

Generated `public/`, `resources/`, `.cache/`, and `.hugo_build.lock` are ignored. Theme licensing is documented in `THIRD-PARTY-NOTICES.md` and `LICENSE-OINK`. Changes to this site do not require application builds or EF migrations.

## 界面截图

`static/images/screenshots/` 保存通过 Computer 从实际 AGW Desktop 和 Server Setup 界面截取的 PNG（2026-09-14）。中英文页面共用原图，各自维护 alt 和图注；Desktop 截图不代表 Mobile 界面。表单使用未保存的展示内容，示例路径需由读者替换；工作流截图仅展示编辑器。

更新界面说明时同步复核截图，在对应操作步骤后用 Markdown 图片及 `{caption="图注"}` 插入。截图应避开 Token、密钥及私人内容，不为取图执行任务或提交示例配置。保留原始截图，使用主题的点击放大功能查看细节。技术架构和流量继续使用 Mermaid 图，避免用无关界面替代解释。
