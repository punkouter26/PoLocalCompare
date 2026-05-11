import { test, expect } from '@playwright/test';

/**
 * E2E tests for the Arena page (duel judging).
 * Uses route interception to mock a completed duel state.
 */

const DUEL_ID = 'e2e-arena-duel-001';
const LEFT_MODEL_ID = 'arena-model-left';
const RIGHT_MODEL_ID = 'arena-model-right';

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
};

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
    // Mock the duel detail endpoint
    await page.route(`**/api/duels/${DUEL_ID}`, route => {
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify(MOCK_DUEL),
      });
    });

    // Mock the duel results endpoint
    await page.route(`**/api/duels/${DUEL_ID}/results`, route => {
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify([MOCK_LEFT_RESULT, MOCK_RIGHT_RESULT]),
      });
    });
  });

  test('Both viewport panels render on Arena page', async ({ page }) => {
    await page.goto(`/arena/${DUEL_ID}`);

    // Wait for loading to finish
    await expect(page.locator('.arena__loading')).toHaveCount(0, { timeout: 10_000 });

    // Both viewport panels should be present
    const panels = page.locator('.arena__viewport-panel');
    await expect(panels).toHaveCount(2);
  });

  test('HUD fields are present in both panels', async ({ page }) => {
    await page.goto(`/arena/${DUEL_ID}`);
    await expect(page.locator('.arena__loading')).toHaveCount(0, { timeout: 10_000 });

    // TelemetryHud should appear inside each viewport panel
    const huds = page.locator('.arena__hud');
    await expect(huds).toHaveCount(2);
  });

  test('Arena title is displayed', async ({ page }) => {
    await page.goto(`/arena/${DUEL_ID}`);
    await expect(page.locator('.arena__loading')).toHaveCount(0, { timeout: 10_000 });

    await expect(page.locator('.arena__title')).toContainText('Arena');
  });

  test('Winner buttons are initially enabled (verdict not yet recorded)', async ({ page }) => {
    await page.goto(`/arena/${DUEL_ID}`);
    await expect(page.locator('.arena__loading')).toHaveCount(0, { timeout: 10_000 });

    // Both winner buttons should be present and enabled
    const leftWinBtn = page.locator('.arena__action-btn').filter({ hasText: 'Winner: Left' });
    const rightWinBtn = page.locator('.arena__action-btn').filter({ hasText: 'Winner: Right' });

    await expect(leftWinBtn).toBeEnabled();
    await expect(rightWinBtn).toBeEnabled();
  });

  test('Clicking Winner: Left shows ELO badge and marks loser dimmed', async ({ page }) => {
    // Mock the verdict POST
    await page.route(`**/api/duels/${DUEL_ID}/verdict`, route => {
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify(MOCK_VERDICT_RESPONSE),
      });
    });

    await page.goto(`/arena/${DUEL_ID}`);
    await expect(page.locator('.arena__loading')).toHaveCount(0, { timeout: 10_000 });

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

    await page.goto(`/arena/${DUEL_ID}`);
    await expect(page.locator('.arena__loading')).toHaveCount(0, { timeout: 10_000 });

    // Click "Winner: Left" — right panel should become loser
    await page.locator('.arena__action-btn').filter({ hasText: 'Winner: Left' }).click();

    // The SandboxedViewport for the right (loser) should receive IsLoser=true
    // which renders a CSS class indicating loser state
    // Check that at least one viewport has the loser indicator
    const loserPanel = page.locator('[class*="loser"]').or(page.locator('.viewport--loser'));
    await expect(loserPanel).toHaveCount(1, { timeout: 5_000 });
  });

  test('Prompt text is displayed in Arena page', async ({ page }) => {
    await page.goto(`/arena/${DUEL_ID}`);
    await expect(page.locator('.arena__loading')).toHaveCount(0, { timeout: 10_000 });

    await expect(page.locator('.arena__prompt-label')).toContainText('Build a stopwatch in HTML.');
  });
});
