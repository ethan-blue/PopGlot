# genai 0.6.5 provenance and local patch

Upstream: https://github.com/jeremychone/rust-genai

Source: crates.io `genai-0.6.5.crate`, SHA-256
`1d12aba7e9dc2c4d54654566dc3dc8383b5cb52e0cfc5754989afe0480d933e3`.
This matches the registry checksum previously pinned in PopGlot's Cargo.lock.
MIT and Apache-2.0 licenses are retained.

Only source change: `src/adapter/mod.rs` exports `prepare_payload`, a synchronous
wrapper around upstream's adapter dispatcher. It does not implement a protocol,
open a socket, resolve credentials, or change upstream request builders.
The generated Cargo manifest omits example/test/bench targets not shipped here.
All 135 upstream source files are retained; no protocol fork is maintained.

PopGlot uses this SDK for the text and image request bodies of OpenAI Chat,
OpenAI Responses, Anthropic Messages and Gemini, including streaming requests.
PopGlot retains endpoint policy, authentication, bounded reads, cancellation,
retry, event lifecycle checks and byte-preserving response extraction.
The SDK high-level client's full-body reads and trimming are not used.

## Upgrade procedure

1. Choose and pin a reviewed stable release. Do not follow beta/latest at runtime.
2. Fetch its crates.io archive and verify its registry checksum.
3. Replace this vendor snapshot, reapply only the export wrapper (or remove it
   if upstream offers an equivalent public API), retain licenses.
4. Update the checksums in `scripts/verify-genai-vendor.ps1`, the Cargo pin/lock,
   and `GENAI_VERSION`. Inspect the upstream protocol diff.
5. Run the vendor verifier against the original archive, Rust workspace tests,
   clippy and the isolated Windows tests. `provider_http` exercises actual local
   HTTP requests, streaming, cancellation, retry, redirects and size caps.
6. Validate the supported real gateways before distributing a release. Ship
   dependency changes in a versioned app build; never download executable SDK
   code into a running app or silently retry a failed generation on another SDK.

Verification (PowerShell 7):

```powershell
./scripts/verify-genai-vendor.ps1 -ArchivePath <path-to-genai-0.6.5.crate>
```
