function escapeXml(value: string): string {
  return value
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;")
    .replaceAll("'", "&apos;");
}

export function buildLaunchAgentPlist(serverPath: string): string {
  const executable = escapeXml(serverPath);
  return `<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>Label</key>
  <string>com.agw.server</string>
  <key>ProgramArguments</key>
  <array>
    <string>${executable}</string>
    <string>serve</string>
  </array>
  <key>RunAtLoad</key>
  <true/>
  <key>KeepAlive</key>
  <true/>
</dict>
</plist>
`;
}

export function buildSystemdUserUnit(serverPath: string): string {
  const escapedPath = serverPath.replaceAll('"', '\\"');
  return `[Unit]
Description=Agw Server
After=network.target

[Service]
ExecStart="${escapedPath}" serve
Restart=on-failure
RestartSec=3

[Install]
WantedBy=default.target
`;
}

export function buildWindowsTaskArguments(serverPath: string): string[] {
  return [
    "/Create",
    "/F",
    "/SC",
    "ONLOGON",
    "/TN",
    "Agw Server",
    "/TR",
    `"${serverPath}" serve`,
    "/RL",
    "LIMITED",
  ];
}
