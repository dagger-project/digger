# Digger for VS Code, Cursor, VSCodium and Windsurf

Registers the `digger` debug type, which runs [Digger](https://github.com/dagger-project/digger)
(`digger dap`) as the debug adapter for .NET programs.

Install `digger` first (`dotnet tool install -g Digger.Debugger`), then this extension
(`./install.sh` from this folder). The extension finds `digger` through the `digger.path`
setting, `$DIGGER_PATH`, `PATH`, or `~/.dotnet/tools/digger`.

```jsonc
// .vscode/launch.json
{
  "version": "0.2.0",
  "configurations": [
    {
      "name": "Digger: Launch",
      "type": "digger",
      "request": "launch",
      "preLaunchTask": "build",
      "program": "${workspaceFolder}/bin/Debug/net10.0/MyApp.dll",
      "args": [],
      "cwd": "${workspaceFolder}"
    },
    {
      "name": "Digger: Attach",
      "type": "digger",
      "request": "attach",
      "processId": "${command:pickProcess}"
    }
  ]
}
```

The launch options are the same as in every other editor; see the main README.
