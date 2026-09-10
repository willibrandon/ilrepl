// Enhance images linked to their full-size source; the link still works without JavaScript.
function initLightbox() {
  let dialog = document.getElementById('image-lightbox');
  if (!dialog) {
    dialog = document.createElement('dialog');
    dialog.id = 'image-lightbox';
    dialog.setAttribute('aria-label', 'Expanded image');

    const image = document.createElement('img');
    const close = document.createElement('button');
    close.type = 'button';
    close.textContent = '×';
    close.setAttribute('aria-label', 'Close expanded image');
    dialog.append(close, image);
    document.body.append(dialog);

    dialog.addEventListener('click', () => dialog.close());
    dialog.addEventListener('close', () => {
      image.removeAttribute('src');
      document.documentElement.classList.remove('image-lightbox-open');
      const opener = document.querySelector('[data-lightbox-active]');
      opener?.removeAttribute('data-lightbox-active');
      opener?.focus({ preventScroll: true });
    });
  }

  document.querySelectorAll('.sl-markdown-content a:not([data-lightbox])').forEach((link) => {
    const image = link.firstElementChild;
    if (link.children.length !== 1 || image?.tagName !== 'IMG' || link.href !== image.src) return;

    link.setAttribute('data-lightbox', '');
    link.setAttribute('role', 'button');
    link.setAttribute('aria-haspopup', 'dialog');
    link.setAttribute('aria-controls', dialog.id);
    link.setAttribute('aria-label', `Enlarge image: ${image.alt}`);
    link.addEventListener('click', (event) => {
      if (event.button !== 0 || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) return;
      event.preventDefault();
      const expanded = dialog.querySelector('img');
      expanded.src = link.href;
      expanded.alt = image.alt;
      link.setAttribute('data-lightbox-active', '');
      dialog.showModal();
      document.documentElement.classList.add('image-lightbox-open');
    });
    link.addEventListener('keydown', (event) => {
      if (event.key === ' ') {
        event.preventDefault();
        link.click();
      }
    });
  });
}

initLightbox();
document.addEventListener('astro:page-load', initLightbox);
