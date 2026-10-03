"""Service-to-service authentication (PROD-AI-001).

Every route except the public liveness probe requires `Authorization: Bearer <service key>`,
the secret shared only with the LifeOS API. Missing header, wrong scheme and wrong key all get
the same 401; the key is compared in constant time (as fixed-length digests, so neither content
nor length is leaked) and never logged or echoed.
"""

import hashlib
import hmac

from starlette.requests import Request
from starlette.responses import JSONResponse

PUBLIC_PATHS = frozenset({"/health/live"})


def _digest(value: str) -> bytes:
    return hashlib.sha256(value.encode("utf-8")).digest()


class ServiceKeyGuard:
    def __init__(self, service_key: str):
        self._expected = _digest(service_key)

    def authorized(self, authorization: str | None) -> bool:
        scheme, _, token = (authorization or "").partition(" ")
        # Always compare, so a wrong scheme costs the same as a wrong key.
        matches = hmac.compare_digest(_digest(token.strip()), self._expected)
        return scheme.lower() == "bearer" and matches

    async def __call__(self, request: Request, call_next):
        if request.url.path in PUBLIC_PATHS or self.authorized(
            request.headers.get("authorization")
        ):
            return await call_next(request)
        return JSONResponse(
            status_code=401,
            content={"error": {"code": "unauthorized"}},
            headers={"WWW-Authenticate": "Bearer"},
        )
