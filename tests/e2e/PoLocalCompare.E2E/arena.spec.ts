import { test, expect } from '@playwright/test';

/**
 * E2E tests for the Arena page (duel judging).
 * Uses route interception to mock a completed duel state.
 */

const DUEL_ID = 'e2e-arena-duel-001';
const LEFT_MODEL_ID = 'arena-model-left';
const RIGHT_MODEL_ID = 'arena-model-right';

const MOCK_LEFT_RESULT = {
  duelId: DUEL_ID,
  modelId: LEFT_MODEL_ID,
  modelName: 'Left LLM',
  side: 'Left',
  htmlOutputRaw: '<html><body><h1>Left Stopwatch</h1><button>Start</button></body></html>',
  tokenCount: 120,
  totalDurationMs: 4200,
  isFailure: false,
  failureReason: null,
  costUsd: 0.002,
  inputTokens: 30,
  outputTokens: 90,
  characterDensity: 0.85,
  greenScoreWatts: 0.42,
};

const MOCK_RIGHT_RESULT = {
  duelId: DUEL_ID,
  modelId: RIGHT_MODEL_ID,
  modelName: 'Right LLM',
  side: 'Right',
  htmlOutputRaw: '<html><body><h1>Right Stopwatch</h1><button>Begin</button></body></html>',
  tokenCount: 98,
  totalDurationMs: 3800,
  isFailure: false,
  failureReason: null,
  costUsd: 0.0018,
  inputTokens: 28,
  outputTokens: 70,
  characterDensity: 0.78,
  greenScoreWatts: 0.38,
};

const MOCK_DUEL = {
  duelId: DUEL_ID,
  leftModelId: LEFT_MODEL_ID,
  rightModelId: RIGHT_MODEL_ID,
  promptText: 'Build a stopwatch in HTML.',
  promptFull: 'Build a stopwatch in HTML.',
  startedAt: new Date(Date.now() - 60_000).toISOString(),
  completedAt: new Date().toISOString(),
  verdict: 'Pending',
  timeLimitSeconds: 300,
  results: [MOCK_LEFT_RESULT, MOCK_RIGHT_RESULT],
};

const MOCK_VERDICT_RESPONSE = {
  duelId: DUEL_ID,
  verdict: 'Left',
  winnerModelId: LEFT_MODEL_ID,
  loserModelId: RIGHT_MODEL_ID,
  eloShiftWinner: 12.4,
  eloShiftLoser: -12.4,
};

test.describe('Arena', () => {
  test.beforeEach(async ({ page }) => {
    // Mock the duel detail endpoint (results embedded so component reads _duel.Results)
    await page.route(`**/api/duels/${DUEL_ID}`, route => {
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify(MOCK_DUEL),
      });
    });

    // Mock the duel results endpoint (kept for completeness)
    await page.route(`**/api/duels/${DUEL_ID}/results`, route => {
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify([MOCK_LEFT_RESULT, MOCK_RIGHT_RESULT]),
      });
    });

    // Inject guest identity so App.razor auth gate allows access (GuestAuthService reads sessionStorage)
    await page.addInitScript(() => {
      sessionStorage.setItem('guest_identity', 'GUEST_E2E_TEST');
    });

    await page.goto(`/arena/${DUEL_ID}`);
    // Wait for Blazor WASM to fully load (network becomes idle when all WASM chunks are fetched)
    await page.waitForLoadState('networkidle', { timeout: 45_000 });
    // Wait for async data load to complete (loading spinner disappears)
    await expect(page.locator('.arena__loading')).toHaveCount(0, { timeout: 15_000 });
  });

  test('Both viewport panels render on Arena page', async ({ page }) => {
    const panels = page.locator('.arena__viewport-panel');
    await expect(panels).toHaveCount(2);
  });

  test('HUD fields are present in both panels', async ({ page }) => {
    const huds = page.locator('.arena__hud');
    await expect(huds).toHaveCount(2);
  });

  test('Arena title is displayed', async ({ page }) => {
    await expect(page.locator('.arena__title')).toContainText('Arena');
  });

  test('Winner buttons are initially enabled (verdict not yet recorded)', async ({ page }) => {
    const leftWinBtn = page.locator('.arena__action-btn').filter({ hasText: 'Winner: Left' });
    const rightWinBtn = page.locator('.arena__action-btn').filter({ hasText: 'Winner: Right' });

    await expect(leftWinBtn).toBeEnabled();
    await expect(rightWinBtn).toBeEnabled();
  });

  test('Clicking Winner: Left shows ELO badge and marks loser dimmed', async ({ page }) => {
    // Mock the verdict POST (set up before clicking, navigation already done in beforeEach)
    await page.route(`**/api/duels/${DUEL_ID}/verdict`, route => {
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify(MOCK_VERDICT_RESPONSE),
      });
    });

    // Click "Winner: Left"
    await page.locator('.arena__action-btn').filter({ hasText: 'Winner: Left' }).click();

    // ELO badge should appear (positive for winner)
    const eloBadge = page.locator('.arena__elo-badge--positive').first();
    await expect(eloBadge).toBeVisible({ timeout: 5_000 });
    await expect(eloBadge).toContainText('+');

    // Winner buttons should now be disabled (verdict recorded)
    const leftWinBtn = page.locator('.arena__action-btn').filter({ hasText: 'Winner: Left' });
    await expect(leftWinBtn).toBeDisabled();
  });

  test('Loser panel has dimmed CSS class after verdict', async ({ page }) => {
    await page.route(`**/api/duels/${DUEL_ID}/verdict`, route => {
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify(MOCK_VERDICT_RESPONSE),
      });
    });

    // Click "Winner: Left" — right panel should become loser
    await page.locator('.arena__action-btn').filter({ hasText: 'Winner: Left' }).click();

    // SandboxedViewport renders sandboxed-viewport--loser on the loser's wrapper div
    const loserPanel = page.locator('.sandboxed-viewport--loser');
    await expect(loserPanel).toHaveCount(1, { timeout: 5_000 });
  });

  test('Prompt text is displayed in Arena page', async ({ page }) => {
    await expect(page.locator('.arena__prompt-label')).toContainText('Build a stopwatch in HTML.');
  });
});
