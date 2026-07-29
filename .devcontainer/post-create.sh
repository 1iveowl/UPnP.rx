#!/usr/bin/env bash
set -euo pipefail

# Docker creates named volumes as root. Make the persistent tool state available
# to the non-root account required for Claude's bypass-permissions mode.
sudo chown -R "$(id -u):$(id -g)" "${CLAUDE_CONFIG_DIR}" "${CODEX_HOME}" "${COPILOT_HOME}"

# Native AOT toolchain. The library declares IsAotCompatible and the release verifies
# that by publishing a sample natively and RUNNING it, rather than by trusting the
# annotation - so the container needs to be able to link a native binary.
#
# Two notes, both learned the hard way:
#   - gcc (already present) is sufficient today; ILC falls back to it. clang is what
#     Microsoft documents as the prerequisite and what ILC prefers, so it is installed
#     for parity rather than because anything is currently broken.
#   - Publish for the HOST architecture. Asking for -r linux-x64 on an arm64 container
#     is a cross-compile and fails at the link step with flags the native gcc rejects -
#     which looks exactly like a missing-toolchain problem and is not one.
#     Use: dotnet publish <sample> -c Release -r linux-$(dpkg --print-architecture | sed 's/amd64/x64/') ...
if ! command -v clang >/dev/null 2>&1; then
	sudo apt-get update -qq
	sudo apt-get install -y --no-install-recommends clang zlib1g-dev
fi

if [[ -f UPnP.Rx.slnx ]]; then
	dotnet restore UPnP.Rx.slnx
else
	echo "UPnP.Rx.slnx has not been created yet; skipping restore."
fi

npm install -g --allow-scripts=@anthropic-ai/claude-code @anthropic-ai/claude-code @openai/codex

claude_settings="${CLAUDE_CONFIG_DIR}/settings.json"
mkdir -p "${CLAUDE_CONFIG_DIR}" "${CODEX_HOME}"

node -e '
const fs = require("fs");
const path = process.argv[1];
let settings = {};
try { settings = JSON.parse(fs.readFileSync(path, "utf8")); } catch (error) {
  if (error.code !== "ENOENT") throw error;
}
settings.defaultMode = "bypassPermissions";
fs.writeFileSync(path, `${JSON.stringify(settings, null, 2)}\n`);
' "${claude_settings}"

codex_config="${CODEX_HOME}/config.toml"
touch "${codex_config}"
sed -i -E '/^(approval_policy|sandbox_mode)[[:space:]]*=/d' "${codex_config}"
printf '\napproval_policy = "never"\nsandbox_mode = "danger-full-access"\n' >> "${codex_config}"
