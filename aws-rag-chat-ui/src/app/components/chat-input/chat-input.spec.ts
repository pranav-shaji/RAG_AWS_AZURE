import { ComponentFixture, TestBed } from '@angular/core/testing';

import { ChatInputComponent } from './chat-input';

describe('ChatInputComponent', () => {
  let component: ChatInputComponent;
  let fixture: ComponentFixture<ChatInputComponent>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [ChatInputComponent],
    }).compileComponents();

    fixture = TestBed.createComponent(ChatInputComponent);
    component = fixture.componentInstance;
    await fixture.whenStable();
  });

  it('should create', () => {
    expect(component).toBeTruthy();
  });
});
