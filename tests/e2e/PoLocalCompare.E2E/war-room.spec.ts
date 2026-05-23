import { test, expect } from '@playwright/test';

/**
 * E2E tests for the War Room page.
 * Uses route interception to mock API responses so tests run
 * without a seeded database.
 */

const MODEL_A = {
  modelId: 'e2e-model-a',
  displayName: 'Alpha LLM',
  modelType: 'Remote',
  currentElo: 1200,
  duelCount: 0,
  winCount: 0,
  greenScoreAvg: 0.0,
};

const MODEL_B = {
  modelId: 'e2e-model-b',
  displayName: 'Beta LLM',
  modelType: 'Remote',
  currentElo: 1200,
  duelCount: 0,
  winCount: 0,
  greenScoreAvg: 0.0,
};

async function mockModels(page: ReturnType<typeof test.extend>['page'] extends never ? never : Parameters<Parameters<typeof test>[1]>[0]['page']) {
  await page.route('**/api/models', route => {
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify([MODEL_A, MODEL_B]),
    });
  });
}

test.describe('War Room', () => {
  test.beforeEach(async ({ page }) => {
    // Mock the models API before each test
    await page.route('**/api/models', route => {
      if (route.request().url().includes('/availability')) {
        // Let availability sub-path fall through to next handler
        route.continue();
        return;
      }
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify([MODEL_A, MODEL_B]),
      });
    });

    // Mock availability so Remote models are selectable
    await page.route('**/api/models/availability', route => {
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify([
          { modelId: MODEL_A.modelId, isAvailable: true, reason: null },
          { modelId: MODEL_B.modelId, isAvailable: true, reason: null },
        ]),
      });
    });

    // Inject guest identity so App.razor auth gate allows access (GuestAuthService reads sessionStorage)
    await page.addInitScript(() => {
      sessionStorage.setItem('guest_identity', 'GUEST_E2E_TEST');
    });

    await page.goto('/war-room');
    // Wait for Blazor WASM to fully load
    await page.waitForLoadState('networkidle', { timeout: 45_000 });
    // Wait for loading panel to disappear (class is war-room__loading-panel, not war-room__loading)
    await expect(page.locator('.war-room__loading-panel')).toHaveCount(0, { timeout: 15_000 });
  });

  test('Commence button is disabled when no models are selected', async ({ page }) => {
    const commenceBtn = page.locator('.war-room__commence-btn');
    await expect(commenceBtn).toBeVisible();
    await expect(commenceBtn).toBeDisabled();
  });

  test('Commence button remains disabled when only left model selected', async ({ page }) => {
    // Select first model card from the flat pool
    await page.locator('.model-card').first().click();

    const commenceBtn = page.locator('.war-room__commence-btn');
    await expect(commenceBtn).toBeDisabled();
  });

  test('Commence button enabled after selecting both models and entering prompt', async ({ page }) => {
    // Select first and second model cards from the flat pool
    await page.locator('.model-card').first().click();
    await page.locator('.model-card').nth(1).click();

    // Enter prompt text
    const promptInput = page.locator('#promptInput');
    await promptInput.fill('Build a simple HTML countdown timer.');

    const commenceBtn = page.locator('.war-room__commence-btn');
    await expect(commenceBtn).toBeEnabled();
  });

  test('Clicking Commence navigates away from War Room', async ({ page }) => {
    // Mock the POST /api/duels endpoint
    await page.route('**/api/duels', route => {
      if (route.request().method() === 'POST') {
        route.fulfill({
          status: 202,
          contentType: 'application/json',
          body: JSON.stringify({
            duelId: 'e2e-duel-001',
            leftModelId: MODEL_A.modelId,
            rightModelId: MODEL_B.modelId,
            promptText: 'Build a timer.',
            promptFull: 'Build a timer.',
            startedAt: new Date().toISOString(),
            completedAt: null,
            verdict: 'Pending',
            timeLimitSeconds: 300,
          }),
        });
      } else {
        route.continue();
      }
    });

    // Select both models and enter prompt
    await page.locator('.model-card').first().click();
    await page.locator('.model-card').nth(1).click();
    await page.locator('#promptInput').fill('Build a timer.');

    // Click Commence
    await page.locator('.war-room__commence-btn').click();

    // Should navigate away from /war-room (to /processing/... or /arena/...)
    await expect(page).not.toHaveURL(/\/war-room$/, { timeout: 5_000 });
  });

  test('Page title shows War Room heading', async ({ page }) => {
    await expect(page.locator('.war-room__title')).toContainText('War Room');
  });
});
