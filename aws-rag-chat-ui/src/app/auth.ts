import {
  Injectable,
  signal,
  computed
} from '@angular/core';

import { environment } from '../environments/environment';

interface TokenResponse {
  access_token: string;
  id_token: string;
  refresh_token?: string;
  expires_in: number;
  token_type: string;
}

interface JwtPayload {
  sub?: string;
  email?: string;
  exp?: number;

  'cognito:groups'?: string[] | string;

  [key: string]: unknown;
}

@Injectable({
  providedIn: 'root'
})
export class Auth {

  private readonly clientId =
    environment.cognitoClientId;

  private readonly domain =
    environment.cognitoDomain;

  private readonly redirectUri =
    window.location.origin;

  private readonly scopes =
    environment.cognitoScopes;

  readonly isAuthenticated =
    signal<boolean>(false);

  readonly userEmail =
    signal<string>('');

  readonly userId =
    signal<string>('');

  readonly role =
    signal<string>('');

  readonly groups =
    signal<string[]>([]);

  readonly idToken =
    signal<string>('');

  readonly accessToken =
    signal<string>('');

  readonly isAdmin = computed(() =>
    this.hasRole('Admin'));

  readonly isManager = computed(() =>
    this.hasRole('Manager'));

  readonly isUser = computed(() =>
    this.hasRole('User'));

  constructor() {

    (window as any).auth = this;

    this.restoreSessionFromStorage();

    void this.handleAuthCallbackIfPresent();
  }

  login(): void {

    const codeVerifier =
      this.generateRandomString(128);

    const state =
      this.generateRandomString(32);

    localStorage.setItem(
      'pkce_code_verifier',
      codeVerifier);

    localStorage.setItem(
      'auth_state',
      state);

    void this.createCodeChallenge(codeVerifier)
      .then((codeChallenge) => {

        const authorizeUrl =
          `${this.domain}/oauth2/authorize` +
          `?response_type=code` +
          `&client_id=${encodeURIComponent(this.clientId)}` +
          `&redirect_uri=${encodeURIComponent(this.redirectUri)}` +
          `&scope=${encodeURIComponent(this.scopes)}` +
          `&code_challenge_method=S256` +
          `&code_challenge=${encodeURIComponent(codeChallenge)}` +
          `&state=${encodeURIComponent(state)}`;

        window.location.href = authorizeUrl;
      });
  }

  logout(): void {

    this.clearSession();

    const logoutUrl =
      `${this.domain}/logout` +
      `?client_id=${encodeURIComponent(this.clientId)}` +
      `&logout_uri=${encodeURIComponent(this.redirectUri)}`;

    window.location.href = logoutUrl;
  }

  getAccessToken(): string {

    if (this.isStoredTokenExpired()) {

      this.clearSession();

      return '';
    }

    return this.accessToken();
  }

  clearLocalSession(): void {

    this.clearSession();
  }

  hasRole(role: string): boolean {

    return this.groups()
      .some(group =>
        group.toLowerCase() === role.toLowerCase());
  }

  private async handleAuthCallbackIfPresent(): Promise<void> {

    const url =
      new URL(window.location.href);

    const code =
      url.searchParams.get('code');

    const returnedState =
      url.searchParams.get('state');

    const storedState =
      localStorage.getItem('auth_state');

    if (!code)
      return;

    if (
      !returnedState ||
      returnedState !== storedState
    ) {

      this.clearSession();

      window.history.replaceState(
        {},
        document.title,
        this.redirectUri);

      return;
    }

    const codeVerifier =
      localStorage.getItem(
        'pkce_code_verifier');

    if (!codeVerifier) {

      this.clearSession();

      window.history.replaceState(
        {},
        document.title,
        this.redirectUri);

      return;
    }

    try {

      const tokenResponse =
        await this.exchangeCodeForTokens(
          code,
          codeVerifier);

      this.storeSession(tokenResponse);

      localStorage.removeItem(
        'pkce_code_verifier');

      localStorage.removeItem(
        'auth_state');

      window.history.replaceState(
        {},
        document.title,
        this.redirectUri);
    }
    catch (error) {

      console.error(
        'Token exchange failed:',
        error);

      this.clearSession();

      window.history.replaceState(
        {},
        document.title,
        this.redirectUri);
    }
  }

  private async exchangeCodeForTokens(
    code: string,
    codeVerifier: string
  ): Promise<TokenResponse> {

    const body =
      new URLSearchParams({
        grant_type: 'authorization_code',
        client_id: this.clientId,
        code,
        redirect_uri: this.redirectUri,
        code_verifier: codeVerifier
      });

    const response =
      await fetch(
        `${this.domain}/oauth2/token`,
        {
          method: 'POST',
          headers: {
            'Content-Type':
              'application/x-www-form-urlencoded'
          },
          body: body.toString()
        });

    if (!response.ok) {

      const text =
        await response.text();

      throw new Error(
        `Cognito token endpoint failed: ${text}`);
    }

    return await response.json() as TokenResponse;
  }

