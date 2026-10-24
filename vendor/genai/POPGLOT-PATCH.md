# genai 0.7.1 provenance and local patch

Upstream: https://github.com/jeremychone/rust-genai

Immutable source: https://static.crates.io/crates/genai/genai-0.7.1.crate

Archive SHA-256:
`f031d61ad96cd018f8413af948613b9ec94cc0eff1dee670461aa490e933522c`.
All **156 source files**, the upstream README and both MIT/Apache-2.0 licenses
are retained. The distribution contains only `src/`, `Cargo.toml`, `README.md`,
`LICENSE-MIT`, `LICENSE-APACHE` and this provenance record. The normalized Cargo
manifest omits unshipped example/test/bench targets, their development
dependencies and the reference to the omitted original manifest; runtime
dependencies, features and the library target are unchanged.

Only source change: `src/adapter/mod.rs` exports `prepare_payload`, a synchronous
wrapper around upstream's adapter dispatcher. It does not implement a protocol,
open a socket, resolve credentials, or change upstream request builders.
The patched file's LF-normalized SHA-256 is
`f9f2e7054d5f4036c97265b99692a2af7269c652a221fc3aafe77e448fb5c66d`.
The verifier compares every source against the immutable archive plus the exact
export insertion, as well as the minimal manifest, README and both licenses.
No protocol fork is maintained.

PopGlot uses this SDK for the text and image request bodies of OpenAI Chat,
OpenAI Responses, Anthropic Messages and Gemini, including streaming requests.
PopGlot retains endpoint policy, authentication, bounded reads, cancellation,
retry, event lifecycle checks and byte-preserving response extraction.
The SDK high-level client's full-body reads and trimming are not used.
PopGlot restores opaque gateway model IDs and removes generated reasoning and
unconfigured prompt-cache options before sending. An explicit SDK
`ReasoningEffort::Zero` suppresses suffix inference during serialization; after
those wire controls are removed, it says nothing about a provider's own reasoning
defaults. Native output-budget fields remain SDK-owned.

The 2026-10-24 source audit found exactly the export insertion above relative to
the archive. The 0.6.5 → 0.7.1 request-source review and verification limits are
recorded in `docs/architecture/ADR-0030-genai-staged-migration.md` at the repo root.
This audit did not run builds/tests or contact credentialed providers; it is not
a claim of live-provider compatibility or release approval.

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
./scripts/verify-genai-vendor.ps1 -ArchivePath <path-to-genai-0.7.1.crate>
```
