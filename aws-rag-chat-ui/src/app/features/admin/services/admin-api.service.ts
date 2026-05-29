import { Injectable, inject } from '@angular/core';

import {
  HttpClient
} from '@angular/common/http';

import { Observable } from 'rxjs';

import { environment } from '../../../../environments/environment';

import {
  AdminConversationAnalytics,
  AdminDashboardStats,
  AdminDocumentMonitoringPage,
  AdminDocumentMonitoring,
  AdminIngestionStatus,
  EnterpriseUser
} from '../models/admin-dashboard.model';

@Injectable({
  providedIn: 'root'
})
export class AdminApiService {

  private readonly http =
    inject(HttpClient);

  private readonly baseUrl =
    `${environment.apiBaseUrl}/Admin`;

  getDashboardStats():
    Observable<AdminDashboardStats> {

    return this.http.get<AdminDashboardStats>(
      `${this.baseUrl}/dashboard`);
  }

  getDocuments(
    pageSize = 20,
    nextToken: string | null = null
  ): Observable<AdminDocumentMonitoringPage> {

    const params = new URLSearchParams({
      pageSize: String(pageSize)
    });

    if (nextToken)
      params.set('nextToken', nextToken);

    return this.http.get<AdminDocumentMonitoringPage>(
      `${this.baseUrl}/documents?${params.toString()}`);
  }

  getDocumentPreviewUrl(
    documentId: string
  ): Observable<{ url: string }> {

    return this.http.get<{ url: string }>(
      `${this.baseUrl}/documents/${encodeURIComponent(documentId)}/preview-url`);
  }

  getConversations(take = 50):
    Observable<AdminConversationAnalytics[]> {

    return this.http.get<AdminConversationAnalytics[]>(
      `${this.baseUrl}/conversations?take=${take}`);
  }

  getIngestionStatus():
    Observable<AdminIngestionStatus> {

    return this.http.get<AdminIngestionStatus>(
      `${this.baseUrl}/ingestion-status`);
  }

  getUsers(): Observable<EnterpriseUser[]> {

    return this.http.get<EnterpriseUser[]>(
      `${this.baseUrl}/users`);
  }

  approveUser(
    userId: string,
    role: string
  ): Observable<EnterpriseUser> {

    return this.http.post<EnterpriseUser>(
      `${this.baseUrl}/users/${encodeURIComponent(userId)}/approve`,
      { role });
  }
}
