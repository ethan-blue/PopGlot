//! Network proxy configuration, validation, redaction, and bypass resolution.

use std::fmt;
use std::net::{IpAddr, Ipv6Addr};

use serde::{Deserialize, Serialize};

use crate::{EndpointClass, classify_endpoint, url_host};

/// Proxy selection mode for outbound HTTP/HTTPS/SOCKS5 traffic.
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase")]
pub enum ProxyMode {
    /// Never use a proxy (always `.no_proxy()`).
    #[default]
    Direct,
    /// Follow OS / environment proxy configuration (with local bypass).
    System,
    /// Route non-bypassed traffic through an explicit custom proxy URL.
    Custom,
}

impl ProxyMode {
    /// Stable wire name used across FFI and settings documents.
    #[must_use]
    pub fn as_str(self) -> &'static str {
        match self {
            Self::Direct => "Direct",
            Self::System => "System",
            Self::Custom => "Custom",
        }
    }

    /// Parses a proxy mode string case-insensitively.
    ///
    /// # Errors
    ///
    /// Returns [`ProxyConfigError::InvalidMode`] for unrecognized mode names.
    pub fn parse(value: &str) -> Result<Self, ProxyConfigError> {
        match value.trim().to_ascii_lowercase().as_str() {
            "direct" => Ok(Self::Direct),
            "system" => Ok(Self::System),
            "custom" => Ok(Self::Custom),
            other => Err(ProxyConfigError::InvalidMode(other.to_owned())),
        }
    }
}

/// Errors produced when validating proxy configuration.
#[derive(Debug, Clone, PartialEq, Eq, thiserror::Error)]
pub enum ProxyConfigError {
    #[error("未知的代理模式：{0}（仅支持 Direct、System 或 Custom）。")]
    InvalidMode(String),
    #[error("已选择自定义代理，但未填写代理地址。")]
    MissingProxyUrl,
    #[error(
        "代理地址格式无效，请使用 http://、https://、socks5:// 或 socks5h:// 开头并包含有效主机与端口。"
    )]
    InvalidProxyUrl,
}

/// Outbound network proxy settings for Rust core HTTP clients.
#[derive(Clone, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[serde(default)]
pub struct NetworkProxyConfig {
    pub mode: ProxyMode,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub proxy_url: Option<String>,
    pub bypass_local: bool,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub no_proxy: Option<String>,
}

impl Default for NetworkProxyConfig {
    fn default() -> Self {
        Self {
            mode: ProxyMode::Direct,
            proxy_url: None,
            bypass_local: true,
            no_proxy: None,
        }
    }
}

impl fmt::Debug for NetworkProxyConfig {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        let redacted_url = self.proxy_url.as_deref().map(redact_proxy_url);
        f.debug_struct("NetworkProxyConfig")
            .field("mode", &self.mode)
            .field("proxy_url", &redacted_url)
            .field("bypass_local", &self.bypass_local)
            .field("no_proxy", &self.no_proxy)
            .finish()
    }
}

impl NetworkProxyConfig {
    /// Direct connection with no proxy and local bypass enabled.
    #[must_use]
    pub fn direct() -> Self {
        Self::default()
    }

    /// Follow operating system / environment proxy settings, bypassing local hosts.
    #[must_use]
    pub fn system() -> Self {
        Self {
            mode: ProxyMode::System,
            proxy_url: None,
            bypass_local: true,
            no_proxy: None,
        }
    }

    /// Use an explicit custom proxy URL (`http://`, `https://`, `socks5://`, `socks5h://`).
    #[must_use]
    pub fn custom(proxy_url: impl Into<String>) -> Self {
        Self {
            mode: ProxyMode::Custom,
            proxy_url: Some(proxy_url.into()),
            bypass_local: true,
            no_proxy: None,
        }
    }

    /// Returns a copy whose `proxy_url` has any `user:pass@` credentials masked.
    #[must_use]
    pub fn redacted(&self) -> Self {
        Self {
            mode: self.mode,
            proxy_url: self.proxy_url.as_deref().map(redact_proxy_url),
            bypass_local: self.bypass_local,
            no_proxy: self.no_proxy.clone(),
        }
    }
}

