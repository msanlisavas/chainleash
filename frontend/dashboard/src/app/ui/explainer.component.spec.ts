import { TestBed } from '@angular/core/testing';
import { ExplainerComponent } from './explainer.component';

// Pure view logic of the privacy-facade video. The point of the facade is that
// YouTube is NOT contacted until play() — so embedUrl stays null on load.
describe('ExplainerComponent', () => {
  let c: ExplainerComponent;

  beforeEach(() => {
    TestBed.configureTestingModule({ imports: [ExplainerComponent] });
    c = TestBed.createComponent(ExplainerComponent).componentInstance;
  });

  it('does not contact YouTube until play() is pressed', () => {
    expect(c.embedUrl()).toBeNull();
    c.play();
    expect(c.embedUrl()).not.toBeNull(); // a SafeResourceUrl is now set
  });

  it('uses the cookie-less nocookie host with autoplay once playing', () => {
    c.play();
    // SafeResourceUrl stringifies to its changingThisBreaksApplicationSecurity value.
    expect(String(c.embedUrl())).toContain('youtube-nocookie.com/embed/6_j26_2XpYA');
    expect(String(c.embedUrl())).toContain('autoplay=1');
  });
});
