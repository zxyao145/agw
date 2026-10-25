// Vite 构建测试页时读取的 PostCSS 配置，与 Web 相同使用 Tailwind。The PostCSS config Vite reads for the test page;
// it uses Tailwind like Web.
const config = {
  plugins: {
    "@tailwindcss/postcss": {},
  },
};

export default config;