/// Masks any `userinfo@` credentials inside a proxy or HTTP URL before logging or returning it.
#[must_use]
pub fn redact_proxy_url(raw: &str) -> String {
    let trimmed = raw.trim();
    let Some((scheme, rest)) = trimmed.split_once("://") else {
        return trimmed.to_owned();
    };
    let (authority, suffix) = match rest.find(['/', '?', '#']) {
        Some(idx) => (&rest[..idx], &rest[idx..]),
        None => (rest, ""),
    };
    if let Some((_, host_part)) = authority.rsplit_once('@') {
        format!("{scheme}://[REDACTED]@{host_part}{suffix}")
    } else {
        trimmed.to_owned()
    }
}

/// Validates and normalizes a custom proxy URL.
///
/// Supported schemes: `http`, `https`, `socks5`, `socks5h`.
/// Never echoes raw `user:pass@` credentials in returned errors.
///
/// # Errors
///
/// Returns [`ProxyConfigError::MissingProxyUrl`] if empty or
/// [`ProxyConfigError::InvalidProxyUrl`] if malformed or using an unsupported scheme.
pub fn validate_proxy_url(raw: &str) -> Result<String, ProxyConfigError> {
    if raw.chars().any(|c| matches!(c, '\r' | '\n' | '\0')) {
        return Err(ProxyConfigError::InvalidProxyUrl);
    }
    let trimmed = raw.trim();
    if trimmed.is_empty() {
        return Err(ProxyConfigError::MissingProxyUrl);
    }
    if trimmed.chars().any(|c| c.is_control() || c.is_whitespace()) {
        return Err(ProxyConfigError::InvalidProxyUrl);
    }

    let Some((raw_scheme, rest)) = trimmed.split_once("://") else {
        return Err(ProxyConfigError::InvalidProxyUrl);
    };
    let scheme = raw_scheme.to_ascii_lowercase();
    if !matches!(scheme.as_str(), "http" | "https" | "socks5" | "socks5h") {
        return Err(ProxyConfigError::InvalidProxyUrl);
    }

    if rest.contains('?') || rest.contains('#') {
        return Err(ProxyConfigError::InvalidProxyUrl);
    }
    let authority = match rest.split_once('/') {
        Some((auth, "")) => auth,
        Some(_) => return Err(ProxyConfigError::InvalidProxyUrl),
        None => rest,
    };
    if authority.is_empty() {
        return Err(ProxyConfigError::InvalidProxyUrl);
    }

    let (userinfo, hostport) = match authority.rsplit_once('@') {
        Some((info, hp)) => {
            if info.is_empty() || info.contains('@') {
                return Err(ProxyConfigError::InvalidProxyUrl);
            }
            if let Some((user, pass)) = info.split_once(':')
                && (user.is_empty() || pass.is_empty())
            {
                return Err(ProxyConfigError::InvalidProxyUrl);
            }
            (Some(info), hp)
        }
        None => (None, authority),
    };

    let (host, port) = parse_host_and_port(hostport)?;
    let normalized_host = if host.contains(':') {
        format!("[{host}]")
    } else {
        host
    };

    let mut out = String::new();
    out.push_str(&scheme);
    out.push_str("://");
    if let Some(info) = userinfo {
        out.push_str(info);
        out.push('@');
    }
    out.push_str(&normalized_host);
    if let Some(port) = port {
        out.push(':');
        out.push_str(&port.to_string());
    }
    Ok(out)
}

fn parse_host_and_port(hostport: &str) -> Result<(String, Option<u16>), ProxyConfigError> {
    if hostport.is_empty() {
        return Err(ProxyConfigError::InvalidProxyUrl);
    }
    let (raw_host, raw_port, is_ipv6) = if let Some(after_open) = hostport.strip_prefix('[') {
        let Some((ipv6, rest)) = after_open.split_once(']') else {
            return Err(ProxyConfigError::InvalidProxyUrl);
        };
        if ipv6.parse::<Ipv6Addr>().is_err() {
            return Err(ProxyConfigError::InvalidProxyUrl);
        }
        let port_str = if rest.is_empty() {
            None
        } else if let Some(p) = rest.strip_prefix(':') {
            Some(p)
        } else {
            return Err(ProxyConfigError::InvalidProxyUrl);
        };
        (ipv6, port_str, true)
    } else {
        if hostport.contains('[') || hostport.contains(']') {
            return Err(ProxyConfigError::InvalidProxyUrl);
        }
        match hostport.split_once(':') {
            Some((h, p)) => (h, Some(p), false),
            None => (hostport, None, false),
        }
    };

    if raw_host.is_empty() || (!is_ipv6 && raw_host.contains(':')) {
        return Err(ProxyConfigError::InvalidProxyUrl);
    }

    let port = match raw_port {
        Some(p) => {
            if p.is_empty() {
                return Err(ProxyConfigError::InvalidProxyUrl);
            }
            let parsed = p
                .parse::<u16>()
                .map_err(|_| ProxyConfigError::InvalidProxyUrl)?;
            if parsed == 0 {
                return Err(ProxyConfigError::InvalidProxyUrl);
            }
            Some(parsed)
        }
        None => None,
    };

    Ok((raw_host.to_ascii_lowercase(), port))
}

