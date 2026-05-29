import {
  Component,
  OnInit,
  computed,
  inject,
  signal
} from '@angular/core';

import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';

import { AdminApiService }
  from '../../services/admin-api.service';

import {
  AdminDashboardStats,
  AdminDocumentMonitoring,
  AdminIngestionStatus,
  EnterpriseUser
}
  from '../../models/admin-dashboard.model';

@Component({
  selector: 'app-admin-dashboard',

  standalone: true,

  imports: [
    CommonModule,
    FormsModule,
    RouterLink
  ],

  templateUrl: './admin-dashboard.html',

  styleUrls: ['./admin-dashboard.css']
})
export class AdminDashboardPage
  implements OnInit {

  private readonly adminApi =
    inject(AdminApiService);

  readonly loading =
    computed(() =>
      this.loadingStats() ||
      this.loadingDocuments() ||
      this.loadingUsers());

  readonly loadingStats =
    signal<boolean>(false);

  readonly loadingDocuments =
    signal<boolean>(false);

  readonly loadingUsers =
    signal<boolean>(false);

  readonly error =
    signal<string>('');

  readonly stats =
    signal<AdminDashboardStats | null>(null);

  readonly documents =
    signal<AdminDocumentMonitoring[]>([]);

  readonly documentNextToken =
    signal<string | null>(null);

  readonly documentPageIndex =
    signal<number>(0);

  readonly documentSearch =
    signal<string>('');

  readonly filteredDocuments =
    computed(() => {

      const searchTerm =
        this.documentSearch().trim().toLowerCase();

      if (!searchTerm)
        return this.documents();

      return this.documents()
        .filter(document =>
          document.fileName
            .toLowerCase()
            .includes(searchTerm));
    });

  readonly ingestion =
    signal<AdminIngestionStatus | null>(null);

  readonly users =
    signal<EnterpriseUser[]>([]);

  readonly roleOptions = ['User', 'Manager', 'HR', 'Admin'];

  readonly selectedUserRoles =
    signal<Record<string, string>>({});

  readonly editingUserRoles =
    signal<Record<string, boolean>>({});

  private readonly documentPageSize = 20;

  private documentPreviousTokens: Array<string | null> = [];

  private currentDocumentPageToken: string | null = null;

  ngOnInit(): void {

    this.loadDashboard();
  }

  loadDashboard(): void {

    this.error.set('');

    this.documentPreviousTokens = [];
    this.currentDocumentPageToken = null;
    this.documentPageIndex.set(0);

    this.loadStats();
    this.loadDocumentsPage(null, false);
    this.loadUsers();
  }

  approveUser(user: EnterpriseUser): void {

    const role =
      this.selectedUserRoles()[user.userId] || user.approvedRole || 'User';

    this.adminApi.approveUser(user.userId, role)
      .subscribe({
        next: (updatedUser) => {
          this.users.update(items =>
            items.map(item => item.userId === updatedUser.userId
              ? updatedUser
              : item));
          this.selectedUserRoles.update(map => ({
            ...map,
            [updatedUser.userId]: updatedUser.approvedRole
          }));
          this.editingUserRoles.update(map => ({
            ...map,
            [updatedUser.userId]: false
          }));
        },
        error: () => {
          this.error.set('User approval could not be saved.');
        }
      });
  }

  editUserRole(user: EnterpriseUser): void {

    this.selectedUserRoles.update(map => ({
      ...map,
      [user.userId]: user.approvedRole || 'User'
    }));

    this.editingUserRoles.update(map => ({
      ...map,
      [user.userId]: true
    }));
  }

  cancelEditUserRole(user: EnterpriseUser): void {

    this.selectedUserRoles.update(map => ({
      ...map,
      [user.userId]: user.approvedRole || 'User'
    }));

    this.editingUserRoles.update(map => ({
      ...map,
      [user.userId]: false
    }));
  }

  roleBadgeClass(role: string): string {

    return `role-badge role-${(role || 'user').toLowerCase()}`;
  }

  statusBadgeClass(status: string): string {

    return `status-pill status-${(status || 'pending').toLowerCase()}`;
  }

  roleList(document: AdminDocumentMonitoring): string[] {

    return document.allowedRoles?.length
      ? document.allowedRoles
      : [];
  }

  formatPageCount(pageCount: number): string {

    return pageCount > 0
      ? String(pageCount)
      : 'Not available';
  }

  setSelectedUserRole(userId: string, role: string): void {

    this.selectedUserRoles.update(map => ({
      ...map,
      [userId]: role
    }));
  }

  loadNextDocumentPage(): void {

    const nextToken = this.documentNextToken();

    if (!nextToken || this.loadingDocuments())
      return;

    this.documentPreviousTokens.push(this.currentDocumentPageToken);
    this.loadDocumentsPage(nextToken, true);
  }

  loadPreviousDocumentPage(): void {

    if (this.documentPreviousTokens.length === 0 || this.loadingDocuments())
      return;

    const previousToken =
      this.documentPreviousTokens.pop() ?? null;

    this.loadDocumentsPage(previousToken, false);
  }

  private loadStats(): void {

    this.loadingStats.set(true);

    this.adminApi.getDashboardStats()
      .subscribe({

        next: (stats) => {

          this.stats.set(stats);

          this.ingestion.set({
            uploaded: stats.processingDocuments,
            indexed: stats.indexedDocuments,
            processing: stats.processingDocuments,
            failed: stats.failedDocuments,
            generatedAtUtc: stats.generatedAtUtc
          });

          this.loadingStats.set(false);
        },

        error: (error) => {

          console.error(
            'Failed to load admin dashboard stats',
            error);

          this.error.set(
            'Admin dashboard stats could not be loaded.');

          this.loadingStats.set(false);
        }
      });
  }

  private loadDocumentsPage(
    nextToken: string | null,
    incrementPage: boolean
  ): void {

    this.loadingDocuments.set(true);

    this.adminApi.getDocuments(
      this.documentPageSize,
      nextToken)
      .subscribe({

        next: (page) => {

          this.currentDocumentPageToken = nextToken;
          this.documents.set(page.items);
          this.documentNextToken.set(page.nextToken ?? null);
          this.documentPageIndex.update(index =>
            incrementPage ? index + 1 : this.documentPreviousTokens.length);
          this.loadingDocuments.set(false);
        },

        error: (error) => {

          console.error(
            'Failed to load admin documents',
            error);

          this.error.set(
            'Uploaded documents could not be loaded.');

          this.loadingDocuments.set(false);
        }
      });
  }

  private loadUsers(): void {

    this.loadingUsers.set(true);

    this.adminApi.getUsers()
      .subscribe({
        next: (users) => {
          this.users.set(users);
          this.selectedUserRoles.set(
            Object.fromEntries(users.map(user => [
              user.userId,
              user.approvedRole || 'User'
            ]))
          );
          this.loadingUsers.set(false);
        },
        error: () => {
          this.error.set('Enterprise users could not be loaded.');
          this.loadingUsers.set(false);
        }
      });
  }

  openDocumentPreview(
    document: AdminDocumentMonitoring
  ): void {

    if (!document.documentId)
      return;

    this.adminApi.getDocumentPreviewUrl(
      document.documentId)
      .subscribe({
        next: (response) => {
          if (response.url)
            window.open(response.url, '_blank', 'noopener,noreferrer');
        },
        error: () => {
          this.error.set(
            'Document preview could not be opened.');
        }
      });
  }
}
