import { ComponentFixture, TestBed } from '@angular/core/testing';

import { UploadPanelComponent } from './upload-panel';

describe('UploadPanelComponent', () => {
  let component: UploadPanelComponent;
  let fixture: ComponentFixture<UploadPanelComponent>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [UploadPanelComponent],
    }).compileComponents();

    fixture = TestBed.createComponent(UploadPanelComponent);
    component = fixture.componentInstance;
    await fixture.whenStable();
  });

  it('should create', () => {
    expect(component).toBeTruthy();
  });
});
