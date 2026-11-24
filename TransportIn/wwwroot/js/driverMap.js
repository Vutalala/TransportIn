// wwwroot/js/driverMap.js
// Карта маршруту для кабінету водія: пункти замовлення, дорожній маршрут, відстань і час.
// Leaflet підключений глобально в App.razor.

let map = null;
let layer = null;
let lastKey = '';
let lastRoute = null;
let lastGeoAt = 0;
const geoCache = {};

function ensureLeaflet() {
    return new Promise((resolve, reject) => {
        if (window.L) return resolve();
        let tries = 0;
        const t = setInterval(() => {
            if (window.L) { clearInterval(t); resolve(); }
            else if (++tries > 50) { clearInterval(t); reject(new Error('Leaflet не завантажився (перевірте інтернет)')); }
        }, 100);
    });
}

function injectStyles() {
    if (document.getElementById('drv-map-styles')) return;
    const st = document.createElement('style');
    st.id = 'drv-map-styles';
    st.textContent = `
        .drv-map .leaflet-container, .drv-map.leaflet-container { background:#0f172a; font-family:inherit }
        .drv-map .leaflet-tile-pane { filter:invert(1) hue-rotate(180deg) brightness(.9) contrast(.85) saturate(.7) }
        .drv-map .leaflet-control-attribution { background:rgba(15,23,42,.7) !important; color:#94a3b8 !important }
        .drv-map .leaflet-control-attribution a { color:#60a5fa !important }
        .drv-pin { width:28px; height:28px; border-radius:7px; border:2px solid #fff; display:flex; align-items:center; justify-content:center; font-size:14px; box-shadow:0 2px 6px rgba(0,0,0,.5) }
        .drv-pin.current { transform:scale(1.25); animation:drv-pulse 1.4s infinite }
        @keyframes drv-pulse { 0%{box-shadow:0 0 0 0 rgba(96,165,250,.8)} 70%{box-shadow:0 0 0 12px rgba(96,165,250,0)} 100%{box-shadow:0 0 0 0 rgba(96,165,250,0)} }
    `;
    document.head.appendChild(st);
}

function esc(s) {
    return String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
}

export async function init(el) {
    await ensureLeaflet();
    injectStyles();

    if (map) { try { map.remove(); } catch { } map = null; }
    lastKey = ''; lastRoute = null;

    el.classList.add('drv-map');
    map = L.map(el, { zoomControl: true }).setView([50.4501, 30.5234], 11);
    L.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png', {
        maxZoom: 19,
        attribution: '&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a>'
    }).addTo(map);
    layer = L.layerGroup().addTo(map);

    setTimeout(() => map && map.invalidateSize(), 200);
    return true;
}

// Геокодування адреси в межах Києва (Nominatim, не частіше ніж раз на ~1,1 с). Повертає [lat, lng] або null
async function geocode(address) {
    const q = /київ/i.test(address) ? address : `${address}, Київ`;
    if (q in geoCache) return geoCache[q];
    try {
        const wait = 1100 - (Date.now() - lastGeoAt);
        if (wait > 0) await new Promise(r => setTimeout(r, wait));
        lastGeoAt = Date.now();

        const ctrl = new AbortController();
        const t = setTimeout(() => ctrl.abort(), 8000);
        const r = await fetch(
            `https://nominatim.openstreetmap.org/search?format=jsonv2&limit=1&accept-language=uk&viewbox=30.2,50.6,30.85,50.2&bounded=1&q=${encodeURIComponent(q)}`,
            { signal: ctrl.signal });
        clearTimeout(t);
        if (!r.ok) return null;
        const j = await r.json();
        const res = j && j.length ? [parseFloat(j[0].lat), parseFloat(j[0].lon)] : null;
        geoCache[q] = res;
        return res;
    } catch {
        return null;
    }
}

function haversineKm(a, b) {
    const R = 6371, rad = Math.PI / 180;
    const dLat = (b[0] - a[0]) * rad, dLng = (b[1] - a[1]) * rad;
    const h = Math.sin(dLat / 2) ** 2 + Math.cos(a[0] * rad) * Math.cos(b[0] * rad) * Math.sin(dLng / 2) ** 2;
    return 2 * R * Math.asin(Math.sqrt(h));
}

