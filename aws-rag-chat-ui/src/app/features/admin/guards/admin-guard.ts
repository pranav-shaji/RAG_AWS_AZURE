import { inject } from '@angular/core';

import {
  CanActivateFn,
  Router
} from '@angular/router';

import { Auth } from '../../../auth';

export const adminGuard: CanActivateFn = () => {

  const auth = inject(Auth);

  const router = inject(Router);

  if (!auth.isAuthenticated()) {

    router.navigateByUrl('/');

    return false;
  }

  if (!auth.isAdmin()) {

    router.navigateByUrl('/');

    return false;
  }

  return true;
};
