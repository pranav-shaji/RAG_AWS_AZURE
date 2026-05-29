import { Routes } from '@angular/router';

import { ChatPage } from './pages/chat-page/chat-page';

export const routes: Routes = [
  {
    path: '',
    component: ChatPage
  },

  {
    path: 'admin',
    loadChildren: () =>
      import('./features/admin/admin.routes')
        .then(r => r.ADMIN_ROUTES)
  }

];
