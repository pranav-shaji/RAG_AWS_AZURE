export interface AdminDashboardStats {
  totalUsers: number;

  totalDocuments: number;

  indexedDocuments: number;

  failedDocuments: number;

  processingDocuments: number;

  totalConversations: number;

  totalMessages: number;

  totalChunks: number;

  totalPages: number;

  generatedAtUtc: string;
}

export interface AdminDocumentMonitoring {
  documentId: string;

  ownerUserId: string;

  fileName: string;

  status: string;

  chunkCount: number;

  pageCount: number;

  createdAtUtc: string;

  updatedAtUtc: string;

  allowedRoles: string[];
}

export interface AdminDocumentMonitoringPage {
  items: AdminDocumentMonitoring[];

  nextToken?: string | null;
}

export interface AdminConversationAnalytics {
  sessionId: string;

  ownerUserId: string;

  title: string;

  messageCount: number;

  createdAtUtc: string;

  lastMessageAtUtc: string;
}

export interface AdminIngestionStatus {
  uploaded: number;

  indexed: number;

  failed: number;

  processing: number;

  generatedAtUtc: string;
}

export interface EnterpriseUser {
  userId: string;
  email: string;
  approvalStatus: string;
  approvedRole: string;
  createdAtUtc: string;
  updatedAtUtc: string;
  approvedBy: string;
  approvedAtUtc?: string | null;
}
