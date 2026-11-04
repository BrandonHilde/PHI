// The VS Code side of PHI's language server: it finds phi and starts `phi lsp`. The server does
// the rest. On Windows a running program's files can't be replaced, and phi is rebuilt all the
// time while working on PHI, so the server runs from a copy, and starts again when phi changes.

const crypto = require('crypto');
const fs = require('fs');
const os = require('os');
const path = require('path');
const vscode = require('vscode');
const { LanguageClient } = require('vscode-languageclient/node');

const EXECUTABLE = process.platform === 'win32' ? 'phi.exe' : 'phi';
const BUILT = ['Debug', 'Release'].map(c => path.join('Compiler', 'Phi.Cli', 'bin', c, 'net8.0', EXECUTABLE));

// copies of phi from earlier builds: left for a day, as another window can be running one
const COPIES = path.join(os.tmpdir(), 'phi-lsp');
const OLD_COPY_MS = 24 * 60 * 60 * 1000;

let client;
let output;
let watcher;
let restartTimer;

/** The phi to run: the setting, one built in the workspace, or the one on the PATH. */
function findPhi() {
    const setting = vscode.workspace.getConfiguration('phi').get('serverPath');
    if (setting) return setting;

    for (const folder of vscode.workspace.workspaceFolders ?? []) {
        for (const built of BUILT) {
            const candidate = path.join(folder.uri.fsPath, built);
            if (fs.existsSync(candidate)) return candidate;
        }
    }
    return 'phi';
}

/** The files of a built phi, which is a folder of them. */
function programFiles(folder) {
    return fs.readdirSync(folder).filter(f => /\.(dll|exe|json)$/.test(f) || f === 'phi');
}

function removeOldCopies(keep) {
    for (const name of fs.readdirSync(COPIES)) {
        const copy = path.join(COPIES, name);
        if (copy === keep) continue;
        try {
            if (Date.now() - fs.statSync(copy).mtimeMs > OLD_COPY_MS) fs.rmSync(copy, { recursive: true, force: true });
        } catch {
            // one that's still running: there's always next time
        }
    }
}

/**
 * A copy of phi to run, so the original can be rebuilt: one folder for each build, found by what's
 * in phi's own files. A phi that isn't a built folder (one on the PATH) runs where it is.
 */
function runnableCopy(phi) {
    if (!path.isAbsolute(phi) || !fs.existsSync(path.join(path.dirname(phi), 'phi.dll'))) return phi;

    const folder = path.dirname(phi);
    const hash = crypto.createHash('sha256');
    for (const file of ['phi.dll', 'Phi.Compiler.dll']) hash.update(fs.readFileSync(path.join(folder, file)));
    const copy = path.join(COPIES, hash.digest('hex').slice(0, 16));

    if (!fs.existsSync(path.join(copy, 'done'))) {
        fs.mkdirSync(copy, { recursive: true });
        for (const file of programFiles(folder)) fs.copyFileSync(path.join(folder, file), path.join(copy, file));
        fs.writeFileSync(path.join(copy, 'done'), '');
    }
    removeOldCopies(copy);
    return path.join(copy, path.basename(phi));
}

async function start() {
    const phi = findPhi();
    output.appendLine(`starting ${phi} lsp`);
    watchForBuild(phi);
    try {
        client = new LanguageClient(
            'phi',
            'PHI',
            { command: runnableCopy(phi), args: ['lsp'] },
            {
                documentSelector: [{ scheme: 'file', language: 'phi' }],
                outputChannel: output,
                synchronize: { fileEvents: vscode.workspace.createFileSystemWatcher('**/*.phi') },
            });
        await client.start();
    } catch (e) {
        vscode.window.showErrorMessage(`PHI's language server didn't start (${phi} lsp): ${e.message}. ` +
            'Build PHI with dotnet build, or set phi.serverPath.');
        client = undefined;
    }
}

/** Start again when phi is rebuilt, since its library and its checks are inside it. */
function watchForBuild(phi) {
    watcher?.close();
    watcher = undefined;
    const dll = path.join(path.dirname(phi), 'phi.dll');
    if (!path.isAbsolute(phi) || !fs.existsSync(dll)) return;

    const builtAt = () => fs.statSync(dll).mtimeMs;
    let last = builtAt();
    watcher = fs.watch(path.dirname(dll), () => {
        // a build writes many files, so wait for it to be over
        clearTimeout(restartTimer);
        restartTimer = setTimeout(() => {
            try {
                if (builtAt() === last) return;
                last = builtAt();
                output.appendLine('phi was rebuilt');
                restart();
            } catch {
                // in the middle of a build: there'll be another event
            }
        }, 1500);
    });
}

async function stop() {
    const stopping = client;
    client = undefined;
    if (stopping) await stopping.stop().catch(() => { });
}

async function restart() {
    await stop();
    await start();
}

async function activate(context) {
    output = vscode.window.createOutputChannel('PHI');
    context.subscriptions.push(output);
    context.subscriptions.push(vscode.commands.registerCommand('phi.restartServer', restart));
    context.subscriptions.push(vscode.workspace.onDidChangeConfiguration(e => {
        if (e.affectsConfiguration('phi.serverPath')) restart();
    }));
    await start();
}

async function deactivate() {
    clearTimeout(restartTimer);
    watcher?.close();
    await stop();
}

module.exports = { activate, deactivate };
