```plain
clients/
├── web/                            # Next.js
│   └── src/
│       └── app/
│           ├── signin/
│           │   └── page.tsx        # 引用 auth 模块下 ui-web 中 pages 里面的页面
│           └── signup/
│               └── page.tsx        # 引用 auth 模块下 ui-web 中 pages 里面的页面
│
├── desktop/                        # Electron
│      └── src/
│           ├── main/
│           ├── preload/
│           └── renderer/
│               ├── routes/
│               │   ├── signin.tsx  # 引用 auth 模块下 ui-desktop 中 pages 里面的页面
│               │   └── signup.tsx  # 引用 auth 模块下 ui-web 中 pages 里面的页面
│               ├── providers/
│               ├── composition/
│               └── platform/ 
│
├── packages/                       # 按照模块拆分
│   ├── http-client/                # 通用 HTTP 请求、协议类型和错误处理
│   │   │   ├── client.ts
│   │   │   ├── request.ts
│   │   │   ├── response.ts
│   │   │   ├── errors.ts
│   │   │   ├── interceptors.ts
│   │   │   └── index.ts
│   │   └── package.json
│   ├── api/                        # 基于 http-client 和 open api 文档生成的代码、业务 DTO
│   │
│   ├── auth/                       # auth 模块，登录状态、Token。
│   │   ├── src/                    
│   │   │   ├── schemas/            # auth 模块下的 Zod Schema、验证规则
│   │   │   ├── types/              # auth 模块下的 TypeScript 类型
│   │   │   ├── lib/                # auth 模块下的 纯工具函数，下面只有 ts，没有 tsx
│   │   │   ├── hooks/              # auth 模块下 hooks
│   │   │   ├── services/           # auth 模块下，负责流程编排和业务逻辑，与平台无关
│   │   │   ├── adapters/           # 可选，auth 模块下，定义平台差异接口、实现。比如 web、desktop、native 有使用不同的认│   证方式
│   │   │   ├── ui-web/             # auth 模块下，web 页面、组件。Web + Electron Renderer 都在这里。
│   │   │   │   ├── pages/          # auth 模块下，ui-web 下面被 clients 复用的页面。
│   │   │   │   │   ├── signin.tsx
│   │   │   │   │   └── signup.tsx
│   │   │   │   └── components/     # auth 模块下，ui-web 下面可被 pages 复用的组件
│   │   │   │       ├── signin-form.tsx
│   │   │   │       ├── signin-verify-code-form.tsx
│   │   │   │       ├── signin-emial-form.tsx
│   │   │   │       └── signin-totp-form.tsx
│   │   │   ├── ui-desktop/         # 可选，auth 模块下，desktop 专用的页面、组件。desktop 可以依赖 ui-web。
│   │   │   │   ├── pages/          # auth 模块下，ui-desktop 下面被 clients 复用的页面。
│   │   │   │   │   └── desktop-signin.tsx
│   │   │   │   └── components/     # auth 模块下，ui-desktop 下面可被 pages 复用的组件
│   │   │   │       └── desktop-signin-form.tsx
│   │   │   │
│   │   │   ├── ui-native/          # auth react native 页面、组件
│   │   │   │   ├── pages/          # auth 模块下，ui-native 下面被 clients 复用的页面。
│   │   │   │   │   ├── native-signin-page.tsx
│   │   │   │   │   └── native-signup-page.tsx
│   │   │   │   └── components/     # auth 模块下，ui-native 下面可被 pages 复用的组件
│   │   │   │       ├── signin-form.tsx
│   │   │   │       ├── signin-verify-code-form.tsx
│   │   │   │       ├── signin-emial-form.tsx
│   │   │   │       └── signin-totp-form.tsx
│   │   │   └── state/              # auth 模块下的 Zustand 状态与业务 Store
│   │   │
│   │   ├── package.json
│   │   ├── tsconfig.json
│   │   └── README.md
│   │
│   └── components/             # 与业务无关的基础组件库，比如 Button、Dialog、Select；shadcn/ui 或 HeroUI 的二次封装。
│       ├── ui-web/             # web 基础组件库
│   │   │   ├── shadcn/         # shadcn ui
│   │   │   └── heroui/         # heroui
│       ├── ui-native/          # react native 基础组件库
│       └── ui-tokens/          # 颜色、字号、间距、圆角等设计 Token，三端共享设计 Token
│
├── tools/
│   └── scripts/
│
├── package.json
├── pnpm-workspace.yaml
├── turbo.json
└── tsconfig.json

```