import {
  Component,
  EventEmitter,
  Input,
  Output
} from '@angular/core';

import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';

@Component({
  selector: 'app-chat-input',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule
  ],
  templateUrl: './chat-input.html',
  styleUrl: './chat-input.css'
})
export class ChatInputComponent {

  @Input()
  question = '';

  @Input()
  loadingAsk = false;

  @Input()
  authenticated = false;

  @Input()
  canSubmit = false;

  @Input()
  suggestionPrompts: string[] = [];

  @Input()
  helperHint = 'Select a document or enable global search to ask from indexed files.';

  @Output()
  questionChange = new EventEmitter<string>();

  @Output()
  askClicked = new EventEmitter<void>();

  @Output()
  suggestionSelected = new EventEmitter<string>();

  onEnter(event: Event): void {
    const keyboardEvent = event as KeyboardEvent;

    if (
      keyboardEvent.shiftKey ||
      this.loadingAsk ||
      !this.authenticated ||
      !this.canSubmit
    ) {
      return;
    }

    keyboardEvent.preventDefault();
    this.askClicked.emit();
  }

  onSuggestionClick(prompt: string): void {
    this.suggestionSelected.emit(prompt);
  }
}
