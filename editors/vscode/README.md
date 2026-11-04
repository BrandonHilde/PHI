# PHI for VS Code

Errors as you type, going to a definition (F12), completing names (Ctrl+Space, or after a dot),
and colors for `.phi` files. The work is done by `phi lsp`, PHI's language server; this
extension starts it. Other editors that speak the Language Server Protocol can run `phi lsp`
the same way.

## Install

**Not beside the `phi-lang.phi-language` extension** ([PHI-VSCODE](https://github.com/BrandonHilde/PHI-VSCODE)):
both answer for `.phi` files, so errors and completions would come twice. Disable that one first.

Build PHI first (`dotnet build PHI.sln`), then from this folder:

```
npm install
npm run package
code --install-extension phi-language-0.1.0.vsix
```

Open a folder with `.phi` files and open one. The extension uses the `phi` built in the folder
you have open (`Compiler/Phi.Cli/bin/...`), or else the one on your `PATH`; the setting
`phi.serverPath` chooses another.

## Working on PHI itself

The server runs from a copy of `phi`, so `dotnet build` still works while it runs, and it starts
again by itself when `phi` is rebuilt. **PHI: Restart Language Server** in the command palette
does the same by hand. What the server says and when it restarts is in the **PHI** output panel.

## How a file is checked

- A program is checked as it is, for PHI OS, or for the system its modules are for.
- A module isn't a program, so it's checked inside the first of these with no errors in it: a
  smallest program around it, its system's whole runtime (`lib/phi/host/`), or a program in the
  repository that `use`s it. Only the errors in the file itself are shown.
- Unsaved text counts: a program sees what's unsaved in the modules you have open.

`phi check` does exactly the same. A file that needs a class the test tools generate when they
run (`lib/phi/test/load.phi`, `samples/mmo/load_players.phi`, `tests/net_http.phi`) shows errors
for those names, because nothing but those tools can say what they are.

## Tests

`node test/run.js` runs `test/suite.js` inside a real VS Code with a profile of its own, opened
on this repository: a window appears for a few seconds. Nothing is saved. The server's own tests
are `LanguageServerTests`, in `dotnet test`.
