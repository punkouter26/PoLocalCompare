# Crawl summary

Pairs crawled: **36** of the planned 42.
Errors: **13**.
Serious/critical axe violations: **11**.
Pages where the expected text was not found: **18**.

## By route

| Route | Pairs | Axe bad | Errors | Missing expectation |
|---|---|---|---|---|
| `/` | 6 | 5 | 0 | 0 |
| `/leaderboard` | 5 | 5 | 0 | 0 |
| `/demo` | 5 | 1 | 3 | 3 |
| `/archive` | 5 | 0 | 0 | 0 |
| `/arena/none` | 5 | 0 | 5 | 5 |
| `/auth/login/fake?returnUrl=/` | 5 | 0 | 0 | 5 |
| `/not-a-route` | 5 | 0 | 5 | 5 |

## By viewport

| Viewport | Pairs | Axe bad | Errors |
|---|---|---|---|
| mobile | 21 | 6 | 9 |
| desktop | 15 | 5 | 4 |

## By theme

| Theme | Pairs | Axe bad | Errors |
|---|---|---|---|
| light | 14 | 5 | 5 |
| dark | 14 | 3 | 5 |
| system | 8 | 3 | 3 |

## Axe violation IDs seen

| Rule | Node count |
|---|---|
| `color-contrast` | 41 |

## Errors

- `mobile-light-demo`: page.waitForFunction: Timeout 20000ms exceeded.
- `mobile-light-arena-404`: page.waitForFunction: Timeout 20000ms exceeded.
- `mobile-light-not-found`: page.waitForFunction: Timeout 20000ms exceeded.
- `mobile-dark-demo`: page.waitForFunction: Timeout 20000ms exceeded.
- `mobile-dark-arena-404`: page.waitForFunction: Timeout 20000ms exceeded.
- `mobile-dark-not-found`: page.waitForFunction: Timeout 20000ms exceeded.
- `mobile-system-demo`: page.waitForFunction: Timeout 20000ms exceeded.
- `mobile-system-arena-404`: page.waitForFunction: Timeout 20000ms exceeded.
- `mobile-system-not-found`: page.waitForFunction: Timeout 20000ms exceeded.
- `desktop-light-arena-404`: page.waitForFunction: Timeout 20000ms exceeded.
- `desktop-light-not-found`: page.waitForFunction: Timeout 20000ms exceeded.
- `desktop-dark-arena-404`: page.waitForFunction: Timeout 20000ms exceeded.
- `desktop-dark-not-found`: page.waitForFunction: Timeout 20000ms exceeded.

## Pages where the expected marker was not found

- `mobile-light-demo`: expected "undefined"
- `mobile-light-arena-404`: expected "undefined"
- `mobile-light-login-already`: expected "undefined"
- `mobile-light-not-found`: expected "undefined"
- `mobile-dark-demo`: expected "undefined"
- `mobile-dark-arena-404`: expected "undefined"
- `mobile-dark-login-already`: expected "undefined"
- `mobile-dark-not-found`: expected "undefined"
- `mobile-system-demo`: expected "undefined"
- `mobile-system-arena-404`: expected "undefined"
- `mobile-system-login-already`: expected "undefined"
- `mobile-system-not-found`: expected "undefined"
- `desktop-light-arena-404`: expected "undefined"
- `desktop-light-login-already`: expected "undefined"
- `desktop-light-not-found`: expected "undefined"
- `desktop-dark-arena-404`: expected "undefined"
- `desktop-dark-login-already`: expected "undefined"
- `desktop-dark-not-found`: expected "undefined"

## Axe violations by pair

- `mobile-light-home`: `color-contrast`×4
- `mobile-light-leaderboard`: `color-contrast`×1
- `mobile-dark-home`: `color-contrast`×1
- `mobile-dark-leaderboard`: `color-contrast`×1
- `mobile-system-home`: `color-contrast`×10
- `mobile-system-leaderboard`: `color-contrast`×1
- `desktop-light-home`: `color-contrast`×10
- `desktop-light-leaderboard`: `color-contrast`×1
- `desktop-light-demo`: `color-contrast`×1
- `desktop-dark-leaderboard`: `color-contrast`×1
- `desktop-system-home`: `color-contrast`×10