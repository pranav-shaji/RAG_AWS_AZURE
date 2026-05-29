import { Routes } from '@angular/router';
import { AdminDashboardPage } from './pages/admin-dashboard/admin-dashboard';
import { adminGuard } from './guards/admin-guard';

export const ADMIN_ROUTES: Routes = [
  {
    path: '',
    component: AdminDashboardPage,
    canActivate: [adminGuard]
  }
];