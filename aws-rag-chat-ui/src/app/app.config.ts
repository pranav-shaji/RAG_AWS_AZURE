import {
  ApplicationConfig,
  provideBrowserGlobalErrorListeners,
  provideZonelessChangeDetection
} from '@angular/core';

import {
  provideHttpClient,
  withInterceptors
} from '@angular/common/http';

import {
  provideRouter
} from '@angular/router';

import { authInterceptor } from './auth-interceptor';

import { routes } from './app.routes';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),

    provideZonelessChangeDetection(),

    provideHttpClient(
      withInterceptors([authInterceptor])
    ),

    provideRouter(routes)
  ]
};