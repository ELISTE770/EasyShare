// EasyShare Service Worker v1.0
const CACHE_NAME = 'easyshare-cache-v1';
const APP_SHELL = [
  '/',
  '/index.html',
  '/manifest.json',
  '/favicon.ico',
  '/icon-192.png'
];

self.addEventListener('install', (event) => {
  event.waitUntil(
    caches.open(CACHE_NAME).then((cache) => cache.addAll(APP_SHELL)).catch(() => {})
  );
  self.skipWaiting();
});

self.addEventListener('activate', (event) => {
  event.waitUntil(
    caches.keys().then((keys) =>
      Promise.all(keys.map((k) => (k !== CACHE_NAME ? caches.delete(k) : null)))
    )
  );
  self.clients.claim();
});

self.addEventListener('fetch', (event) => {
  const url = new URL(event.request.url);

  // אין לשמור במטמון קריאות API, העלאות, והורדות
  if (url.pathname.startsWith('/api/') || url.pathname.startsWith('/secure/')) {
    return;
  }

  // אסטרטגיית Stale-While-Revalidate עבור משאבי האפליקציה (App Shell)
  event.respondWith(
    caches.match(event.request).then((cachedResponse) => {
      const fetchPromise = fetch(event.request)
        .then((networkResponse) => {
          if (networkResponse && networkResponse.status === 200) {
            const clone = networkResponse.clone();
            caches.open(CACHE_NAME).then((cache) => cache.put(event.request, clone));
          }
          return networkResponse;
        })
        .catch(() => {
          if (cachedResponse) return cachedResponse;
          // דף אופליין במקרה שהמחשב כבוי או מנותק
          return new Response(
            `<!DOCTYPE html><html dir="rtl" lang="he"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><title>מנותק מהמחשב</title><style>body{background:#0b1120;color:#f8fafc;font-family:system-ui;display:flex;align-items:center;justify-content:center;height:100vh;margin:0;text-align:center;padding:20px;}.card{background:#1e293b;padding:32px;border-radius:16px;border:1px solid #334155;max-width:400px;}</style></head><body><div class="card"><div style="font-size:3rem;margin-bottom:1rem;">📡</div><h2>אין תקשורת עם המחשב</h2><p style="color:#94a3b8;font-size:0.9rem;">ודא שתוכנת 'שיתוף קל' פועלת במחשב ושהמכשיר מחובר לאותה רשת Wi-Fi.</p><button onclick="location.reload()" style="background:#2563eb;color:white;border:none;padding:10px 20px;border-radius:8px;font-weight:bold;cursor:pointer;margin-top:10px;">נסה שוב 🔄</button></div></body></html>`,
            { headers: { 'Content-Type': 'text/html; charset=utf-8' } }
          );
        });

      return cachedResponse || fetchPromise;
    })
  );
});
