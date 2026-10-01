"""Checks the board server makes on every request: which Host a request may carry. A web page can
reach a loopback server through DNS rebinding (its own name resolving to 127.0.0.1), and then its
Origin and Host agree, so the same-origin check alone does not stop it; only the Host name does."""
from __future__ import annotations

import ipaddress


def _is_loopback_name(name: str) -> bool:
    name = name.strip("[]").lower()
    if name == "localhost":
        return True
    try:
        return ipaddress.ip_address(name).is_loopback
    except ValueError:
        return False


def bind_allowed(host: str, allow_remote: bool) -> bool:
    """The board has no authentication, so it may only listen on a loopback address (127.0.0.1, localhost, ::1)
    unless the operator opted in. An empty host or 0.0.0.0 means every interface and is not loopback."""
    return allow_remote or _is_loopback_name(host)


def host_header_ok(header: str | None, port: int, allow_remote: bool = False) -> bool:
    """True only for a loopback name (127.0.0.1, localhost, [::1]) with this server's own port.
    A missing or malformed header is refused: nothing a browser sends for this board looks like it.
    With the remote opt-in the operator chose to expose the board and no name can be listed, so any Host passes."""
    if allow_remote:
        return True
    if not header:
        return False
    host, port_text = header, ""
    if header.startswith("["):
        end = header.find("]")
        if end == -1:
            return False
        host, rest = header[:end + 1], header[end + 1:]
        if rest and not rest.startswith(":"):
            return False
        port_text = rest[1:]
    elif ":" in header:
        host, _, port_text = header.partition(":")
        if ":" in port_text:
            return False  # an unbracketed IPv6 address is not a valid Host value
    if not _is_loopback_name(host):
        return False
    return port_text == str(port) or (port_text == "" and port == 80)


SERVER_NAME = "claude-monitor"  # the Server header: the application, not the Python version behind it

# Sent with every response. The page has one inline <style> block and external scripts only, so scripts are
# 'self' (no inline script, no handler attributes) and only styles may be inline. The board is never framed.
SECURITY_HEADERS = (
    ("Content-Security-Policy", "default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; "
     "img-src 'self'; connect-src 'self'; manifest-src 'self'; worker-src 'self'; "
     "frame-ancestors 'none'; base-uri 'none'; form-action 'none'"),
    ("X-Content-Type-Options", "nosniff"),
    ("X-Frame-Options", "DENY"),
    ("Referrer-Policy", "no-referrer"),
    ("Permissions-Policy", "camera=(), microphone=(), geolocation=()"),
    ("Cross-Origin-Resource-Policy", "same-origin"),
)