/// Normalizes a `no_proxy` bypass list into canonical comma-separated entries.
#[must_use]
pub fn normalize_no_proxy(raw: Option<&str>) -> Option<String> {
    let raw = raw?;
    let items: Vec<&str> = raw
        .split([',', ';'])
        .map(str::trim)
        .filter(|entry| !entry.is_empty())
        .collect();
    (!items.is_empty()).then(|| items.join(","))
}

/// Validates and normalizes a [`NetworkProxyConfig`].
///
/// # Errors
///
/// Returns [`ProxyConfigError`] if `Custom` mode is selected without a valid proxy URL.
pub fn validate_proxy_config(
    config: &NetworkProxyConfig,
) -> Result<NetworkProxyConfig, ProxyConfigError> {
    let no_proxy = normalize_no_proxy(config.no_proxy.as_deref());
    match config.mode {
        ProxyMode::Direct => Ok(NetworkProxyConfig::direct()),
        ProxyMode::System => Ok(NetworkProxyConfig {
            mode: ProxyMode::System,
            proxy_url: None,
            bypass_local: config.bypass_local,
            no_proxy,
        }),
        ProxyMode::Custom => {
            let raw_url = config
                .proxy_url
                .as_deref()
                .ok_or(ProxyConfigError::MissingProxyUrl)?;
            let normalized_url = validate_proxy_url(raw_url)?;
            Ok(NetworkProxyConfig {
                mode: ProxyMode::Custom,
                proxy_url: Some(normalized_url),
                bypass_local: config.bypass_local,
                no_proxy,
            })
        }
    }
}

/// Returns `true` when `base_url`'s host matches any rule in `no_proxy`.
#[must_use]
pub fn host_matches_no_proxy(base_url: &str, no_proxy: Option<&str>) -> bool {
    let Some(list) = no_proxy else {
        return false;
    };
    let trimmed = base_url.trim();
    let candidate = if trimmed.contains("://") {
        trimmed.to_owned()
    } else {
        format!("http://{trimmed}")
    };
    let Ok(target_host) = url_host(&candidate) else {
        return false;
    };
    let target_ip = target_host.parse::<IpAddr>().ok();

    for entry in list
        .split([',', ';'])
        .map(str::trim)
        .filter(|s| !s.is_empty())
    {
        if entry == "*" {
            return true;
        }
        if let Some((net_str, prefix_str)) = entry.split_once('/') {
            if let (Some(ip), Ok(net_ip), Ok(prefix)) = (
                target_ip,
                net_str.trim_matches(['[', ']']).parse::<IpAddr>(),
                prefix_str.parse::<u8>(),
            ) && ip_in_cidr(ip, net_ip, prefix)
            {
                return true;
            }
            continue;
        }

        let pattern_host = strip_optional_port(entry).to_ascii_lowercase();
        if pattern_host.is_empty() {
            continue;
        }
        if let (Some(t_ip), Ok(p_ip)) = (target_ip, pattern_host.parse::<IpAddr>()) {
            if t_ip == p_ip {
                return true;
            }
            continue;
        }

        let domain = pattern_host.trim_start_matches('.');
        if !domain.is_empty()
            && (target_host == domain || target_host.ends_with(&format!(".{domain}")))
        {
            return true;
        }
    }

    false
}

fn strip_optional_port(entry: &str) -> &str {
    if let Some(after_open) = entry.strip_prefix('[')
        && let Some((ipv6, _)) = after_open.split_once(']')
    {
        return ipv6;
    }
    match entry.rsplit_once(':') {
        Some((host, port)) if !host.contains(':') && port.parse::<u16>().is_ok() => host,
        _ => entry,
    }
}

