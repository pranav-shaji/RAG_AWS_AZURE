import { ComponentFixture, TestBed } from '@angular/core/testing';

import { MessageItemComponent } from './message-item';

describe('MessageItemComponent', () => {
  let component: MessageItemComponent;
  let fixture: ComponentFixture<MessageItemComponent>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [MessageItemComponent],
    }).compileComponents();

    fixture = TestBed.createComponent(MessageItemComponent);
    fixture.componentRef.setInput('message', {
      messageId: 'm1',
      sessionId: 's1',
      role: 'assistant',
      content: 'Hello',
      createdAtUtc: new Date().toISOString(),
      tokensApprox: 0,
      citations: []
    });
    fixture.componentRef.setInput('renderedContent', 'Hello');
    component = fixture.componentInstance;
    await fixture.whenStable();
  });

  it('should create', () => {
    expect(component).toBeTruthy();
  });
});
