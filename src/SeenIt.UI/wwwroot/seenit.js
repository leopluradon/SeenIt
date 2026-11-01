// Small browser helpers for the shared SeenIt UI.
window.seenit = {
    download(fileName, contentType, base64) {
        const bytes = Uint8Array.from(atob(base64), c => c.charCodeAt(0));
        const url = URL.createObjectURL(new Blob([bytes], { type: contentType }));
        const a = Object.assign(document.createElement('a'), { href: url, download: fileName });
        document.body.appendChild(a);
        a.click();
        a.remove();
        setTimeout(() => URL.revokeObjectURL(url), 10000);
    },
    async share(title, url) {
        if (navigator.share) {
            try { await navigator.share({ title, url }); return 'shared'; }
            catch (e) { if (e && e.name === 'AbortError') return 'cancelled'; }
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

        document.addEventListener('touchmove', e => {
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
        }, { passive: false });

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
