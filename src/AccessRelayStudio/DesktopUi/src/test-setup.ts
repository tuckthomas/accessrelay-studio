// jsdom does not implement the native dialog top layer. Unit tests exercise React
// state; Playwright tests exercise real inertness, focus containment and restoration.
if (!HTMLDialogElement.prototype.showModal) {
  HTMLDialogElement.prototype.showModal = function () { this.open = true; };
  HTMLDialogElement.prototype.close = function () { this.open = false; };
}
