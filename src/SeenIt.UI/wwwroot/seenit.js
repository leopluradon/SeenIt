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
};
