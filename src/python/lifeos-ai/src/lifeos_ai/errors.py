"""Failures the API reports as stable codes, never as provider details (shared by capabilities)."""


class ServiceError(Exception):
    """Base of every expected failure. `code` and `status` are all a caller ever sees."""

    code = "service_error"
    status = 503


class ProviderUnavailable(ServiceError):
    """Not configured, unreachable, timed out, rate-limited or rejected credentials."""

    code = "provider_unavailable"
    status = 503