  private storeSession(
    tokenResponse: TokenResponse
  ): void {

    localStorage.setItem(
      'access_token',
      tokenResponse.access_token);

    localStorage.setItem(
      'id_token',
      tokenResponse.id_token);

    localStorage.setItem(
      'access_token_expires_at',
      String(
        Date.now() +
        tokenResponse.expires_in * 1000));

    this.accessToken.set(
      tokenResponse.access_token);

    this.idToken.set(
      tokenResponse.id_token);

    const accessPayload =
      this.decodeJwt(
        tokenResponse.access_token);

    const idPayload =
      this.decodeJwt(
        tokenResponse.id_token);

    this.userEmail.set(
      typeof idPayload.email === 'string'
        ? idPayload.email
        : '');

    this.userId.set(
      typeof accessPayload.sub === 'string'
        ? accessPayload.sub
        : '');

    this.applyGroups(
      accessPayload['cognito:groups'] ??
      idPayload['cognito:groups']);

    this.isAuthenticated.set(true);
  }

  private restoreSessionFromStorage(): void {

    const accessToken =
      localStorage.getItem('access_token');

    const idToken =
      localStorage.getItem('id_token');

    if (!accessToken || !idToken)
      return;

    if (this.isStoredTokenExpired()) {

      this.clearSession();

      return;
    }

    this.accessToken.set(accessToken);

    this.idToken.set(idToken);

    const accessPayload =
      this.decodeJwt(accessToken);

    const idPayload =
      this.decodeJwt(idToken);

    this.userEmail.set(
      typeof idPayload.email === 'string'
        ? idPayload.email
        : '');

    this.userId.set(
      typeof accessPayload.sub === 'string'
        ? accessPayload.sub
        : '');

    this.applyGroups(
      accessPayload['cognito:groups'] ??
      idPayload['cognito:groups']);

    this.isAuthenticated.set(true);
  }

  private clearSession(): void {

    localStorage.removeItem('access_token');

    localStorage.removeItem('id_token');

    localStorage.removeItem('refresh_token');

    localStorage.removeItem(
      'access_token_expires_at');

    localStorage.removeItem(
      'pkce_code_verifier');

    localStorage.removeItem(
      'auth_state');

    this.accessToken.set('');

    this.idToken.set('');

    this.userEmail.set('');

    this.userId.set('');

    this.role.set('');

    this.groups.set([]);

    this.isAuthenticated.set(false);
  }

  private decodeJwt(
    token: string
  ): JwtPayload {

    const parts =
      token.split('.');

    if (parts.length !== 3)
      return {};

    try {

      const payload =
        parts[1]
          .replace(/-/g, '+')
          .replace(/_/g, '/');

      return JSON.parse(
        atob(payload)
      ) as JwtPayload;
    }
    catch {

      return {};
    }
  }

  private applyGroups(
    groupsClaim: unknown
  ): void {

    const groups =
      Array.isArray(groupsClaim)
        ? groupsClaim.filter(
            (group): group is string =>
              typeof group === 'string')
        : typeof groupsClaim === 'string'
          ? [groupsClaim]
        : [];

    this.groups.set(groups);

    const adminGroup =
      groups.find(group =>
        group.toLowerCase() === 'admin');

    this.role.set(
      adminGroup
        ? adminGroup
        : groups[0] ?? '');
  }

  private isStoredTokenExpired(): boolean {

    const expiresAt =
      Number(
        localStorage.getItem(
          'access_token_expires_at') ?? '0');

    if (
      Number.isFinite(expiresAt) &&
      expiresAt > 0
    ) {

      return expiresAt <=
        Date.now() + 30_000;
    }

    const payload =
      this.decodeJwt(
        this.accessToken());

    return typeof payload.exp === 'number'
      ? payload.exp * 1000 <=
          Date.now() + 30_000
      : true;
  }

  private async createCodeChallenge(
    codeVerifier: string
  ): Promise<string> {

    const encoder =
      new TextEncoder();

    const data =
      encoder.encode(codeVerifier);

    const digest =
      await crypto.subtle.digest(
        'SHA-256',
        data);

    return btoa(
      String.fromCharCode(
        ...new Uint8Array(digest)))
      .replace(/\+/g, '-')
      .replace(/\//g, '_')
      .replace(/=+$/, '');
  }

  private generateRandomString(
    length: number
  ): string {

    const charset =
      'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~';

    const randomValues =
      new Uint8Array(length);

    crypto.getRandomValues(
      randomValues);

    return Array.from(randomValues)
      .map(x =>
        charset[x % charset.length])
      .join('');
  }
}
