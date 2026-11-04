// What a person sees in the editor, asked of the editor itself. Files are only changed in
// memory: nothing is saved. Results go to a file too, as a test's console isn't always shown.

const assert = require('assert');
const { execSync } = require('child_process');
const fs = require('fs');
const os = require('os');
const path = require('path');
const vscode = require('vscode');

const results = process.env.PHI_TEST_RESULTS ?? path.join(os.tmpdir(), 'phi-vscode-test-results.txt');
fs.writeFileSync(results, '');
function say(line) {
    console.log(line);
    fs.appendFileSync(results, line + '\n');
}

const sleep = ms => new Promise(done => setTimeout(done, ms));

async function until(what, check, patience = 30000) {
    const end = Date.now() + patience;
    let last;
    while (Date.now() < end) {
        last = await check();
        if (last) return last;
        await sleep(100);
    }
    assert.fail(`never: ${what}`);
}

const repository = () => vscode.workspace.workspaceFolders[0].uri.fsPath;

async function open(file) {
    const document = await vscode.workspace.openTextDocument(path.join(repository(), file));
    await vscode.window.showTextDocument(document);
    return document;
}

/** Puts text in place of the document's whole text, in memory. */
async function setText(document, text) {
    const edit = new vscode.WorkspaceEdit();
    edit.replace(document.uri, new vscode.Range(0, 0, document.lineCount, 0), text);
    assert.ok(await vscode.workspace.applyEdit(edit));
}

const errorsOf = document => vscode.languages.getDiagnostics(document.uri).filter(d => d.severity === vscode.DiagnosticSeverity.Error);

function positionOf(document, text, inside = 0) {
    const at = document.getText().indexOf(text);
    assert.ok(at >= 0, `the file has no ${text}`);
    return document.positionAt(at + inside);
}

const tests = [];
const test = (name, run) => tests.push({ name, run });

test('the file is PHI, and has no errors', async () => {
    const document = await open('samples/desktop.rootfs/paddles.phi');
    assert.strictEqual(document.languageId, 'phi');
    await sleep(1500);
    assert.deepStrictEqual(errorsOf(document).map(d => d.message), []);
});

test('errors come and go while typing, without saving', async () => {
    const document = await open('samples/desktop.rootfs/paddles.phi');
    const good = document.getText();
    await setText(document, good + '\nphi.Mistaken:Program\n{\n\tnothingCalledThis++ 1;\n}\n');
    const errors = await until('an error appears', () => {
        const found = errorsOf(document);
        return found.length > 0 ? found : null;
    });
    assert.match(errors[0].message, /nothingCalledThis/);
    assert.strictEqual(document.getText(errors[0].range).length > 0, true);
    await setText(document, good);
    await until('the error goes', () => errorsOf(document).length === 0);
});

test('a module of the standard library has no false errors', async () => {
    for (const file of ['lib/phi/host/net.phi', 'lib/phi/net/tlslink.phi', 'lib/phi/drivers/vga_text.phi']) {
        const document = await open(file);
        await sleep(1200);
        assert.deepStrictEqual(errorsOf(document).map(d => d.message), [], file);
    }
});

test('going to a definition across files', async () => {
    const document = await open('samples/desktop.rootfs/paddles.phi');
    const uses = document.getText().match(/\b(Draw|App|Sound|Gui)\.[A-Z]\w+/);
    assert.ok(uses, 'paddles calls into a library');
    const position = positionOf(document, uses[0], uses[0].indexOf('.') + 2);
    const found = await until('a definition', async () => {
        const answers = await vscode.commands.executeCommand('vscode.executeDefinitionProvider', document.uri, position);
        return answers?.length ? answers : null;
    });
    const target = found[0].uri ?? found[0].targetUri;
    assert.ok(fs.existsSync(target.fsPath), `${target.fsPath} is there to open`);
    assert.notStrictEqual(target.fsPath, document.uri.fsPath);
});

test('completing after a dot, and in half-written code', async () => {
    const document = await open('tests/struct_methods.phi');
    const good = document.getText();
    const marker = 'phi.StructMethods';
    assert.ok(good.includes(marker));
    const written = good.replace(/(phi\.StructMethods:\w+\s*\{)/, '$1\n\tCounter fresh;\n\tfresh.');
    await setText(document, written);
    const at = written.indexOf('fresh.') + 'fresh.'.length;
    const position = document.positionAt(at);
    const list = await until('completions', async () => {
        const answers = await vscode.commands.executeCommand('vscode.executeCompletionItemProvider', document.uri, position, '.');
        const labels = answers?.items.map(i => typeof i.label === 'string' ? i.label : i.label.label) ?? [];
        return labels.includes('count') ? labels : null;
    });
    assert.ok(list.includes('Add'), 'a struct has its methods too');
    assert.ok(!list.includes('while'), 'and no keywords after a dot');
    await setText(document, good);
});

test('phi can be rebuilt while the server runs, which then starts again', async () => {
    // if the server were running from where phi is built, this would fail on Windows
    execSync('dotnet build Compiler/Phi.Cli --no-incremental -v q --nologo', { cwd: repository(), stdio: 'pipe', timeout: 120000 });
    await sleep(2500);

    const document = await open('samples/desktop.rootfs/paddles.phi');
    const good = document.getText();
    await setText(document, good + '\nphi.Mistaken:Program\n{\n\tafterRebuild++ 1;\n}\n');
    const errors = await until('errors after starting again', () => {
        const found = errorsOf(document);
        return found.length > 0 ? found : null;
    });
    assert.match(errors[0].message, /afterRebuild/);
    await setText(document, good);
});

async function run() {
    const extension = vscode.extensions.all.find(e => e.packageJSON.name === 'phi-language');
    assert.ok(extension, 'the extension is loaded');

    let failed = 0;
    for (const { name, run: body } of tests) {
        try {
            await body();
            say(`ok    ${name}`);
        } catch (e) {
            failed++;
            say(`FAIL  ${name}\n      ${e.message}`);
        }
    }
    say(failed ? `${failed} failed` : 'all passed');
    if (failed) throw new Error(`${failed} failed`);
}

module.exports = { run };