fn ip_in_cidr(ip: IpAddr, net: IpAddr, prefix: u8) -> bool {
    match (ip, net) {
        (IpAddr::V4(ip4), IpAddr::V4(net4)) if prefix <= 32 => {
            let mask = if prefix == 0 {
                0_u32
            } else {
                u32::MAX << (32 - u32::from(prefix))
            };
            (u32::from(ip4) & mask) == (u32::from(net4) & mask)
        }
        (IpAddr::V6(ip6), IpAddr::V6(net6)) if prefix <= 128 => {
            let mask = if prefix == 0 {
                0_u128
            } else {
                u128::MAX << (128 - u32::from(prefix))
            };
            (u128::from(ip6) & mask) == (u128::from(net6) & mask)
        }
        _ => false,
    }
}

/// Resolves the effective proxy configuration for a specific target Base URL.
///
/// Security and privacy guarantees:
/// - Loopback targets (`localhost`, `127.0.0.1`, `::1`, `*.localhost`) ALWAYS
///   resolve to [`ProxyMode::Direct`] (`.no_proxy()`), even when `bypass_local` is `false`.
/// - RFC1918 / IPv6 ULA targets resolve to [`ProxyMode::Direct`] whenever `bypass_local` is `true`.
/// - Targets matching `no_proxy` resolve to [`ProxyMode::Direct`].
///
/// # Errors
///
/// Returns [`ProxyConfigError`] if the configured `Custom` proxy URL is invalid.
pub fn effective_proxy_for(
    base_url: &str,
    config: &NetworkProxyConfig,
) -> Result<NetworkProxyConfig, ProxyConfigError> {
    let normalized = validate_proxy_config(config)?;
    if normalized.mode == ProxyMode::Direct {
        return Ok(normalized);
    }
    match classify_endpoint(base_url) {
        EndpointClass::Loopback => return Ok(NetworkProxyConfig::direct()),
        EndpointClass::PrivateNetwork if normalized.bypass_local => {
            return Ok(NetworkProxyConfig::direct());
        }
        EndpointClass::PrivateNetwork | EndpointClass::Internet => {}
    }
    if host_matches_no_proxy(base_url, normalized.no_proxy.as_deref()) {
        return Ok(NetworkProxyConfig::direct());
    }
    Ok(normalized)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::ProviderSettings;

    #[test]
    fn proxy_mode_parses_case_insensitively_and_rejects_unknown() {
        assert_eq!(ProxyMode::parse("Direct").unwrap(), ProxyMode::Direct);
        assert_eq!(ProxyMode::parse(" system ").unwrap(), ProxyMode::System);
        assert_eq!(ProxyMode::parse("CUSTOM").unwrap(), ProxyMode::Custom);
        assert!(matches!(
            ProxyMode::parse("socks"),
            Err(ProxyConfigError::InvalidMode(_))
        ));
    }

    #[test]
    fn validate_proxy_url_accepts_supported_schemes_and_normalizes() {
        assert_eq!(
            validate_proxy_url("HTTP://127.0.0.1:7890/").unwrap(),
            "http://127.0.0.1:7890"
        );
        assert_eq!(
            validate_proxy_url("https://proxy.example.com:8443").unwrap(),
            "https://proxy.example.com:8443"
        );
        assert_eq!(
            validate_proxy_url("socks5://127.0.0.1:1080").unwrap(),
            "socks5://127.0.0.1:1080"
        );
        assert_eq!(
            validate_proxy_url("socks5h://alice:secret@PROXY.local:1081").unwrap(),
            "socks5h://alice:secret@proxy.local:1081"
        );
        assert_eq!(
            validate_proxy_url("http://[::1]:7897").unwrap(),
            "http://[::1]:7897"
        );
    }

    #[test]
    fn validate_proxy_url_rejects_invalid_inputs_without_leaking_credentials() {
        assert_eq!(
            validate_proxy_url("   "),
            Err(ProxyConfigError::MissingProxyUrl)
        );
        for bad in [
            "127.0.0.1:7890",
            "ftp://127.0.0.1:21",
            "socks4://127.0.0.1:1080",
            "http://127.0.0.1:0",
            "http://127.0.0.1:70000",
            "http://127.0.0.1:7890/extra",
            "http://127.0.0.1:7890?a=1",
            "http://user:topsecret@127.0.0.1:7890/path",
            "http://:pass@127.0.0.1:7890",
            "http://user:@127.0.0.1:7890",
            "http://[::1",
            "http://127.0.0.1:7890\n",
        ] {
            let err = validate_proxy_url(bad).expect_err(&format!("must reject: {bad}"));
            assert!(
                !err.to_string().contains("topsecret"),
                "error must never echo raw credentials: {err}"
            );
        }
    }

    #[test]
    fn redact_proxy_url_and_debug_hide_credentials() {
        let cfg = NetworkProxyConfig::custom("socks5h://alice:s3cr3tPass@127.0.0.1:1080");
        let debug_str = format!("{cfg:?}");
        assert!(!debug_str.contains("s3cr3tPass"));
        assert!(!debug_str.contains("alice"));
        assert!(debug_str.contains("[REDACTED]@127.0.0.1:1080"));

        let redacted = cfg.redacted();
        assert_eq!(
            redacted.proxy_url.as_deref(),
            Some("socks5h://[REDACTED]@127.0.0.1:1080")
        );
    }

    #[test]
    fn effective_proxy_forces_direct_for_loopback_even_with_bypass_local_false() {
        let custom = NetworkProxyConfig {
            mode: ProxyMode::Custom,
            proxy_url: Some("http://127.0.0.1:7890".to_owned()),
            bypass_local: false,
            no_proxy: None,
        };
        for local in [
            "http://localhost:11434/v1",
            "http://127.0.0.1:1234/v1",
            "http://[::1]:11434/v1",
            "http://ollama.localhost:11434/v1",
        ] {
            let resolved = effective_proxy_for(local, &custom).expect("resolve loopback");
            assert_eq!(resolved.mode, ProxyMode::Direct, "for {local}");
        }

        // Public host still uses the custom proxy.
        let public = effective_proxy_for("https://api.openai.com/v1", &custom).expect("public");
        assert_eq!(public.mode, ProxyMode::Custom);
    }

    #[test]
    fn effective_proxy_respects_bypass_local_and_no_proxy_rules() {
        let mut system = NetworkProxyConfig::system();
        system.no_proxy = Some("dict.youdao.com, .bigmodel.cn, 203.0.113.0/24".to_owned());

        // RFC1918 private address bypassed by default.
        assert_eq!(
            effective_proxy_for("http://192.168.1.20:8080/v1", &system)
                .unwrap()
                .mode,
            ProxyMode::Direct
        );

        // Matched by no_proxy domain / subdomain / CIDR.
        assert_eq!(
            effective_proxy_for("https://dict.youdao.com/jsonapi", &system)
                .unwrap()
                .mode,
            ProxyMode::Direct
        );
        assert_eq!(
            effective_proxy_for("https://open.bigmodel.cn/api/paas/v4", &system)
                .unwrap()
                .mode,
            ProxyMode::Direct
        );
        assert_eq!(
            effective_proxy_for("https://203.0.113.42/v1", &system)
                .unwrap()
                .mode,
            ProxyMode::Direct
        );

        // Unmatched internet host stays System.
        assert_eq!(
            effective_proxy_for("https://api.openai.com/v1", &system)
                .unwrap()
                .mode,
            ProxyMode::System
        );

        // Disabling bypass_local lets RFC1918 use System/Custom proxy while loopback stays Direct.
        system.bypass_local = false;
        assert_eq!(
            effective_proxy_for("http://192.168.1.20:8080/v1", &system)
                .unwrap()
                .mode,
            ProxyMode::System
        );
    }

    #[test]
    fn provider_settings_legacy_json_omits_or_defaults_proxy_to_none() {
        let legacy = r#"{"network_enabled":true}"#;
        let parsed: ProviderSettings = serde_json::from_str(legacy).expect("deserialize");
        assert!(parsed.proxy.is_none());

        let serialized = serde_json::to_string(&parsed).expect("serialize");
        assert!(!serialized.contains("\"proxy\""));

        let mut with_proxy = parsed;
        with_proxy.proxy = Some(NetworkProxyConfig::custom("socks5://127.0.0.1:7897"));
        let json = serde_json::to_string(&with_proxy).expect("serialize with proxy");
        let round_trip: ProviderSettings = serde_json::from_str(&json).expect("round trip");
        assert_eq!(round_trip.proxy, with_proxy.proxy);
    }
}
