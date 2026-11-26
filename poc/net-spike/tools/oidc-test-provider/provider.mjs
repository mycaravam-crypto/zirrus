// Spike-only OpenID provider: stands in for Keycloak because Maven Central rate-limited the
// Keycloak download (HTTP 429). Uses oidc-provider (MIT, OpenID Certified) dev interactions.
import Provider from 'oidc-provider';

const roles = { editor: ['editor'], viewer: ['viewer'], admin: ['admin'] };
const provider = new Provider('http://127.0.0.1:8180', {
  clients: [{
    client_id: 'zirrus-web',
    client_secret: 'spike-only-secret',
    redirect_uris: ['http://127.0.0.1:5080/signin-oidc'],
    post_logout_redirect_uris: ['http://127.0.0.1:5080/'],
    grant_types: ['authorization_code'],
    response_types: ['code'],
  }],
  pkce: { required: () => true },
  claims: { openid: ['sub'], profile: ['preferred_username', 'roles'] },
  conformIdTokenClaims: false, // put profile claims in the ID token for the ASP.NET handler
  findAccount: async (_ctx, id) => ({
    accountId: id,
    claims: async () => ({ sub: id, preferred_username: id, roles: roles[id] ?? [] }),
  }),
  features: { devInteractions: { enabled: true } },
});
provider.listen(8180, () => console.log('oidc test provider on http://127.0.0.1:8180'));
