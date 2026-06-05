import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../environments/environment';

export interface UploadResponse {
  documentId: string;
  existingDocumentId?: string | null;
  fileName: string;
  storageKey: string;
  isDuplicate?: boolean;
  status?: string;
  message: string;
}

export interface Citation {
  documentId: string;
  chunkId: string;
  fileName: string;
  pageNumber: number;
  snippet: string;
}

export interface DocumentMetadata {
  documentId: string;
  ownerUserId: string;
  fileName: string;
  storageKey: string;
  fileHash: string;
  status: string;
  chunkCount: number;
  createdAtUtc: string;
  updatedAtUtc: string;
  pageCount: number;
  allowedRoles: string[];
}

export interface ChartData {
  labels: string[];
  values: number[];
}

export interface TableData {
  columns: string[];
  rows: string[][];
}

export interface InteractiveOption {
  label: string;
  description: string;
  action: string;
  prompt: string;
}

export interface InteractiveOptionsData {
  options: InteractiveOption[];
}

export interface DocumentSelectorData {
  documents: DocumentMetadata[];
  selectedDocumentId?: string | null;
}

export interface DocumentImage {
  documentId: string;
  fileName: string;
  url: string;
  pageNumber: number;
  sourceType: string;
}

export interface ChatAskRequest {
  sessionId: string;
  documentId: string | null;
  searchAcrossAllDocuments: boolean;
  question: string;
  outputFormat: string;
}

export interface ChatAskResponse {
  responseType: string;
  answer: string;
  citations: Citation[];
  data?: unknown;

  images?: DocumentImage[] | null;
  chartData?: ChartData | null;
}

export interface CreateConversationRequest {
  title?: string | null;
}

export interface ConversationSession {
  sessionId: string;
  title: string;
  summary: string;
  createdAtUtc: string;
  updatedAtUtc: string;
  lastMessageAtUtc: string;
  messageCount: number;
  isArchived: boolean;
}

export interface ConversationMessage {
  messageId: string;
  sessionId: string;
  role: string;
  content: string;
  createdAtUtc: string;
  tokensApprox: number;
  citations: Citation[];

  data?: unknown;

  responseType?: string;

  chartData?: ChartData | null;
}

@Injectable({
  providedIn: 'root'
})
export class Api {

  private readonly http = inject(HttpClient);

  private readonly baseUrl = environment.apiBaseUrl;

  uploadDocument(file: File, allowedRoles: string[]): Observable<UploadResponse> {

    const formData = new FormData();

    formData.append('file', file);

    allowedRoles.forEach(role =>
      formData.append('allowedRoles', role));

    return this.http.post<UploadResponse>(
      `${this.baseUrl}/Documents/upload`,
      formData
    );
  }

  getDocuments(): Observable<DocumentMetadata[]> {

    return this.http.get<DocumentMetadata[]>(
      `${this.baseUrl}/Documents`
    );
  }

  getDocument(
    documentId: string
  ): Observable<DocumentMetadata> {

    return this.http.get<DocumentMetadata>(
      `${this.baseUrl}/Documents/${encodeURIComponent(documentId)}`
    );
  }

  createConversation(
    payload: CreateConversationRequest = {}
  ): Observable<ConversationSession> {

    return this.http.post<ConversationSession>(
      `${this.baseUrl}/Conversations`,
      payload
    );
  }

  getConversations(): Observable<ConversationSession[]> {

    return this.http.get<ConversationSession[]>(
      `${this.baseUrl}/Conversations`
    );
  }

  getConversationMessages(
    sessionId: string,
    take = 100
  ): Observable<ConversationMessage[]> {

    return this.http.get<ConversationMessage[]>(
      `${this.baseUrl}/Conversations/${encodeURIComponent(sessionId)}/messages?take=${take}`
    );
  }

  deleteConversation(sessionId: string): Observable<void> {

    return this.http.delete<void>(
      `${this.baseUrl}/Conversations/${encodeURIComponent(sessionId)}`
    );
  }

  askQuestion(
    payload: ChatAskRequest
  ): Observable<ChatAskResponse> {

    return this.http.post<ChatAskResponse>(
      `${this.baseUrl}/Chat/ask`,
      payload
    );
  }
}
