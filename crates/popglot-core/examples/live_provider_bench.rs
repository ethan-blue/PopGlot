//! Live Provider Streaming Benchmark CLI (Strict Safety & Zero Leaks)
//!
//! # Safety Invariants
//!
//! - **Default OFFLINE**: Network requests are unconditionally blocked unless BOTH
//!   `--live` and `--i-understand-cost` flags are passed.
//! - **No CLI API Key**: `--api-key` is rejected. Secrets are only read from
//!   environment variables (`POPGLOT_BENCHMARK_API_KEY` or provider-specific vars).
//! - **Zero Secret/Body Leaks**: Output JSON and console summaries never record
//!   raw internal URLs, prompts, bodies, headers, or keys.

#![allow(clippy::too_many_lines)]

use popglot_core::benchmark::{
    BenchmarkSafetyError, BenchmarkSubset, LiveBenchmarkConfig, LiveBenchmarkSafetyFlags,
    RealProviderClientExecutor, generate_dry_run_report, run_live_benchmark,
};
use popglot_core::provider::{ProviderClient, TransportLimits};
use popglot_domain::ProviderSettings;
use std::path::Path;
use std::process::ExitCode;

fn print_usage() {
    eprintln!(
        r"PopGlot Live Provider Benchmark CLI (Safety First)

USAGE:
    cargo run --example live_provider_bench [OPTIONS]

SAFETY CONTROLS:
    --live                  Enable live external network requests (requires --i-understand-cost)
    --i-understand-cost     Explicitly acknowledge LLM provider costs (requires --live)
    NOTE: When either safety flag is missing, the tool executes in Dry-Run mode and exits with code 2.

OPTIONS:
    --settings <JSON_OR_PATH>   Explicit provider settings (JSON literal or path to JSON file)
    --model <MODEL_NAME>        Override text model in settings
    --subset <SUBSET>           Fixture subset: minimal, code-mixed, all (default: minimal)
    --max-chars <NUM>           Maximum total input character limit (default: 2000)
    --text <CUSTOM_TEXT>        Benchmark custom single text input (bounded by max-chars)
    --json                      Output pure JSON report instead of text summary
    -h, --help                  Show this help message

AUTHENTICATION:
    API keys CANNOT be passed via CLI arguments. Configure one of:
      - POPGLOT_BENCHMARK_API_KEY (universal benchmark key)
      - OPENAI_API_KEY / ANTHROPIC_API_KEY / GEMINI_API_KEY (provider-specific)
"
    );
}

fn parse_settings_arg(arg: &str) -> Result<ProviderSettings, String> {
    let trimmed = arg.trim();
    if trimmed.is_empty() {
        return Err("Settings argument cannot be empty".to_owned());
    }

    // Check if it's a file path
    let path = Path::new(trimmed);
    if path.exists() && path.is_file() {
        let content = std::fs::read_to_string(path)
            .map_err(|e| format!("Failed to read settings file {trimmed:?}: {e}"))?;
        return serde_json::from_str::<ProviderSettings>(&content)
            .map_err(|e| format!("Failed to parse JSON from file {trimmed:?}: {e}"));
    }

    // Parse as JSON string directly
    serde_json::from_str::<ProviderSettings>(trimmed)
        .map_err(|e| format!("Failed to parse settings JSON literal: {e}"))
}

