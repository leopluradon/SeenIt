// Small browser helpers for the shared SeenIt UI.
window.seenit = {
    /** The text actually in a field: password managers can fill a box without telling Blazor. */
    fieldValue(id) { const el = document.getElementById(id); return el ? el.value : null; },
    // Customizing a profile: drag a widget's grip to move it, drag its corner to resize it (snaps to the grid).
    profileGrid: {
        attach(grid, dotnet) {
            if (!grid || grid._pwAttached) return;
            grid._pwAttached = true;
            const cells = () => {
                const cs = getComputedStyle(grid);
                const cols = cs.gridTemplateColumns.split(' ').filter(Boolean).length;
                const gap = parseFloat(cs.columnGap) || 0, rowGap = parseFloat(cs.rowGap) || 0;
                return { cols, gap, rowGap, colW: (grid.clientWidth - gap * (cols - 1)) / cols, rowH: parseFloat(cs.gridAutoRows) || 168 };
            };
            const edgeScroll = y => { if (y < 70) window.scrollBy(0, -14); else if (y > innerHeight - 70) window.scrollBy(0, 14); };

            grid.addEventListener('pointerdown', e => {
                const grip = e.target.closest('.pw-grip'), corner = e.target.closest('.pw-resize');
                if ((!grip && !corner) || e.button !== 0) return;
                const box = e.target.closest('.pw');
                if (!box) return;
                e.preventDefault();
                (grip || corner).setPointerCapture(e.pointerId);
                const handle = grip || corner, id = box.dataset.id;

                if (grip) {
                    const x0 = e.clientX, y0 = e.clientY + scrollY;
                    let target = null, before = false;
                    box.classList.add('pw-dragging');
                    const clear = () => grid.querySelectorAll('.pw-drop-before, .pw-drop-after').forEach(el => el.classList.remove('pw-drop-before', 'pw-drop-after'));
                    const move = ev => {
                        box.style.transform = `translate(${ev.clientX - x0}px, ${ev.clientY + scrollY - y0}px)`;
                        box.style.pointerEvents = 'none';
                        const under = document.elementFromPoint(ev.clientX, ev.clientY)?.closest('.pw');
                        box.style.pointerEvents = '';
                        clear();
                        target = under && under !== box && grid.contains(under) ? under : null;
                        if (target) {
                            const r = target.getBoundingClientRect();
                            // Wide widgets: top half goes before; others: left half goes before.
                            before = r.width > grid.clientWidth * .9 ? ev.clientY < r.top + r.height / 2 : ev.clientX < r.left + r.width / 2;
                            target.classList.add(before ? 'pw-drop-before' : 'pw-drop-after');
                        }
                        edgeScroll(ev.clientY);
                    };
                    const up = () => {
                        handle.removeEventListener('pointermove', move);
                        handle.removeEventListener('pointerup', up);
                        handle.removeEventListener('pointercancel', up);
                        box.classList.remove('pw-dragging');
                        box.style.transform = '';
                        clear();
                        if (target) dotnet.invokeMethodAsync('Move', id, target.dataset.id, before);
                    };
                    handle.addEventListener('pointermove', move);
                    handle.addEventListener('pointerup', up);
                    handle.addEventListener('pointercancel', up);
                } else {
                    const c = cells(), r = box.getBoundingClientRect();
                    const w0 = +(box.className.match(/\bw(\d+)\b/)?.[1] || 12), h0 = +getComputedStyle(box).getPropertyValue('--h') || 1;
                    const widths = [3, 4, 6, 8, 9, 12], names = { 3: '¼', 4: '⅓', 6: '½', 8: '⅔', 9: '¾', 12: 'Full' };
                    let w = w0, h = h0;
                    box.classList.add('pw-resizing');
                    const move = ev => {
                        // Width: the share of the row the pointer has reached, snapped to ¼ ⅓ ½ ⅔ ¾ or full.
                        // (Phones show one column, so only the height changes there.)
                        if (c.cols >= 12) {
                            const share = (ev.clientX - r.left + c.gap) / (grid.clientWidth + c.gap) * 12;
                            w = widths.reduce((best, x) => Math.abs(x - share) < Math.abs(best - share) ? x : best);
                        }
                        h = Math.max(1, Math.min(4, Math.round((ev.clientY - r.top + c.rowGap) / (c.rowH + c.rowGap))));
                        if (c.cols >= 12) box.style.gridColumn = `span ${w}`;
                        box.style.gridRow = `span ${h}`;
                        box.dataset.size = `${names[w]} × ${h}`;
                        edgeScroll(ev.clientY);
                    };
                    const up = () => {
                        handle.removeEventListener('pointermove', move);
                        handle.removeEventListener('pointerup', up);
                        handle.removeEventListener('pointercancel', up);
                        box.classList.remove('pw-resizing');
                        box.style.gridColumn = box.style.gridRow = '';
                        delete box.dataset.size;
                        if (w !== w0 || h !== h0) dotnet.invokeMethodAsync('Resize', id, w, h);
                    };
                    handle.addEventListener('pointermove', move);
                    handle.addEventListener('pointerup', up);
                    handle.addEventListener('pointercancel', up);
                }
            });
        },
        reveal(grid, id) {
            const el = grid?.querySelector(`.pw[data-id="${id}"]`);
            el?.scrollIntoView({ behavior: 'smooth', block: 'center' });
        },
    },
    // Escape closes the dialog on top. Each open dialog registers here; the newest one wins.
    _dialogs: [],
    dialogOpened(id, dotnet) {
        this._dialogs = this._dialogs.filter(d => d.id !== id);
        this._dialogs.push({ id, dotnet });
        if (!this._escBound) {
            this._escBound = true;
            document.addEventListener('keydown', e => {
                if (e.key !== 'Escape' || e.defaultPrevented || !this._dialogs.length) return;
                e.preventDefault();
                this._dialogs[this._dialogs.length - 1].dotnet.invokeMethodAsync('Escape');
            });
        }
    },
    dialogClosed(id) { this._dialogs = this._dialogs.filter(d => d.id !== id); },

    /**
     * Right-click (or long-press on touch) anything with a data-ctx attribute to open SeenIt's menu for it.
     * Shift+right-click still gives the browser's own menu. The menu key / Shift+F10 opens it for the focused element.
     */
    contextMenu(dotnet) {
        if (window.seenitCtx) { window.seenitCtx.dotnet = dotnet; return; }
        const st = window.seenitCtx = { dotnet, timer: null, x: 0, y: 0, opened: 0 };
        const find = t => t && t.closest ? t.closest('[data-ctx]') : null;
        const open = (el, x, y, touch) =>
            st.dotnet.invokeMethodAsync('Open', el.getAttribute('data-ctx'), x, y, window.innerWidth, window.innerHeight, touch);
        const cancel = () => { if (st.timer) { clearTimeout(st.timer); st.timer = null; } };

        document.addEventListener('contextmenu', e => {
            if (e.shiftKey) return;
            const el = find(e.target);
            if (!el) return;
            e.preventDefault();
            if (Date.now() - st.opened < 1500) return;   // the long press already opened it
            open(el, e.clientX, e.clientY, false);
        });
        document.addEventListener('touchstart', e => {
            cancel();
            const el = find(e.target);
            if (!el || e.touches.length !== 1) return;
            st.x = e.touches[0].clientX; st.y = e.touches[0].clientY;
            st.timer = setTimeout(() => {
                st.timer = null;
                st.opened = Date.now();
                try { navigator.vibrate && navigator.vibrate(12); } catch { }
                open(el, st.x, st.y, true);
            }, 450);
        }, { passive: true });
        document.addEventListener('touchmove', e => {
            if (st.timer && Math.hypot(e.touches[0].clientX - st.x, e.touches[0].clientY - st.y) > 10) cancel();
        }, { passive: true });
        document.addEventListener('touchend', e => {
            if (st.timer) cancel();
            else if (Date.now() - st.opened < 1500 && e.cancelable) e.preventDefault();   // no click after a long press
        }, { passive: false });
        document.addEventListener('touchcancel', cancel, { passive: true });
        document.addEventListener('scroll', cancel, { passive: true, capture: true });

        // Drag the bottom sheet down to close it, from its handle or anywhere once its list is scrolled to the top.
        const drag = { sheet: null, y0: 0, t0: 0, dy: 0, on: false };
        document.addEventListener('touchstart', e => {
            const sheet = e.target.closest && e.target.closest('.ctx-sheet');
            drag.sheet = sheet && e.touches.length === 1 && sheet.scrollTop <= 0 ? sheet : null;
            drag.on = false; drag.dy = 0;
            if (drag.sheet) { drag.y0 = e.touches[0].clientY; drag.t0 = Date.now(); }
        }, { passive: true });
        document.addEventListener('touchmove', e => {
            if (!drag.sheet) return;
            const dy = e.touches[0].clientY - drag.y0;
            if (!drag.on) {
                if (Math.abs(dy) < 8) return;
                if (dy < 0 || drag.sheet.scrollTop > 0) { drag.sheet = null; return; }   // scrolling the list up
                drag.on = true;
                drag.sheet.style.transition = 'none';
            }
            if (e.cancelable) e.preventDefault();
            drag.dy = Math.max(0, dy);
            drag.sheet.style.transform = `translateY(${drag.dy}px)`;
        }, { passive: false });
        const release = () => {
            const sheet = drag.sheet;
            drag.sheet = null;
            if (!sheet || !drag.on) return;
            const fast = drag.dy / Math.max(1, Date.now() - drag.t0) > 0.6;
            sheet.style.transition = 'transform .18s ease-out';
            if (drag.dy > Math.min(120, sheet.offsetHeight * 0.3) || (fast && drag.dy > 30)) {
                sheet.style.transform = 'translateY(100%)';
                setTimeout(() => st.dotnet.invokeMethodAsync('Escape'), 160);
            } else {
                sheet.style.transform = '';
            }
        };
        document.addEventListener('touchend', release, { passive: true });
        document.addEventListener('touchcancel', release, { passive: true });
        // Belt and braces: swallow the click that some browsers still send after a long press.
        document.addEventListener('click', e => {
            if (Date.now() - st.opened < 700 && find(e.target)) { e.preventDefault(); e.stopPropagation(); }
        }, true);
        document.addEventListener('keydown', e => {
            if (!(e.key === 'ContextMenu' || (e.shiftKey && e.key === 'F10'))) return;
            const el = find(document.activeElement);
            if (!el) return;
            e.preventDefault();
            const r = el.getBoundingClientRect();
            open(el, r.left + Math.min(r.width / 2, 40), r.top + Math.min(r.height / 2, 40), false);
        });
    },
    /** Tells LiveConnection when the tab is hidden or shown (null unregisters). */
    onVisibility(dotnet) {
        window.seenitVisibility = dotnet;
        if (window.seenitVisibilityBound) return;
        window.seenitVisibilityBound = true;
        document.addEventListener('visibilitychange', () => {
            const d = window.seenitVisibility;
            if (d) d.invokeMethodAsync('Visibility', document.visibilityState === 'visible').catch(() => { });
        });
    },
    copyText(text) {
        return navigator.clipboard.writeText(text).then(() => true, () => false);
    },
    focusFirst(selector) {
        const el = document.querySelector(selector);
        if (el) el.focus({ preventScroll: true });
    },
    // Settings → Display: compact spacing and poster size, as classes on <html>.
    setLayout(compact, posters) {
        const c = document.documentElement.classList;
        c.toggle('compact', !!compact);
        c.toggle('posters-small', posters === 'Small');
        c.toggle('posters-large', posters === 'Large');
    },
    /* ---------- Push notifications (website and installed web apps; not the Android app) ---------- */
    _pushReg: null,
    async _pushWorker() {
        if (!('serviceWorker' in navigator)) return null;
        if (!this._pushReg) {
            this._pushReg = await navigator.serviceWorker.register('push-sw.js');
            // A tapped notification asks an open window to go to its page, inside the app.
            navigator.serviceWorker.addEventListener('message', e => {
                if (e.data && e.data.type === 'seenit-navigate' && e.data.url && window.Blazor) window.Blazor.navigateTo(e.data.url);
            });
        }
        await navigator.serviceWorker.ready;
        return this._pushReg;
    },
    _pushIos() { return /iPad|iPhone|iPod/.test(navigator.userAgent) || (navigator.platform === 'MacIntel' && navigator.maxTouchPoints > 1); },
    _pushStandalone() { return window.matchMedia('(display-mode: standalone)').matches || navigator.standalone === true; },
    _pushDevice() {
        const ua = navigator.userAgent;
        const os = /iPhone/.test(ua) ? 'iPhone' : /iPad/.test(ua) || this._pushIos() ? 'iPad' : /Android/.test(ua) ? 'Android'
            : /Mac OS X/.test(ua) ? 'Mac' : /Windows/.test(ua) ? 'Windows' : /Linux/.test(ua) ? 'Linux' : 'Device';
        const browser = /Edg\//.test(ua) ? 'Edge' : /Firefox\//.test(ua) ? 'Firefox' : /Chrome\//.test(ua) ? 'Chrome' : /Safari\//.test(ua) ? 'Safari' : 'browser';
        return this._pushStandalone() ? `${os} (Home Screen app)` : `${os}, ${browser}`;
    },
    /** What this browser can do and whether it's subscribed: { supported, standalone, ios, permission, endpoint }. */
    async pushState() {
        const supported = 'serviceWorker' in navigator && 'PushManager' in window && 'Notification' in window;
        const state = { supported, standalone: this._pushStandalone(), ios: this._pushIos(), permission: supported ? Notification.permission : 'unsupported', endpoint: null };
        if (!supported) return state;
        try {
            const reg = await navigator.serviceWorker.getRegistration();
            const sub = reg && await reg.pushManager.getSubscription();
            state.endpoint = sub ? sub.endpoint : null;
        } catch { }
        return state;
    },
    /** Asks permission (must follow a tap: iOS insists) and subscribes. Returns the keys for the server, or { error }. */
    async pushSubscribe(publicKey) {
        try {
            const reg = await this._pushWorker();
            if (!reg) return { error: 'This browser can\'t do push notifications.' };
            const permission = await Notification.requestPermission();
            if (permission !== 'granted') return { error: permission === 'denied' ? 'Notifications are blocked for SeenIt. Allow them in this browser\'s (or iOS) settings.' : 'Notifications weren\'t allowed.' };
            const key = Uint8Array.from(atob(publicKey.replace(/-/g, '+').replace(/_/g, '/') + '='.repeat((4 - publicKey.length % 4) % 4)), c => c.charCodeAt(0));
            let sub = await reg.pushManager.getSubscription();
            if (sub) {
                // Made with another server key (keys changed): start over.
                const old = sub.options && sub.options.applicationServerKey && new Uint8Array(sub.options.applicationServerKey);
                if (old && (old.length !== key.length || old.some((b, i) => b !== key[i]))) { await sub.unsubscribe(); sub = null; }
            }
            sub = sub || await reg.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey: key });
            const j = sub.toJSON();
            return { endpoint: j.endpoint, p256dh: j.keys.p256dh, auth: j.keys.auth, device: this._pushDevice() };
        } catch (e) {
            return { error: (e && e.message) || 'Couldn\'t turn on notifications.' };
        }
    },
    /** Unsubscribes this browser; returns the endpoint it had, so the server can forget it too. */
    async pushUnsubscribe() {
        try {
            const reg = await navigator.serviceWorker.getRegistration();
            const sub = reg && await reg.pushManager.getSubscription();
            if (!sub) return null;
            const endpoint = sub.endpoint;
            await sub.unsubscribe();
            return endpoint;
        } catch { return null; }
    },

    /** Settings search: show only the sections whose text has every word typed (accents and case ignored); "" shows all. */
    filterSettings(q) {
        const fold = t => t.normalize('NFD').replace(/[\u0300-\u036f]/g, '').toLowerCase();
        const words = fold(q).split(/\s+/).filter(Boolean);
        const body = document.querySelector('.settings-body');
        if (!body) return;
        let shown = 0;
        for (const sec of body.querySelectorAll('section.panel')) {
            // Form values count too (a select's options are in its text; inputs aren't).
            const text = fold(sec.textContent + ' ' + [...sec.querySelectorAll('input[placeholder]')].map(i => i.placeholder).join(' '));
            const hit = words.every(w => text.includes(w));
            sec.hidden = !hit;
            if (hit) shown++;
        }
        // A tab's heading goes when none of its sections match.
        for (const h of body.querySelectorAll('.settings-group')) {
            let n = h.nextElementSibling, any = false;
            while (n && !n.classList.contains('settings-group')) { if (n.matches('section.panel') && !n.hidden) any = true; n = n.nextElementSibling; }
            h.hidden = !any;
        }
        const none = document.querySelector('.settings-nomatch');
        if (none) none.hidden = !(words.length && shown === 0);
    },

    scrollToId(id) {
        const el = document.getElementById(id);
        if (el) el.scrollIntoView({ block: 'start', behavior: 'instant' });
    },
    // Color theme: <html data-theme> drives the CSS; remembered on this device so the next start
    // (see the snippet in index.html) paints in it straight away.
    applyTheme(key, bg) {
        const root = document.documentElement;
        if (!key || key === 'seenit') delete root.dataset.theme; else root.dataset.theme = key;
        try { if (!key || key === 'seenit') localStorage.removeItem('seenit.theme'); else localStorage.setItem('seenit.theme', key); } catch { }
        const meta = document.querySelector('meta[name="theme-color"]');
        if (meta && bg) meta.setAttribute('content', bg);
    },
    // Accent color picked over the theme's own: CSS custom properties on <html>, remembered for the next start.
    applyAccent(css) {
        const root = document.documentElement;
        for (const p of ['--accent', '--accent-hover', '--accent-rgb', '--accent-soft', '--accent-text', '--on-accent']) root.style.removeProperty(p);
        if (css) root.style.cssText += css;
        try { if (css) localStorage.setItem('seenit.accent', css); else localStorage.removeItem('seenit.accent'); } catch { }
    },
    // Phones only open the keyboard when focus happens during a tap; by the time the search page
    // has rendered, that tap is over. So the tap focuses this hidden stand-in at once, and the
    // page then moves focus to the real box, which keeps the keyboard up.
    primeKeyboard() {
        // Already on the search page (it won't re-render): just focus its box.
        const existing = document.querySelector('.searchbar input[type=search]');
        if (existing) { existing.focus({ preventScroll: true }); return; }
        let proxy = document.getElementById('seenit-kb-proxy');
        if (!proxy) {
            proxy = Object.assign(document.createElement('input'), { id: 'seenit-kb-proxy', type: 'text', tabIndex: -1 });
            proxy.setAttribute('aria-hidden', 'true');
            // 16px stops iOS zooming in; off-screen-ish but focusable.
            proxy.style.cssText = 'position:fixed;top:0;left:0;width:1px;height:1px;opacity:0;font-size:16px;border:0;padding:0;pointer-events:none;';
            document.body.appendChild(proxy);
        }
        proxy.focus({ preventScroll: true });
        clearTimeout(this._proxyTimer);
        this._proxyTimer = setTimeout(() => proxy.remove(), 2000);
    },
    focusInput(el) {
        if (!el) return;
        el.focus({ preventScroll: true });
        const proxy = document.getElementById('seenit-kb-proxy');
        if (proxy) proxy.remove();
    },
    download(fileName, contentType, base64) {
        const bytes = Uint8Array.from(atob(base64), c => c.charCodeAt(0));
        const url = URL.createObjectURL(new Blob([bytes], { type: contentType }));
        const a = Object.assign(document.createElement('a'), { href: url, download: fileName });
        document.body.appendChild(a);
        a.click();
        a.remove();
        setTimeout(() => URL.revokeObjectURL(url), 10000);
    },
    /** Shares a file through the system share sheet where the browser can; otherwise downloads it. */
    async shareFile(fileName, contentType, base64, title) {
        const bytes = Uint8Array.from(atob(base64), c => c.charCodeAt(0));
        const file = new File([bytes], fileName, { type: contentType });
        if (navigator.canShare && navigator.canShare({ files: [file] })) {
            try { await navigator.share({ files: [file], title }); return 'shared'; }
            catch (e) { if (e && e.name === 'AbortError') return 'canceled'; }
        }
        this.download(fileName, contentType, base64);
        return 'downloaded';
    },
    async share(title, url) {
        if (navigator.share) {
            try { await navigator.share({ title, url }); return 'shared'; }
            catch (e) { if (e && e.name === 'AbortError') return 'canceled'; }
        }
        try { await navigator.clipboard.writeText(url); return 'copied'; }
        catch { return 'failed'; }
    },

    /** Pull down at the top of a page to reload it. Touch screens only; `dotnet.Refresh()` does the reload. */
    pullToRefresh(dotnet) {
        if (window.seenitPtr) { window.seenitPtr.dotnet = dotnet; return; }
        const state = window.seenitPtr = { dotnet, startX: 0, startY: 0, pull: 0, active: false, busy: false, decided: false };
        const threshold = 70, max = 110;

        const el = document.createElement('div');
        el.className = 'ptr';
        el.setAttribute('aria-hidden', 'true');
        el.innerHTML = '<svg viewBox="0 0 24 24" width="20" height="20" fill="none" stroke="currentColor" stroke-width="2.4" stroke-linecap="round" stroke-linejoin="round"><path d="M21 12a9 9 0 1 1-3-6.7L21 8"/><path d="M21 3v5h-5"/></svg>';
        document.body.appendChild(el);
        document.documentElement.classList.add('has-ptr');

        const show = (y, progress) => {
            el.style.transform = `translate(-50%, ${y}px) rotate(${progress * 270}deg)`;
            el.style.opacity = Math.min(1, progress * 1.4);
            el.classList.toggle('ready', progress >= 1);
        };
        const reset = () => {
            el.classList.remove('ready', 'spinning');
            el.style.transition = 'transform .2s, opacity .2s';
            show(0, 0);
            setTimeout(() => el.style.transition = '', 220);
        };

        // Somewhere the gesture shouldn't start: an open menu or dialog, or inside something already scrolled.
        const blocked = target => {
            if (document.querySelector('.modal-backdrop, .menu-backdrop, .ctx-backdrop')) return true;
            for (let n = target; n && n !== document.body; n = n.parentElement)
                if (n.scrollTop > 0) return true;
            return false;
        };

        document.addEventListener('touchstart', e => {
            if (state.busy || e.touches.length !== 1 || window.scrollY > 0 || blocked(e.target)) { state.active = false; return; }
            state.active = true; state.decided = false; state.pull = 0;
            state.startX = e.touches[0].clientX; state.startY = e.touches[0].clientY;
        }, { passive: true });

        // A touchmove listener that may cancel scrolling makes the browser wait for JavaScript
        // before every scroll step, which feels sticky when the app is busy. A pull can only start
        // at the very top, so the listener is only attached while the page is scrolled to the top.
        const onMove = e => {
            if (!state.active) return;
            // A long press opened a menu during this same touch: the drag belongs to the menu now.
            if (document.querySelector('.modal-backdrop, .menu-backdrop, .ctx-backdrop')) { state.active = false; reset(); return; }
            const dx = e.touches[0].clientX - state.startX, dy = e.touches[0].clientY - state.startY;
            if (!state.decided) {
                if (Math.abs(dx) < 8 && Math.abs(dy) < 8) return;
                // Sideways (a poster row) or upwards (normal scrolling): not ours.
                if (dy <= 0 || Math.abs(dx) > Math.abs(dy) || window.scrollY > 0) { state.active = false; return; }
                state.decided = true;
            }
            e.preventDefault();
            state.pull = Math.min(max, dy * 0.5);
            show(state.pull, state.pull / threshold);
        };
        let listening = false;
        const sync = () => {
            const atTop = window.scrollY <= 0;
            if (atTop === listening || (!atTop && state.active)) return;
            listening = atTop;
            if (atTop) document.addEventListener('touchmove', onMove, { passive: false });
            else document.removeEventListener('touchmove', onMove);
        };
        window.addEventListener('scroll', sync, { passive: true });
        sync();

        const end = async () => {
            if (!state.active) return;
            state.active = false;
            if (!state.decided || state.pull < threshold) { reset(); return; }
            state.busy = true;
            el.classList.add('spinning');
            el.style.transition = 'transform .15s';
            el.style.transform = `translate(-50%, ${threshold * 0.8}px)`;
            const started = Date.now();
            try { await state.dotnet.invokeMethodAsync('Refresh'); } catch { }
            // Keep the spinner up briefly so a fast refresh still registers.
            await new Promise(r => setTimeout(r, Math.max(0, 500 - (Date.now() - started))));
            state.busy = false;
            reset();
        };
        document.addEventListener('touchend', end);
        document.addEventListener('touchcancel', end);
    },
};

