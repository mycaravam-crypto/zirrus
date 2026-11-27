import "@testing-library/jest-dom/vitest";

// jsdom lacks the browser APIs Mantine uses for media queries, scroll areas and color schemes.
Object.defineProperty(window, "matchMedia", {
  writable: true,
  value: (query: string) => ({
    matches: false,
    media: query,
    onchange: null,
    addListener: () => {},
    removeListener: () => {},
    addEventListener: () => {},
    removeEventListener: () => {},
    dispatchEvent: () => false,
  }),
});

class ResizeObserverStub {
  observe() {}
  unobserve() {}
  disconnect() {}
}
window.ResizeObserver ??= ResizeObserverStub as unknown as typeof ResizeObserver;
window.HTMLElement.prototype.scrollIntoView ??= () => {};

// Mantine's autosizing Textarea listens for font loading, which jsdom does not implement.
if (!("fonts" in document)) {
  Object.defineProperty(document, "fonts", {
    value: { addEventListener: () => {}, removeEventListener: () => {}, ready: Promise.resolve() },
  });
}
