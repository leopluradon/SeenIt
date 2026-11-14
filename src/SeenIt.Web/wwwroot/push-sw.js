/*
 * SeenIt's service worker: push notifications only. It has no fetch handler and caches nothing, so it can never
 * serve an old version of the site. Payloads are { title, body, url, tag } from the server (PushPayload).
 */
self.addEventListener('install', () => self.skipWaiting());
self.addEventListener('activate', e => e.waitUntil(self.clients.claim()));

self.addEventListener('push', e => {
    let d = {};
    try { d = e.data ? e.data.json() : {}; } catch { d = { body: e.data ? e.data.text() : '' }; }
    e.waitUntil(self.registration.showNotification(d.title || 'SeenIt', {
        body: d.body || '',
        icon: 'icon-192.png',
        badge: 'icon-192.png',
        tag: d.tag || undefined,
        data: { url: d.url || '' },
    }));
});

// Tapping it: bring an open SeenIt window forward and go to the page (in-app, no reload), or open one.
self.addEventListener('notificationclick', e => {
    e.notification.close();
    const url = new URL((e.notification.data && e.notification.data.url) || '', self.registration.scope).href;
    e.waitUntil((async () => {
        const open = await self.clients.matchAll({ type: 'window', includeUncontrolled: true });
        for (const c of open) {
            if (!c.url.startsWith(self.registration.scope)) continue;
            try { await c.focus(); } catch { }
            c.postMessage({ type: 'seenit-navigate', url });
            return;
        }
        if (self.clients.openWindow) await self.clients.openWindow(url);
    })());
});
