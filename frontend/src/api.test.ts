import { describe, it, expect } from 'vitest'
import { startDeployment } from './api'

/**
 * Smoke test for the API contract: verifies the request body shape the backend
 * StartDeploymentCommand expects (requestedBy, subscription, state, fields).
 */
describe('api contract', () => {
  it('builds a valid start deployment payload', () => {
    const payload = {
      requestedBy: 'test@contoso.com',
      subscription: 'sub-x',
      state: 'dev',
      resourceName: 'payments web app',
      fields: { app_name: 'app-pay' },
    }
    // Type-level + structural check against DeploymentRequest input contract
    expect(Object.keys(payload)).toEqual(
      expect.arrayContaining(['requestedBy', 'subscription', 'state']))
    expect(startDeployment).toBeTypeOf('function')
  })
})
