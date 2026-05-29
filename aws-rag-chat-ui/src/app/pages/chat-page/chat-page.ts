import {
  Component,
  inject,
  signal,
  computed,
  effect,
  ElementRef,
  ViewChild,
  AfterViewChecked,
  OnDestroy,
  SecurityContext
} from '@angular/core';

import { CommonModule } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { RouterLink } from '@angular/router';

import { SidebarComponent } from '../../components/sidebar/sidebar';
import { MessageItemComponent } from '../../components/message-item/message-item';
import { UploadPanelComponent } from '../../components/upload-panel/upload-panel';
import { ChatInputComponent } from '../../components/chat-input/chat-input';

import { FormsModule } from '@angular/forms';

import {
  DomSanitizer,
  SafeHtml
} from '@angular/platform-browser';

import { marked } from 'marked';

import {
  Api,
  ChatAskResponse,
  DocumentMetadata,
  InteractiveOption,
  ConversationMessage,
  ConversationSession,
  UploadResponse
} from '../../api';

import { Auth } from '../../auth';

marked.use({
  gfm: true,
  breaks: false
});

@Component({
  selector: 'app-chat-page',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    RouterLink,
    SidebarComponent,
    MessageItemComponent,
    UploadPanelComponent,
    ChatInputComponent
  ],
  templateUrl: './chat-page.html',
  styleUrl: './chat-page.css'
})
export class ChatPage
implements AfterViewChecked, OnDestroy {

  @ViewChild('messagesContainer')
  messagesContainer?: ElementRef<HTMLDivElement>;

  private readonly api = inject(Api);

  private readonly sanitizer = inject(DomSanitizer);

  readonly auth = inject(Auth);

  readonly sessions = signal<ConversationSession[]>([]);

  readonly messages = signal<ConversationMessage[]>([]);

  readonly documents = signal<DocumentMetadata[]>([]);

  readonly selectedDocument = computed<DocumentMetadata | null>(() => {
    const documentId = this.uploadedDocumentId();
    return documentId
      ? this.documents().find(doc => doc.documentId === documentId) ?? null
      : null;
  });

  readonly activeSessionId = signal<string>('');

  readonly selectedFileName = signal<string>('');

  readonly uploadedDocumentId = signal<string>('');

  readonly uploadMessage = signal<string>('');

  readonly question = signal<string>('');

  readonly loadingUpload = signal<boolean>(false);

  readonly loadingAsk = signal<boolean>(false);

  readonly loadingSessions = signal<boolean>(false);

  readonly loadingMessages = signal<boolean>(false);

  readonly loadingDocuments = signal<boolean>(false);

  readonly documentLoadError = signal<string>('');

  readonly creatingConversation = signal<boolean>(false);

  readonly useGlobalSearch = signal<boolean>(false);

  readonly allowedUploadRoles = signal<string[]>([]);

  readonly errorMessage = signal<string>('');

  readonly assistantTyping = signal<boolean>(false);

  readonly sidebarCollapsed = signal<boolean>(false);

  private selectedFile: File | null = null;

  private lastScrollSignature = '';

  private askRequestId = 0;

  private loadMessagesRequestId = 0;

  private documentPollTimer: ReturnType<typeof setTimeout> | null = null;

  readonly pendingAskSessionId = signal<string>('');

  readonly deleteCandidate = signal<ConversationSession | null>(null);

  readonly deletingSessionId = signal<string>('');

  readonly suggestionPrompts = computed(() => {

    if (
      !this.auth.isAuthenticated() ||
      this.loadingAsk()
    ) {
      return [];
    }

    return [
      'Summarize uploaded documents',
      'Show citations from uploaded documents',
      'List all files/documents',
      'Search the knowledge base',
      'Find related content',
      'Retrieve from all indexed files'
    ];
  });

  readonly inputHint = computed(() => {

  return 'Ask questions from the enterprise knowledge base.';
});

  private readonly messageCache = new Map<string, ConversationMessage[]>();

  private selectedFileInput: HTMLInputElement | null = null;

  constructor() {
    effect(() => {
      if (this.auth.isAuthenticated()) {
        this.loadConversations();

        if (this.auth.isAdmin()) {
          this.loadDocuments();
        }

        return;
      }

      this.sessions.set([]);
      this.messages.set([]);
      this.documents.set([]);
      this.activeSessionId.set('');
      this.messageCache.clear();
      this.deleteCandidate.set(null);
      this.documentLoadError.set('');
      this.clearDocumentPoll();
    });
  }

  ngAfterViewChecked(): void {
    this.scrollToBottom();
  }

  ngOnDestroy(): void {
    this.clearDocumentPoll();
  }

  private scrollToBottom(): void {

    const container =
      this.messagesContainer?.nativeElement;

    if (!container)
      return;

    const currentMessages =
      this.messages();

    const signature = [
      currentMessages.length,
      currentMessages.at(-1)?.messageId ?? '',
      this.assistantTyping()
    ].join(':');

    if (signature === this.lastScrollSignature)
      return;

    this.lastScrollSignature = signature;

    container.scrollTop =
      container.scrollHeight;
  }

  onFileSelected(event: Event): void {

    const input =
      event.target as HTMLInputElement;

    this.selectedFileInput = input;

    const file =
      input.files?.[0] ?? null;

    this.selectedFile = file;

    this.selectedFileName.set(
      file?.name ?? ''
    );

    this.errorMessage.set('');
    this.uploadMessage.set('');
  }

  clearSelectedFile(): void {

    this.selectedFile = null;
    this.selectedFileName.set('');
    this.uploadMessage.set('');
    this.errorMessage.set('');

    if (this.selectedFileInput)
      this.selectedFileInput.value = '';
  }

  login(): void {
    this.auth.login();
  }

  logout(): void {
    this.auth.logout();
  }

  toggleSidebar(): void {
    this.sidebarCollapsed.update(collapsed => !collapsed);
  }

  createNewConversation(): void {

    if (this.creatingConversation())
      return;

    if (!this.auth.isAuthenticated()) {

      this.errorMessage.set(
        'Please log in first.'
      );

      return;
    }

    this.creatingConversation.set(true);

    this.api.createConversation({
      title: null
    }).subscribe({

      next: (session) => {

        this.activeSessionId.set(
          session.sessionId
        );

        this.messages.set([]);
        this.messageCache.set(session.sessionId, []);

        this.question.set('');

        this.upsertSession(session);

        this.creatingConversation.set(false);

        this.loadConversations();
      },

      error: () => {

        this.errorMessage.set(
          'Failed to create conversation.'
        );

        this.creatingConversation.set(false);
      }
    });
  }

  selectConversation(sessionId: string): void {

    if (!sessionId.trim())
      return;

    const currentActiveSessionId =
      this.activeSessionId();

    const sameSessionSelected =
      sessionId === currentActiveSessionId;

    const existingMessages =
      this.messages().length > 0;

    if (sameSessionSelected && existingMessages)
      return;

    this.activeSessionId.set(sessionId);
    this.errorMessage.set('');

    const cachedMessages =
      this.messageCache.get(sessionId);

    this.messages.set(cachedMessages ?? []);

    this.loadMessages(sessionId);
  }

  openDeleteConversationDialog(session: ConversationSession): void {

    this.deleteCandidate.set(session);
    this.errorMessage.set('');
  }

  cancelDeleteConversation(): void {

    if (this.deletingSessionId())
      return;

    this.deleteCandidate.set(null);
  }

  confirmDeleteConversation(): void {

    const session =
      this.deleteCandidate();

    if (!session || this.deletingSessionId())
      return;

    const wasActive =
      this.activeSessionId() === session.sessionId;

    this.deletingSessionId.set(session.sessionId);

    this.sessions.update(items =>
      items.filter(x => x.sessionId !== session.sessionId)
    );

    this.messageCache.delete(session.sessionId);

    if (wasActive) {
      this.askRequestId++;
      this.loadMessagesRequestId++;
      this.activeSessionId.set('');
      this.messages.set([]);
      this.question.set('');
      this.loadingAsk.set(false);
      this.loadingMessages.set(false);
      this.assistantTyping.set(false);
      this.pendingAskSessionId.set('');
    }

    this.api.deleteConversation(session.sessionId)
      .subscribe({
        next: () => {
          this.errorMessage.set('');
          this.deletingSessionId.set('');
          this.deleteCandidate.set(null);
          this.loadConversations();
        },
        error: () => {
          this.errorMessage.set(
            'Failed to delete conversation.'
          );

          this.deletingSessionId.set('');
          this.deleteCandidate.set(null);
          this.loadConversations();
        }
      });
  }

  upload(): void {

    if (!this.selectedFile)
      return;

    this.errorMessage.set('');
    this.documentLoadError.set('');
    this.loadingUpload.set(true);

    this.api.uploadDocument(
      this.selectedFile,
      this.allowedUploadRoles()
    ).subscribe({

      next: (response: UploadResponse) => {

        this.uploadedDocumentId.set(
          response.documentId
        );

        this.uploadMessage.set(
          response.isDuplicate
            ? `Document already exists. Status: ${response.status ?? 'UNKNOWN'}`
            : `Uploaded successfully. Processing started for ${response.fileName}.`
        );

        this.selectedFile = null;
        this.selectedFileName.set('');
        this.allowedUploadRoles.set([]);

        if (this.selectedFileInput)
          this.selectedFileInput.value = '';

        this.loadingUpload.set(false);

        if (this.auth.isAdmin()) {
          this.loadDocuments();
          this.pollDocumentUntilReady(response.documentId);
        }
      },

      error: () => {

        this.errorMessage.set(
          'Upload failed.'
        );

        this.loadingUpload.set(false);
      }
    });
  }

  toggleUploadRole(role: string): void {
    this.allowedUploadRoles.update(roles =>
      roles.includes(role)
        ? roles.filter(item => item !== role)
        : [...roles, role]);
  }

  toggleAllUploadRoles(): void {
    const allRoles = ['User', 'Manager', 'HR', 'Admin'];

    this.allowedUploadRoles.set(
      allRoles.every(role => this.allowedUploadRoles().includes(role))
        ? []
        : allRoles
    );
  }

  ask(): void {

    if (this.loadingAsk() || this.creatingConversation())
      return;

    if (!this.auth.isAuthenticated()) {
      this.errorMessage.set(
        'Please log in first.'
      );

      return;
    }

    const q =
      this.question().trim();

    if (!q)
      return;

    const sessionId =
      this.activeSessionId();

    if (!sessionId) {
      this.createConversationAndAsk(q);
      return;
    }

    this.askInSession(q, sessionId);
  }

  selectSuggestion(prompt: string): void {

    this.question.set(prompt);
    this.errorMessage.set('');
  }

  handleInteractiveOption(option: InteractiveOption): void {

    this.errorMessage.set('');

    if (option.action === 'document-selector') {
      this.addLocalAssistantMessage(
        'You can ask directly. Chat searches all shared indexed enterprise documents by default.',
        'text'
      );
      return;
    }

    if (option.action === 'focus-chat') {
      this.question.set('');
      return;
    }

    if (option.prompt) {
      this.question.set(option.prompt);
      this.ask();
    }
  }

  selectDocumentFromChat(document: DocumentMetadata): void {

    this.uploadedDocumentId.set('');
    this.errorMessage.set('');

    this.addLocalAssistantMessage(
      `Search will continue across all shared indexed documents, including **${document.fileName}**.`,
      'text'
    );
  }

  private createConversationAndAsk(question: string): void {

    if (!this.auth.isAuthenticated()) {
      this.errorMessage.set(
        'Please log in first.'
      );

      return;
    }

    this.creatingConversation.set(true);
    this.errorMessage.set('');

    this.api.createConversation({
      title: null
    }).subscribe({
      next: (session) => {
        this.activeSessionId.set(session.sessionId);
        this.messages.set([]);
        this.messageCache.set(session.sessionId, []);
        this.upsertSession(session);
        this.creatingConversation.set(false);
        this.askInSession(question, session.sessionId);
      },
      error: () => {
        this.errorMessage.set(
          'Failed to create conversation.'
        );

        this.creatingConversation.set(false);
      }
    });
  }

  private askInSession(
    q: string,
    sessionId: string
  ): void {

    this.errorMessage.set('');

    const requestId =
      ++this.askRequestId;

    const userMessage: ConversationMessage = {

      messageId: crypto.randomUUID(),

      sessionId,

      role: 'user',

      content: q,

      createdAtUtc:
        new Date().toISOString(),

      tokensApprox: 0,

      citations: []
    };

    this.messages.update(x => [
      ...x,
      userMessage
    ]);

    this.updateMessageCache(sessionId, this.messages());

    this.question.set('');

    this.loadingAsk.set(true);

    this.assistantTyping.set(true);

    this.pendingAskSessionId.set(sessionId);

    this.api.askQuestion({

      sessionId,

      documentId: null,

      searchAcrossAllDocuments: true,

      question: q,

      outputFormat: this.resolveOutputFormat(q)

    }).subscribe({

      next: (
        response: ChatAskResponse
      ) => {

        const assistantMessage:
          ConversationMessage = {

          messageId:
            crypto.randomUUID(),

          sessionId,

          role: 'assistant',

          content: response.answer,

          createdAtUtc:
            new Date().toISOString(),

          tokensApprox: 0,

          citations:
            response.citations ?? [],

          data: response.data,

          responseType:
            response.responseType,

          chartData:
            response.chartData
        };

        if (
          this.activeSessionId() === sessionId &&
          this.askRequestId === requestId
        ) {
          this.messages.update(x => [
            ...x,
            assistantMessage
          ]);
          this.updateMessageCache(sessionId, this.messages());
        }

        this.loadConversations();
        this.loadMessages(sessionId);

        if (this.askRequestId === requestId) {
          this.loadingAsk.set(false);
          this.assistantTyping.set(false);
          this.pendingAskSessionId.set('');
        }
      },

      error: (error: HttpErrorResponse) => {

        if (this.askRequestId === requestId) {
          this.errorMessage.set(
            this.getErrorMessage(error, 'Question failed. Please try again.')
          );

          this.loadingAsk.set(false);

          this.assistantTyping.set(false);
          this.pendingAskSessionId.set('');
        }
      }
    });
  }

  private showDocumentSelectorMessage(): void {

    if (!this.activeSessionId()) {
      this.createNewConversation();
      setTimeout(() => this.showDocumentSelectorMessage(), 0);
      return;
    }

    this.addLocalAssistantMessage(
      'Select a document to scope retrieval to that file.',
      'document-selector',
      {
        documents: this.documents(),
        selectedDocumentId: this.uploadedDocumentId() || null
      }
    );
  }

  private addLocalAssistantMessage(
    content: string,
    responseType: string,
    data: unknown = null
  ): void {

    const sessionId = this.activeSessionId();

    if (!sessionId)
      return;

    const assistantMessage: ConversationMessage = {
      messageId: crypto.randomUUID(),
      sessionId,
      role: 'assistant',
      content,
      createdAtUtc: new Date().toISOString(),
      tokensApprox: 0,
      citations: [],
      responseType,
      data
    };

    this.messages.update(x => [
      ...x,
      assistantMessage
    ]);

    this.updateMessageCache(sessionId, this.messages());
  }

  private resolveOutputFormat(question: string): string {

    const q = question.toLowerCase();

    if (q.includes('table') || q.includes('tabular'))
      return 'table';

    if (q.includes('chart') || q.includes('graph') || q.includes('statistics') || q.includes('analytics'))
      return 'chart';

    return 'text';
  }

  renderMessageContent(
    content: string
  ): SafeHtml {

    if (!content)
      return '';

    const markdown =
      this.normalizeMarkdownTables(content);

    // Parse markdown
    const html = marked.parse(markdown) as string;

    // Clean markdown artifacts: remove escaped asterisks, backslashes
    let cleaned = html
      .replace(/<br\s*\/?>/g, '') // Remove <br> tags
      .replace(/\*\*/g, '') // Remove escaped ** markdown
      .replace(/\\([*_`])/g, '$1') // Unescape markdown characters
      .replace(/&lt;br\s*\/?&gt;/g, '') // Remove encoded <br> tags
      .replace(/&amp;/g, '&') // Properly decode ampersands if needed
      .trim();

    // Sanitize HTML to prevent XSS while preserving safe formatting
    return this.sanitizer.sanitize(
      SecurityContext.HTML,
      cleaned
    ) ?? '';
  }

  private normalizeMarkdownTables(content: string): string {

    const lines = content.replace(/\r\n/g, '\n').split('\n');
    const normalizedLines: string[] = [];

    for (let i = 0; i < lines.length; i++) {
      if (!this.isLikelyTableRow(lines[i])) {
        normalizedLines.push(lines[i]);
        continue;
      }

      const tableBlock: string[] = [];

      while (i < lines.length && this.isLikelyTableRow(lines[i])) {
        tableBlock.push(lines[i]);
        i++;
      }

      i--;

      normalizedLines.push(tableBlock[0]);

      if (
        tableBlock.length >= 2 &&
        !this.isMarkdownTableSeparator(tableBlock[1])
      ) {
        const columnCount =
          this.countMarkdownTableColumns(tableBlock[0]);

        normalizedLines.push(
          Array.from({ length: columnCount }, () => '---').join(' | ')
        );
      }

      normalizedLines.push(...tableBlock.slice(1));
    }

    return normalizedLines.join('\n');
  }

  private isLikelyTableRow(line: string): boolean {

    const trimmed = line.trim();

    if (!trimmed || trimmed.startsWith('```'))
      return false;

    return this.countMarkdownTableColumns(trimmed) >= 2;
  }

  private isMarkdownTableSeparator(line: string): boolean {

    const cells = line
      .trim()
      .replace(/^\|/, '')
      .replace(/\|$/, '')
      .split('|')
      .map(cell => cell.trim());

    return cells.length >= 2 &&
      cells.every(cell => /^:?-{3,}:?$/.test(cell));
  }

  private countMarkdownTableColumns(line: string): number {

    return line
      .trim()
      .replace(/^\|/, '')
      .replace(/\|$/, '')
      .split('|')
      .map(cell => cell.trim())
      .filter(Boolean)
      .length;
  }

  trackSession(
    index: number,
    session: ConversationSession
  ): string {

    return session.sessionId;
  }

  trackMessage(
    index: number,
    message: ConversationMessage
  ): string {

    return message.messageId;
  }

  private loadConversations(): void {

    this.loadingSessions.set(true);

    this.api.getConversations()
      .subscribe({

      next: (sessions) => {

        const deletingSessionId =
          this.deletingSessionId();

        const deleteCandidateId =
          this.deleteCandidate()?.sessionId ?? '';

        const filteredSessions = sessions.filter(session =>
          session.sessionId !== deletingSessionId &&
          session.sessionId !== deleteCandidateId);

        this.sessions.set(filteredSessions);

        const activeSessionId = this.activeSessionId();

        if (filteredSessions.length === 0) {
          this.activeSessionId.set('');
          this.messages.set([]);
        }
        else if (!activeSessionId ||
                 !filteredSessions.some(x => x.sessionId === activeSessionId)) {
          const firstSessionId = filteredSessions[0].sessionId;
          this.activeSessionId.set(firstSessionId);
          this.messages.set(this.messageCache.get(firstSessionId) ?? []);
          this.loadMessages(firstSessionId);
        }
        else if (
          activeSessionId &&
          !this.messageCache.has(activeSessionId) &&
          this.messages().length === 0
        ) {
          this.loadMessages(activeSessionId);
        }

        this.loadingSessions.set(false);
      },

      error: () => {

        this.loadingSessions.set(false);
      }
    });
  }

  private loadDocuments(): void {

    if (!this.auth.isAuthenticated())
      return;

    if (!this.auth.isAdmin())
      return;

    this.loadingDocuments.set(true);
    this.documentLoadError.set('');

    this.api.getDocuments()
      .subscribe({
        next: (documents) => {
          this.documents.set(documents);
          this.loadingDocuments.set(false);
        },
        error: () => {
          this.documents.set([]);
          this.documentLoadError.set(
            'Documents could not be loaded right now.'
          );
          this.loadingDocuments.set(false);
        }
      });
  }

  private loadMessages(
    sessionId: string
  ): void {

    const requestId =
      ++this.loadMessagesRequestId;

    // Each conversation switch owns a request id so slower responses cannot replace the active chat.
    this.loadingMessages.set(true);

    this.api.getConversationMessages(
      sessionId
    ).subscribe({

      next: (messages) => {

        if (this.loadMessagesRequestId !== requestId)
          return;

        this.updateMessageCache(sessionId, messages);

        if (this.activeSessionId() === sessionId) {
          this.messages.set(messages);
          this.loadingMessages.set(false);
          return;
        }

        this.loadingMessages.set(false);
      },

      error: () => {

        if (this.loadMessagesRequestId === requestId) {
          this.loadingMessages.set(false);
          this.errorMessage.set(
            'Failed to load conversation messages.'
          );
        }
      }
    });
  }

  private updateMessageCache(
    sessionId: string,
    messages: ConversationMessage[]
  ): void {

    this.messageCache.set(sessionId, [...messages]);
  }

  private removeOptimisticMessage(
    sessionId: string,
    messageId: string
  ): void {

    const nextMessages = this.messages()
      .filter(message => message.messageId !== messageId);

    if (this.activeSessionId() === sessionId)
      this.messages.set(nextMessages);

    this.updateMessageCache(sessionId, nextMessages);
  }

  private getErrorMessage(
    error: HttpErrorResponse,
    fallback: string
  ): string {

    if (typeof error.error === 'string' && error.error.trim())
      return error.error.trim();

    if (error.error?.detail)
      return String(error.error.detail);

    if (error.error?.title)
      return String(error.error.title);

    return fallback;
  }

  private upsertSession(session: ConversationSession): void {

    this.sessions.update(items => [
      session,
      ...items.filter(item => item.sessionId !== session.sessionId)
    ]);
  }

  private pollDocumentUntilReady(
    documentId: string,
    attempt = 0
  ): void {

    this.clearDocumentPoll();

    if (!documentId || attempt >= 30)
      return;

    this.documentPollTimer = setTimeout(() => {
      this.api.getDocument(documentId)
        .subscribe({
          next: (document) => {
            this.upsertDocument(document);

            if (document.status === 'INDEXED') {
              this.uploadMessage.set(
                `${document.fileName} is indexed and ready to query.`
              );
              return;
            }

            if (document.status === 'FAILED') {
              this.uploadMessage.set(
                `${document.fileName} could not be indexed. Upload another file or check ingestion logs.`
              );
              return;
            }

            this.uploadMessage.set(
              `${document.fileName} is ${document.status.toLowerCase()}. You can ask once indexing completes.`
            );

            this.pollDocumentUntilReady(documentId, attempt + 1);
          },
          error: () => {
            this.pollDocumentUntilReady(documentId, attempt + 1);
          }
        });
    }, attempt < 3 ? 2500 : 5000);
  }

  private upsertDocument(document: DocumentMetadata): void {

    this.documents.update(items => [
      document,
      ...items.filter(item => item.documentId !== document.documentId)
    ]);
  }

  private clearDocumentPoll(): void {

    if (!this.documentPollTimer)
      return;

    clearTimeout(this.documentPollTimer);
    this.documentPollTimer = null;
  }

}
