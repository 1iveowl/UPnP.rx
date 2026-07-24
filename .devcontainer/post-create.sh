#!/usr/bin/env bash
set -euo pipefail

# Docker creates named volumes as root. Make the persistent tool state available
# to the non-root account required for Claude's bypass-permissions mode.
sudo chown -R "$(id -u):$(id -g)" "${CLAUDE_CONFIG_DIR}" "${CODEX_HOME}" "${COPILOT_HOME}"

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