// Дорожній маршрут через публічний OSRM; якщо недоступний — орієнтовно за прямими лініями
async function buildRoute(points) {
    try {
        const coords = points.map(p => `${p.lng},${p.lat}`).join(';');
        const ctrl = new AbortController();
        const t = setTimeout(() => ctrl.abort(), 8000);
        const r = await fetch(
            `https://router.project-osrm.org/route/v1/driving/${coords}?overview=full&geometries=geojson`,
            { signal: ctrl.signal });
        clearTimeout(t);
        if (r.ok) {
            const j = await r.json();
            if (j.code === 'Ok' && j.routes && j.routes.length) {
                const rt = j.routes[0];
                return {
                    line: rt.geometry.coordinates.map(c => [c[1], c[0]]),
                    km: rt.distance / 1000,
                    min: rt.duration / 60,
                    approx: false
                };
            }
        }
    } catch { }

    let km = 0;
    for (let i = 1; i < points.length; i++) {
        km += haversineKm([points[i - 1].lat, points[i - 1].lng], [points[i].lat, points[i].lng]);
    }
    km *= 1.3; // поправка на дорожню мережу
    return { line: points.map(p => [p.lat, p.lng]), km, min: km / 40 * 60, approx: true };
}

// Малює пункти й маршрут. Повертає координати, знайдені через геокодування (щоб C# зберіг їх у БД), відстань і час
export async function showRoute(points, currentIndex) {
    if (!map) return null;

    const geocoded = [];
    const missing = [];

    for (const p of points) {
        if (p.lat == null || p.lng == null) {
            const g = await geocode(p.address);
            if (g) { p.lat = g[0]; p.lng = g[1]; geocoded.push({ id: p.id, lat: g[0], lng: g[1] }); }
            else missing.push(p.address);
        }
    }

    const ready = points.filter(p => p.lat != null && p.lng != null);
    const key = ready.map(p => `${p.lat},${p.lng}`).join(';');
    const keyChanged = key !== lastKey;

    if (keyChanged) {
        lastRoute = ready.length >= 2 ? await buildRoute(ready) : null;
        lastKey = key;
    }

    layer.clearLayers();

    if (lastRoute) {
        L.polyline(lastRoute.line, {
            color: '#3b82f6', weight: 5, opacity: lastRoute.approx ? 0.5 : 0.85,
            dashArray: lastRoute.approx ? '8 8' : null
        }).addTo(layer);
    }

    const done = ['Завантажено', 'Розвантажено', 'Виїхав'];
    ready.forEach((p) => {
        const idx = points.indexOf(p);
        const color = p.status === 'Нова' ? '#64748b' : done.includes(p.status) ? '#10b981' : '#f59e0b';
        const current = idx === currentIndex;
        const icon = L.divIcon({
            className: '',
            iconSize: [28, 28],
            iconAnchor: [14, 14],
            html: `<div class="drv-pin${current ? ' current' : ''}" style="background:${color}">${p.isLoading ? '📦' : '🏁'}</div>`
        });
        L.marker([p.lat, p.lng], { icon, zIndexOffset: current ? 500 : 0 })
            .bindTooltip(
                `Пункт ${idx + 1}: ${p.isLoading ? 'завантаження' : 'розвантаження'}<br>${esc(p.address)}<br>Статус: ${esc(p.status)}`,
                { direction: 'top', offset: [0, -12] })
            .addTo(layer);
    });

    if (keyChanged && ready.length) {
        const bounds = L.latLngBounds(ready.map(p => [p.lat, p.lng]));
        if (lastRoute) lastRoute.line.forEach(c => bounds.extend(c));
        map.fitBounds(bounds, { padding: [30, 30], maxZoom: 15 });
    }

    return {
        geocoded,
        missing,
        km: lastRoute ? lastRoute.km : 0,
        min: lastRoute ? lastRoute.min : 0,
        approx: lastRoute ? lastRoute.approx : false,
        hasRoute: !!lastRoute
    };
}

export function dispose() {
    try { if (map) map.remove(); } catch { }
    map = null; layer = null; lastKey = ''; lastRoute = null;
}