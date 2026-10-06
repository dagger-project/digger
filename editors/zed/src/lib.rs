//! Zed integration for Digger: tells Zed how to start the adapter and how to turn the
//! "new debug session" UI into a Digger launch/attach configuration.

use std::net::Ipv4Addr;

use zed_extension_api::{
    self as zed, serde_json, DebugAdapterBinary, DebugConfig, DebugRequest, DebugScenario,
    DebugTaskDefinition, StartDebuggingRequestArguments, StartDebuggingRequestArgumentsRequest,
    TcpArguments, Worktree,
};

const ADAPTER_NAME: &str = "Digger";
const BINARY_NAME: &str = "digger";
const DEFAULT_SERVER_PORT: u16 = 4711;

struct DigExtension;

impl DigExtension {
    /// Resolution order: `dap.Digger.binary` setting, `$DIGGER_PATH`, `digger` on PATH,
    /// then the .NET global tools directory (`dotnet tool install -g digger`, `scripts/install.sh`).
    fn find_binary(user_path: Option<String>, worktree: &Worktree) -> Result<String, String> {
        if let Some(path) = user_path {
            return Ok(path);
        }

        let env = worktree.shell_env();
        if let Some((_, path)) = env.iter().find(|(key, _)| key == "DIGGER_PATH") {
            return Ok(path.clone());
        }

        if let Some(path) = worktree.which(BINARY_NAME) {
            return Ok(path);
        }

        if let Some((_, home)) = env.iter().find(|(key, _)| key == "HOME") {
            return Ok(format!("{home}/.dotnet/tools/{BINARY_NAME}"));
        }

        Err(format!(
            "Could not find `{BINARY_NAME}`. Put it on PATH or set \"dap\": {{ \"{ADAPTER_NAME}\": {{ \"binary\": \"/path/to/digger\" }} }} in settings."
        ))
    }

    fn request_kind(config: &serde_json::Value) -> Result<StartDebuggingRequestArgumentsRequest, String> {
        match config.get("request").and_then(|value| value.as_str()) {
            Some("launch") | None => Ok(StartDebuggingRequestArgumentsRequest::Launch),
            Some("attach") => Ok(StartDebuggingRequestArgumentsRequest::Attach),
            Some(other) => Err(format!("Unknown request '{other}': expected 'launch' or 'attach'.")),
        }
    }
}

impl zed::Extension for DigExtension {
    fn new() -> Self {
        DigExtension
    }

    fn get_dap_binary(
        &mut self,
        adapter_name: String,
        config: DebugTaskDefinition,
        user_provided_debug_adapter_path: Option<String>,
        worktree: &Worktree,
    ) -> Result<DebugAdapterBinary, String> {
        if adapter_name != ADAPTER_NAME {
            return Err(format!("Unknown debug adapter '{adapter_name}'"));
        }

        let parsed: serde_json::Value = serde_json::from_str(&config.config)
            .map_err(|error| format!("Invalid debug configuration: {error}"))?;
        let request = Self::request_kind(&parsed)?;
        let request_args = StartDebuggingRequestArguments {
            configuration: config.config.clone(),
            request,
        };

        // "tcp_connection" in debug.json: connect to an already running `digger dap --server`
        // (for example inside a container) instead of starting one.
        if let Some(tcp) = config.tcp_connection {
            return Ok(DebugAdapterBinary {
                command: None,
                arguments: Vec::new(),
                envs: Vec::new(),
                cwd: None,
                connection: Some(TcpArguments {
                    port: tcp.port.unwrap_or(DEFAULT_SERVER_PORT),
                    host: tcp.host.unwrap_or_else(|| Ipv4Addr::LOCALHOST.to_bits()),
                    timeout: tcp.timeout,
                }),
                request_args,
            });
        }

        let command = Self::find_binary(user_provided_debug_adapter_path, worktree)?;

        // Optional diagnostics: "logFile": "/tmp/digger.log" in the debug configuration.
        let mut arguments = vec!["dap".to_owned()];
        if let Some(log) = parsed.get("logFile").and_then(|value| value.as_str()) {
            arguments.push(format!("--log={log}"));
        }

        Ok(DebugAdapterBinary {
            command: Some(command),
            arguments,
            envs: Vec::new(),
            cwd: Some(worktree.root_path()),
            connection: None,
            request_args,
        })
    }

    fn dap_request_kind(
        &mut self,
        adapter_name: String,
        config: serde_json::Value,
    ) -> Result<StartDebuggingRequestArgumentsRequest, String> {
        if adapter_name != ADAPTER_NAME {
            return Err(format!("Unknown debug adapter '{adapter_name}'"));
        }

        Self::request_kind(&config)
    }

    fn dap_config_to_scenario(&mut self, config: DebugConfig) -> Result<DebugScenario, String> {
        let configuration = match config.request {
            DebugRequest::Launch(launch) => {
                let env: serde_json::Map<String, serde_json::Value> = launch
                    .envs
                    .into_iter()
                    .map(|(key, value)| (key, serde_json::Value::String(value)))
                    .collect();
                serde_json::json!({
                    "request": "launch",
                    "program": launch.program,
                    "args": launch.args,
                    "cwd": launch.cwd,
                    "env": env,
                    "stopAtEntry": config.stop_on_entry.unwrap_or(false),
                })
            }
            DebugRequest::Attach(attach) => serde_json::json!({
                "request": "attach",
                "processId": attach.process_id,
            }),
        };

        Ok(DebugScenario {
            label: config.label,
            adapter: config.adapter,
            build: None,
            config: configuration.to_string(),
            tcp_connection: None,
        })
    }
}

zed::register_extension!(DigExtension);