// Any tap on a plain link to the search page (header icon, tab bar, More menu) primes the keyboard.
document.addEventListener('click', e => {
    const a = e.target instanceof Element ? e.target.closest('a[href]') : null;
    if (!a) return;
    const url = new URL(a.getAttribute('href'), document.baseURI);
    const search = new URL('search', document.baseURI);
    if (url.origin === search.origin && url.pathname === search.pathname && !url.searchParams.get('q')) window.seenit.primeKeyboard();
}, true);

// "Back to top": appears after scrolling about two screens down, hides near the top.
(() => {
    let queued = false;
    const update = () => {
        queued = false;
        const btn = document.getElementById('seenit-to-top');
        if (!btn) return;
        const show = window.scrollY > Math.max(1200, window.innerHeight * 2);
        btn.classList.toggle('show', show);
        btn.tabIndex = show ? 0 : -1;
    };
    window.addEventListener('scroll', () => { if (!queued) { queued = true; requestAnimationFrame(update); } }, { passive: true });
})();

// Search suggestions: ↑/↓ move through the list instead of jumping the cursor to the start or end of the box.
document.addEventListener('keydown', e => {
    if ((e.key === 'ArrowUp' || e.key === 'ArrowDown') && e.target instanceof HTMLInputElement && e.target.dataset.suggestOpen) e.preventDefault();
}, true);

