import {
  Component,
  EventEmitter,
  Input,
  Output
} from '@angular/core';

import { CommonModule } from '@angular/common';

@Component({
  selector: 'app-upload-panel',
  standalone: true,
  imports: [CommonModule],
  templateUrl: './upload-panel.html',
  styleUrl: './upload-panel.css'
})
export class UploadPanelComponent {

  @Input()
  selectedFileName = '';

  @Input()
  loadingUpload = false;

  @Input()
  uploadMessage = '';

  @Input()
  authenticated = false;

  @Input()
  allowedRoles: string[] = [];

  @Output()
  roleToggled = new EventEmitter<string>();

  @Output()
  selectAllRolesToggled = new EventEmitter<void>();

  @Output()
  fileSelected = new EventEmitter<Event>();

  @Output()
  fileCleared = new EventEmitter<void>();

  @Output()
  uploadClicked = new EventEmitter<void>();

  readonly roleOptions = ['User', 'Manager', 'HR', 'Admin'];

  isRoleSelected(role: string): boolean {
    return this.allowedRoles.includes(role);
  }

  allRolesSelected(): boolean {
    return this.roleOptions.every(role => this.allowedRoles.includes(role));
  }

  clearSelectedFile(input: HTMLInputElement): void {
    input.value = '';
    this.fileCleared.emit();
  }
}
