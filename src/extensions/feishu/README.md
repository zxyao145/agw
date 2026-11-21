# Feishu extension

Build the extension package from the `src` workspace:

```bash
pnpm --filter @agw/extension-feishu build
```

The package is written to `src/extensions/feishu/dist`. The server host needs Node.js available as `node` because the Feishu SDK worker runs as a child process.

Set the public server address in the host configuration so Feishu binding cards can link back to Agw:

```json
{
  "Extensions": {
    "PublicBaseUrl": "https://agw.example.com"
  }
}
```

`Extensions:PublicBaseUrl` must be an HTTPS base URL accessible to users. HTTP loopback URLs are accepted for local development. The Feishu app credentials and webhook secrets are entered in the extension settings and encrypted by the server.