/*
 * Back / forward keeps your place. The browser restores scroll before Blazor has drawn the page (and
 * before its data arrives), when the page is still too short, so you'd land at the top. Instead remember
 * each page's position, and on Back wait until the page has rendered and is tall enough, then scroll there.
 * Touching, scrolling or a key press cancels the restore. Same in the Android app, whose back button goes
 * back through this history.
 */
(() => {
    try { history.scrollRestoration = 'manual'; } catch { }
    const KEY = 'seenit.scroll', MAX_PAGES = 100;
    let saved = {};
    try { saved = JSON.parse(sessionStorage.getItem(KEY) || '{}') || {}; } catch { saved = {}; }
    const here = () => location.pathname + location.search;
    let restoring = null, persistTimer = 0;

    const persist = () => {
        const keys = Object.keys(saved);
        for (const k of keys.slice(0, Math.max(0, keys.length - MAX_PAGES))) delete saved[k];   // oldest first
        try { sessionStorage.setItem(KEY, JSON.stringify(saved)); } catch { }
    };
    window.addEventListener('scroll', () => {
        if (restoring) return;   // the page shrinking mid-restore isn't where you were
        const k = here();
        delete saved[k];         // re-insert, so the newest pages are kept
        saved[k] = Math.round(window.scrollY);
        clearTimeout(persistTimer);
        persistTimer = setTimeout(persist, 300);
    }, { passive: true });

    window.addEventListener('popstate', () => {
        const y = saved[here()];
        if (y > 0) restore(y);
    });

    function restore(y) {
        const token = {};
        restoring = token;
        const started = performance.now();
        let changes = 0, lastChange = started;
        // Wait for the new page to be drawn: a mutation after Back, then quiet for a moment.
        const observer = new MutationObserver(() => { changes++; lastChange = performance.now(); });
        observer.observe(document.body, { childList: true, subtree: true });
        const inputs = ['wheel', 'touchstart', 'keydown', 'mousedown'];
        const stop = () => {
            if (restoring === token) restoring = null;
            observer.disconnect();
            inputs.forEach(e => window.removeEventListener(e, stop, true));
        };
        inputs.forEach(e => window.addEventListener(e, stop, { capture: true, passive: true }));

        const tick = () => {
            if (restoring !== token) return;
            const now = performance.now();
            const max = document.documentElement.scrollHeight - window.innerHeight;
            if (changes > 0 && max >= y - 2) {
                if (Math.abs(window.scrollY - y) > 1) window.scrollTo(0, y);
                // Done once the page has stopped changing (late images or rows could still push it).
                if (now - lastChange > 400) return stop();
            }
            if (now - started > 5000) {   // never got tall enough: as close as it goes
                if (changes > 0) window.scrollTo(0, Math.min(y, Math.max(0, max)));
                return stop();
            }
            requestAnimationFrame(tick);
        };
        requestAnimationFrame(tick);
    }
})();

// Already using push on this device: register the worker on every load so tapped notifications reach this window.
(() => {
    try {
        if ('serviceWorker' in navigator && 'Notification' in window && Notification.permission === 'granted')
            navigator.serviceWorker.getRegistration().then(r => { if (r) window.seenit._pushWorker(); });
    } catch { }
})();
