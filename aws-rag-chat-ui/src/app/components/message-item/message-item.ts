import {
  Component,
  EventEmitter,
  Input,
  Output,
  signal
} from '@angular/core';

import { CommonModule } from '@angular/common';

import { SafeHtml } from '@angular/platform-browser';
import {
  ConversationMessage,
  DocumentMetadata,
  DocumentImage,
  TableData,
  InteractiveOption
} from '../../api';

import { PieChartMessageComponent }
from '../pie-chart-message/pie-chart-message';

@Component({
  selector: 'app-message-item',
  standalone: true,
  imports: [
    CommonModule,
    PieChartMessageComponent
  ],
  templateUrl: './message-item.html',
  styleUrl: './message-item.css'
})
export class MessageItemComponent {

  @Input({ required: true }) message!: ConversationMessage;

  @Input() renderedContent!: SafeHtml;

  @Output() optionSelected = new EventEmitter<InteractiveOption>();

  @Output() documentSelected = new EventEmitter<DocumentMetadata>();

  copied = signal(false);

  get documentData(): DocumentMetadata[] {
    const data = this.message.data as { documents?: DocumentMetadata[] } | DocumentMetadata[] | null | undefined;

    if (Array.isArray(data))
      return data as DocumentMetadata[];

    return data?.documents ?? [];
  }

  get imageData(): DocumentImage[] {
    return Array.isArray(this.message.data)
      ? this.message.data as DocumentImage[]
      : [];
  }

  isPdfPreview(image: DocumentImage): boolean {
    return image.sourceType === 'pdf-preview' ||
      image.fileName.toLowerCase().endsWith('.pdf');
  }

  get tableData(): TableData | null {
    const data = this.message.data as TableData | DocumentMetadata[] | null | undefined;

    if (data && !Array.isArray(data) && Array.isArray(data.columns) && Array.isArray(data.rows))
      return data;

    if (!Array.isArray(data))
      return null;

    return {
      columns: ['File Name', 'Status', 'Pages', 'Chunks', 'Document ID'],
      rows: data.map(doc => [
        doc.fileName,
        doc.status,
        doc.pageCount > 0 ? String(doc.pageCount) : 'Not available',
        String(doc.chunkCount ?? 0),
        doc.documentId
      ])
    };
  }

  get options(): InteractiveOption[] {
    const data = this.message.data as { options?: InteractiveOption[] } | null | undefined;
    return data?.options ?? [];
  }

  get selectorDocuments(): DocumentMetadata[] {
    const data = this.message.data as { documents?: DocumentMetadata[] } | null | undefined;
    return data?.documents ?? [];
  }

  isSelectedDocument(documentId: string): boolean {
    const data = this.message.data as { selectedDocumentId?: string | null } | null | undefined;
    return data?.selectedDocumentId === documentId;
  }

  async copyAssistantAnswer(): Promise<void> {
    if (this.message.role !== 'assistant' || !this.message.content)
      return;

    try {
      await navigator.clipboard.writeText(this.message.content);
      this.copied.set(true);
      setTimeout(() => this.copied.set(false), 1400);
    } catch {
      this.copyWithFallback(this.message.content);
    }
  }

  private copyWithFallback(text: string): void {
    const textArea = document.createElement('textarea');
    textArea.value = text;
    textArea.setAttribute('readonly', '');
    textArea.style.position = 'fixed';
    textArea.style.opacity = '0';
    document.body.appendChild(textArea);
    textArea.select();
    document.execCommand('copy');
    document.body.removeChild(textArea);
    this.copied.set(true);
    setTimeout(() => this.copied.set(false), 1400);
  }
}
