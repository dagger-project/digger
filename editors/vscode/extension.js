// VS Code (and Cursor, VSCodium, Windsurf, ...) integration for Digger: starts `digger dap`
// as the debug adapter for "type": "digger" configurations. Plain JavaScript, no dependencies,
// so the folder can be installed as is (see install.sh).
"use strict";

const vscode = require("vscode");
const childProcess = require("child_process");
const fs = require("fs");
const os = require("os");
const path = require("path");

function findDigger() {
  const configured = vscode.workspace.getConfiguration("digger").get("path");
  if (configured) {
    return configured;
  }

  if (process.env.DIGGER_PATH) {
    return process.env.DIGGER_PATH;
  }

  const executable = process.platform === "win32" ? "digger.exe" : "digger";
  for (const directory of (process.env.PATH || "").split(path.delimiter)) {
    const candidate = path.join(directory, executable);
    if (directory && fs.existsSync(candidate)) {
      return candidate;
    }
  }

  // `dotnet tool install -g Digger.Debugger` and scripts/install.sh put it here.
  return path.join(os.homedir(), ".dotnet", "tools", executable);
}

class DiggerAdapterFactory {
  createDebugAdapterDescriptor(session) {
    const config = session.configuration;

    // "server": "host:port" connects to an already running `digger dap --server`.
    if (config.server) {
      const separator = String(config.server).lastIndexOf(":");
      const host = separator > 0 ? config.server.slice(0, separator) : "127.0.0.1";
      const port = Number(separator >= 0 ? config.server.slice(separator + 1) : config.server);
      return new vscode.DebugAdapterServer(port, host);
    }

    const digger = findDigger();
    if (!fs.existsSync(digger)) {
      throw new Error(`Could not find digger at ${digger}. Install it with 'dotnet tool install -g Digger.Debugger' or set "digger.path".`);
    }

    const args = ["dap"];
    if (config.logFile) {
      args.push(`--log=${config.logFile}`);
    }

    return new vscode.DebugAdapterExecutable(digger, args);
  }
}

class DiggerConfigurationProvider {
  // F5 with no launch.json: debug the .NET project of the open file's folder.
  resolveDebugConfiguration(folder, config) {
    if (!config.type && !config.request && !config.name) {
      const editor = vscode.window.activeTextEditor;
      if (!editor || !["csharp", "fsharp", "vb"].includes(editor.document.languageId)) {
        return config;
      }

      vscode.window.showInformationMessage("Add a launch.json with a \"digger\" configuration (Run > Add Configuration... > Digger).");
      return undefined;
    }

    if (config.request === "attach" && !config.processId) {
      config.processId = "${command:pickProcess}";
    }

    return config;
  }
}

/** Lists processes that look like .NET programs (dotnet host or anything with libcoreclr loaded). */
function listDotnetProcesses() {
  return new Promise((resolve) => {
    if (process.platform === "win32") {
      resolve([]);
      return;
    }

    childProcess.execFile("ps", ["-axo", "pid=,args="], { maxBuffer: 16 * 1024 * 1024 }, (error, stdout) => {
      if (error) {
        resolve([]);
        return;
      }

      const items = [];
      for (const line of stdout.split("\n")) {
        const match = /^\s*(\d+)\s+(.*)$/.exec(line);
        if (!match || Number(match[1]) === process.pid) {
          continue;
        }

        const [, pid, command] = match;
        const program = command.split(" ")[0];
        const dotnet = path.basename(program) === "dotnet" || hasCoreClr(pid);
        if (dotnet && !/\bdigger\b|vstest|MSBuild\.dll|Roslyn|languageserver/i.test(command)) {
          items.push({ label: path.basename(command.split(" ").find((a) => a.endsWith(".dll")) || program), description: pid, detail: command, pid });
        }
      }

      resolve(items);
    });
  });
}

function hasCoreClr(pid) {
  if (process.platform !== "linux") {
    return false;
  }

  try {
    return fs.readFileSync(`/proc/${pid}/maps`, "utf8").includes("libcoreclr");
  } catch {
    return false;
  }
}

async function pickProcess() {
  const items = await listDotnetProcesses();
  const picked = await vscode.window.showQuickPick(items, { placeHolder: "Select the .NET process to attach to", matchOnDescription: true, matchOnDetail: true });
  return picked ? picked.pid : undefined;
}

function activate(context) {
  context.subscriptions.push(
    vscode.debug.registerDebugAdapterDescriptorFactory("digger", new DiggerAdapterFactory()),
    vscode.debug.registerDebugConfigurationProvider("digger", new DiggerConfigurationProvider()),
    vscode.commands.registerCommand("digger.pickProcess", pickProcess),
  );
}

function deactivate() {}

module.exports = { activate, deactivate };
