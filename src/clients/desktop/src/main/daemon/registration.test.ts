import assert from "node:assert/strict";
import test from "node:test";

import {
  buildLaunchAgentPlist,
  buildSystemdUserUnit,
  buildWindowsTaskArguments,
} from "./registration";

test("macOS LaunchAgent runs the bundled server for the current user", () => {
  const plist = buildLaunchAgentPlist(
    "/Applications/Agw Desktop.app/Contents/Resources/server/agw-server",
  );

  assert.match(plist, /com\.agw\.server/);
  assert.match(plist, /<string>serve<\/string>/);
  assert.match(plist, /<key>RunAtLoad<\/key>\s*<true\/>/);
});

test("Linux systemd unit restarts the bundled server", () => {
  const unit = buildSystemdUserUnit("/opt/Agw Desktop/resources/server/agw-server");

  assert.match(unit, /ExecStart="\/opt\/Agw Desktop\/resources\/server\/agw-server" serve/);
  assert.match(unit, /Restart=on-failure/);
  assert.match(unit, /WantedBy=default\.target/);
});

test("Windows task uses the current-user logon trigger", () => {
  const args = buildWindowsTaskArguments(
    "C:\\Users\\Ben\\AppData\\Local\\Agw Desktop\\server\\agw-server.exe",
  );

  assert.deepEqual(args.slice(0, 4), ["/Create", "/F", "/SC", "ONLOGON"]);
  assert.ok(args.includes("Agw Server"));
  assert.ok(args.some((value) => value.includes('agw-server.exe" serve')));
});
