// Small browser helpers for the shared SeenIt UI.
window.seenit = {
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
    // Color theme: <html data-theme> drives the CSS; remembered on this device so the next start
    // (see the snippet in index.html) paints in it straight away.
    applyTheme(key, bg) {
        const root = document.documentElement;
        if (!key || key === 'seenit') delete root.dataset.theme; else root.dataset.theme = key;
        try { if (!key || key === 'seenit') localStorage.removeItem('seenit.theme'); else localStorage.setItem('seenit.theme', key); } catch { }
        const meta = document.querySelector('meta[name="theme-color"]');
        if (meta && bg) meta.setAttribute('content', bg);
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
            if (document.querySelector('.modal-backdrop, .menu-backdrop')) return true;
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
