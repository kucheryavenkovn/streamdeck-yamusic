export const escape = value => String(value ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&apos;' })[c]);
export const dataUri = source => `data:image/svg+xml;base64,${Buffer.from(source).toString('base64')}`;
