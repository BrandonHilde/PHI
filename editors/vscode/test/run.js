// Runs test/suite.js inside a real VS Code, with a profile of its own (your settings and
// extensions aren't touched), opened on this repository: node test/run.js. Build PHI first.

const { spawnSync } = require('child_process');
const fs = require('fs');
const os = require('os');
const path = require('path');

const extension = path.resolve(__dirname, '..');
const repository = path.resolve(extension, '..', '..');
const profile = fs.mkdtempSync(path.join(os.tmpdir(), 'phi-vscode-test-'));

// started from inside an editor, these would make it run as something else
const env = { ...process.env };
for (const name of Object.keys(env))
    if (name.startsWith('VSCODE_') || name === 'ELECTRON_RUN_AS_NODE') delete env[name];

const result = spawnSync(process.env.PHI_CODE ?? 'code', [
    repository,
    '--new-window',
    '--wait',
    '--disable-extensions',
    '--disable-workspace-trust',
    '--skip-welcome',
    '--skip-release-notes',
    `--user-data-dir=${path.join(profile, 'data')}`,
    `--extensions-dir=${path.join(profile, 'extensions')}`,
    `--extensionDevelopmentPath=${extension}`,
    `--extensionTestsPath=${path.join(__dirname, 'suite.js')}`,
], { env, stdio: 'inherit', shell: process.platform === 'win32', timeout: 180000 });

// (VS Code can hold on to a file or two for a moment after it's gone; it's only a temporary folder)
try {
    fs.rmSync(profile, { recursive: true, force: true, maxRetries: 5, retryDelay: 1000 });
} catch { }
process.exit(result.status ?? 1);
