import {
  Component,
  Input,
  Output,
  EventEmitter
} from '@angular/core';

import { CommonModule } from '@angular/common';
import { ConversationSession } from '../../api';

@Component({
  selector: 'app-sidebar',  
  standalone: true,
  imports: [CommonModule],
  templateUrl: './sidebar.html',
  styleUrls: ['./sidebar.css']
})
export class SidebarComponent {

  @Input() sessions: ConversationSession[] = [];

  @Input() activeSessionId: string = '';

  @Input() loadingSessions = false;

  @Input() creatingConversation = false;

  @Output() createConversation =
    new EventEmitter<void>();

  @Output() conversationSelected =
    new EventEmitter<string>();

  @Output() conversationDeleteRequested =
    new EventEmitter<ConversationSession>();

  onCreateConversation(): void {

    this.createConversation.emit();
  }

  onSelectConversation(
    sessionId: string
  ): void {

    this.conversationSelected.emit(sessionId);
  }

  onDeleteConversation(
    event: Event,
    session: ConversationSession
  ): void {

    event.preventDefault();
    event.stopPropagation();
    this.conversationDeleteRequested.emit(session);
  }

  trackSession(
    index: number,
    session: ConversationSession
  ): string {

    return session.sessionId;
  }
}
