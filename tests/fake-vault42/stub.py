"""
Fake vault42 — fixed JWKS + always-mints-the-same-shape JWT.

Drop-in replacement for vault42 in test profiles where auth realism doesn't
matter. Real vault42 was 401/403'ing during high-rate matrix runs because
seed_rules.py defaulted to a stale password and vault42 throttled the retry
storm. This stub:

  * generates an RSA-2048 keypair ONCE at startup
  * serves /.well-known/jwks.json with the public key (no expiry)
  * serves POST /auth/login (any creds) → freshly-minted JWT with 7-day exp
  * serves POST /auth/token, GET /.well-known/openid-configuration, /livez

Coord refetches JWKS on first auth check, so swapping the deployment image
is enough — no coord-side config change. Token claims mirror real vault42
(iss, sub, aud, roles=[user,admin], scopes=[read,write]) so handlers that
check `roles` or `scopes` still pass.
"""
from __future__ import annotations

import base64
import json
import os
import time
import uuid
from http.server import BaseHTTPRequestHandler, HTTPServer

import jwt as pyjwt
from cryptography.hazmat.primitives import serialization
from cryptography.hazmat.primitives.asymmetric import rsa

ISSUER = os.environ.get("VAULT_ORIGIN", "http://vault42.hermod.svc.cluster.local:8080")
PORT = int(os.environ.get("PORT", "8080"))
KID = "fake-vault42-kid"

_key = rsa.generate_private_key(public_exponent=65537, key_size=2048)
_pem = _key.private_bytes(
    encoding=serialization.Encoding.PEM,
    format=serialization.PrivateFormat.PKCS8,
    encryption_algorithm=serialization.NoEncryption(),
)
_public_numbers = _key.public_key().public_numbers()


def _b64u_int(n: int) -> str:
    raw = n.to_bytes((n.bit_length() + 7) // 8, "big")
    return base64.urlsafe_b64encode(raw).rstrip(b"=").decode()


JWKS = {
    "keys": [{
        "kty": "RSA",
        "use": "sig",
        "kid": KID,
        "alg": "RS256",
        "n": _b64u_int(_public_numbers.n),
        "e": _b64u_int(_public_numbers.e),
    }]
}

OIDC = {
    "issuer": ISSUER,
    "jwks_uri": f"{ISSUER}/.well-known/jwks.json",
    "authorization_endpoint": f"{ISSUER}/auth/authorize",
    "token_endpoint": f"{ISSUER}/auth/token",
    "id_token_signing_alg_values_supported": ["RS256"],
    "response_types_supported": ["token", "id_token"],
    "subject_types_supported": ["public"],
}


def mint_token(sub: str = "fake-user-00000000-0000-0000-0000-000000000001") -> str:
    now = int(time.time())
    payload = {
        "iss": ISSUER,
        "sub": sub,
        "aud": [ISSUER],
        "exp": now + 7 * 24 * 3600,
        "nbf": now - 5,
        "iat": now,
        "jti": str(uuid.uuid4()),
        "roles": ["user", "admin"],
        "scopes": ["read", "write"],
        "fingerprint": "fake-vault42-fingerprint",
        "token_type": "Bearer",
    }
    return pyjwt.encode(payload, _pem, algorithm="RS256",
                        headers={"kid": KID, "typ": "JWT"})


class Handler(BaseHTTPRequestHandler):
    def _ok(self, body, ct="application/json"):
        b = body.encode() if isinstance(body, str) else body
        self.send_response(200)
        self.send_header("Content-Type", ct)
        self.send_header("Content-Length", str(len(b)))
        self.end_headers()
        self.wfile.write(b)

    def do_GET(self):
        if self.path in ("/.well-known/jwks.json", "/jwks.json"):
            self._ok(json.dumps(JWKS))
        elif self.path == "/.well-known/openid-configuration":
            self._ok(json.dumps(OIDC))
        elif self.path in ("/livez", "/readyz", "/healthz"):
            self._ok("ok", "text/plain")
        else:
            self.send_error(404)

    def do_POST(self):
        if self.path in ("/auth/login", "/auth/token", "/auth/refresh"):
            length = int(self.headers.get("Content-Length", "0") or 0)
            if length:
                self.rfile.read(length)  # drain body, ignore creds
            token = mint_token()
            self._ok(json.dumps({
                "access_token": token,
                "token_type": "Bearer",
                "expires_in": 7 * 24 * 3600,
            }))
        else:
            self.send_error(404)

    def log_message(self, fmt, *args):  # noqa
        pass  # quiet


if __name__ == "__main__":
    print(f"fake-vault42 listening on :{PORT} (issuer={ISSUER}, kid={KID})",
          flush=True)
    HTTPServer(("0.0.0.0", PORT), Handler).serve_forever()