#[tokio::main]
async fn main() -> ExitCode {
    let args: Vec<String> = std::env::args().collect();

    let mut safety_flags = LiveBenchmarkSafetyFlags::default();
    let mut explicit_settings: Option<ProviderSettings> = None;
    let mut model_override: Option<String> = None;
    let mut subset = BenchmarkSubset::Minimal;
    let mut max_chars = popglot_core::benchmark::DEFAULT_MAX_BENCHMARK_INPUT_CHARS;
    let mut custom_text: Option<String> = None;
    let mut json_only = false;

    let mut i = 1;
    while i < args.len() {
        let arg = &args[i];
        if arg == "-h" || arg == "--help" {
            print_usage();
            return ExitCode::SUCCESS;
        }

        if arg == "--api-key" || arg.starts_with("--api-key=") || arg == "-k" {
            eprintln!(
                "Error: Passing API key via CLI argument is strictly forbidden for security.\n\
                 Please set POPGLOT_BENCHMARK_API_KEY or provider-specific environment variables."
            );
            return ExitCode::from(1);
        }

        match arg.as_str() {
            "--live" => {
                safety_flags.live = true;
            }
            "--i-understand-cost" => {
                safety_flags.i_understand_cost = true;
            }
            "--json" => {
                json_only = true;
            }
            "--settings" => {
                i += 1;
                if i >= args.len() {
                    eprintln!("Error: Missing value for --settings");
                    return ExitCode::from(1);
                }
                match parse_settings_arg(&args[i]) {
                    Ok(s) => explicit_settings = Some(s),
                    Err(err) => {
                        eprintln!("Error: {err}");
                        return ExitCode::from(1);
                    }
                }
            }
            "--model" => {
                i += 1;
                if i >= args.len() {
                    eprintln!("Error: Missing value for --model");
                    return ExitCode::from(1);
                }
                model_override = Some(args[i].clone());
            }
            "--subset" => {
                i += 1;
                if i >= args.len() {
                    eprintln!("Error: Missing value for --subset");
                    return ExitCode::from(1);
                }
                if let Some(s) = BenchmarkSubset::parse(&args[i]) {
                    subset = s;
                } else {
                    eprintln!(
                        "Error: Unknown subset '{}'. Available: minimal, code-mixed, all",
                        args[i]
                    );
                    return ExitCode::from(1);
                }
            }
            "--max-chars" => {
                i += 1;
                if i >= args.len() {
                    eprintln!("Error: Missing value for --max-chars");
                    return ExitCode::from(1);
                }
                match args[i].parse::<usize>() {
                    Ok(val) => max_chars = val,
                    Err(e) => {
                        eprintln!("Error: Invalid --max-chars: {e}");
                        return ExitCode::from(1);
                    }
                }
            }
            "--text" => {
                i += 1;
                if i >= args.len() {
                    eprintln!("Error: Missing value for --text");
                    return ExitCode::from(1);
                }
                custom_text = Some(args[i].clone());
            }
            unknown => {
                eprintln!("Error: Unknown argument '{unknown}'");
                print_usage();
                return ExitCode::from(1);
            }
        }
        i += 1;
    }

    let settings = explicit_settings.unwrap_or_default();

    let config = LiveBenchmarkConfig {
        settings: settings.clone(),
        model_override,
        safety_flags,
        subset,
        max_input_chars: max_chars,
        custom_text,
        ..LiveBenchmarkConfig::default()
    };

    // If dual safety flags are not satisfied, output dry run summary and exit with code 2
    if !safety_flags.is_live_permitted() {
        let dry_report = generate_dry_run_report(&config);
        if json_only {
            if let Ok(json_str) = serde_json::to_string_pretty(&dry_report) {
                println!("{json_str}");
            }
        } else {
            eprintln!("[PopGlot Safety Guard] Dry-Run Mode Active.");
            eprintln!(
                "To enable live network calls, you MUST provide BOTH `--live` and `--i-understand-cost`.\n"
            );
            println!("{}", dry_report.summary_text());
        }
        return ExitCode::from(2);
    }

    // Safety checks on settings
    if settings.safe_dev_mode {
        eprintln!("Error: ProviderSettings.safe_dev_mode is enabled. Network calls are blocked.");
        return ExitCode::from(2);
    }
    if !settings.network_enabled {
        eprintln!("Error: ProviderSettings.network_enabled is false. Network calls are blocked.");
        return ExitCode::from(2);
    }

    // Construct live executor
    let client = match ProviderClient::new(TransportLimits {
        accept_invalid_certs: settings.allow_insecure_tls,
        ..TransportLimits::default()
    }) {
        Ok(c) => c,
        Err(e) => {
            eprintln!("Error creating provider client: {e}");
            return ExitCode::from(1);
        }
    };

    let executor = RealProviderClientExecutor::new(client);

    match run_live_benchmark(&config, &executor).await {
        Ok(report) => {
            if json_only {
                match serde_json::to_string_pretty(&report) {
                    Ok(json_str) => println!("{json_str}"),
                    Err(e) => {
                        eprintln!("Error serializing report: {e}");
                        return ExitCode::from(1);
                    }
                }
            } else {
                println!("{}", report.summary_text());
            }
            ExitCode::SUCCESS
        }
        Err(BenchmarkSafetyError::MissingSafetyFlags { .. }) => {
            let dry_report = generate_dry_run_report(&config);
            if json_only {
                let _ = serde_json::to_string_pretty(&dry_report).map(|s| println!("{s}"));
            } else {
                println!("{}", dry_report.summary_text());
            }
            ExitCode::from(2)
        }
        Err(
            BenchmarkSafetyError::SafeDevModeBlocked | BenchmarkSafetyError::NetworkDisabledBlocked,
        ) => {
            eprintln!("Safety error: Settings prevent network access.");
            ExitCode::from(2)
        }
        Err(err) => {
            eprintln!("Benchmark execution failed: {err}");
            ExitCode::from(1)
        }
    }
}
