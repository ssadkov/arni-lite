# ADR 002: Desktop sign-in bridge

Date: 2026-11-10. Status: accepted. The backend routes are implemented locally and are not deployed.

Arni Lite does not embed an OAuth client and does not store a provider secret. The server creates a device-code challenge:

1. `POST /api/desktop/auth/start` returns `device_code`, public `request_id`, short `user_code`, `verification_uri`, `expires_in`, and `interval`.
2. The browser opens `verification_uri` on the existing web chat. The page keeps `request_id` in `sessionStorage`, so the normal Yandex, Google, or VK redirect can finish first.
3. The signed-in user sees the code and must type the code shown by Arni Lite. A click alone does not approve the request.
4. `POST /api/desktop/auth/token` with `device_code` returns `pending`, `slow_down`, `denied`, `expired`, or a new Arni JWT.

`device_code` is stored only as a SHA-256 hash. Neither `request_id` nor `user_code` is sufficient to mint a JWT. The JWT is not stored in the challenge row: a poll before expiry signs a new token, so a lost response can be retried until the challenge expires. After expiry the client must start again.

The client refuses verification links that are not HTTPS on `chat.arnion.kz` or `api.arnion.ru`, and links that carry `device_code`, `access_token`, `id_token`, or `token`. Loopback HTTP is allowed for local development. The server rejects a client-supplied redirect.

Wrong codes are limited to five attempts, then the challenge is denied. Start requests are limited per hashed client address. Logs must not include `device_code`, `user_code`, JWTs, or the verification URL.
