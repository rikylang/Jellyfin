// Live Hub — shared helpers for the plugin pages (ES module).
let strings = {};
let lang = 'en';

export function resolveLanguage(configured) {
    const pick = (configured && configured !== 'auto') ? configured : (document.documentElement.lang || navigator.language || 'en');
    const two = String(pick).toLowerCase().slice(0, 2);
    return two === 'it' ? 'it' : 'en';
}

export async function loadStrings(configured) {
    lang = resolveLanguage(configured);
    try {
        const res = await fetch(ApiClient.getUrl('LiveHub/i18n/' + lang));
        strings = await res.json();
    } catch (e) {
        console.warn('LiveHub: unable to load strings', e);
        strings = {};
    }
    return lang;
}

export function currentLanguage() {
    return lang;
}

export function t(key, ...args) {
    let s = strings[key] ?? key;
    args.forEach((a, i) => { s = s.replace('{' + i + '}', a); });
    return s;
}

export function applyI18n(root) {
    root.querySelectorAll('[data-i18n]').forEach(el => { el.textContent = t(el.getAttribute('data-i18n')); });
    root.querySelectorAll('[data-i18n-placeholder]').forEach(el => { el.setAttribute('placeholder', t(el.getAttribute('data-i18n-placeholder'))); });
    root.querySelectorAll('[data-i18n-title]').forEach(el => { el.setAttribute('title', t(el.getAttribute('data-i18n-title'))); });
}

export async function api(method, path, body, options) {
    // Read the body as text: 204 No Content (saves) must not be parsed as JSON.
    const opts = { type: method, url: ApiClient.getUrl('LiveHub/' + path), dataType: 'text' };
    if (body !== undefined) {
        opts.data = JSON.stringify(body);
        opts.contentType = 'application/json';
    }
    try {
        const text = await ApiClient.ajax(opts);
        if (typeof text !== 'string') {
            return text ?? {};
        }
        return text.length ? JSON.parse(text) : {};
    } catch (err) {
        // 400 responses carry validation details: return them to the caller.
        if (options && options.allowErrors && err && typeof err.json === 'function') {
            try { return await err.json(); } catch (e) { /* fall through */ }
        }
        let message = String(err && (err.statusText || err.message || err));
        try {
            if (err && typeof err.json === 'function') {
                const j = await err.json();
                message = j.detail || j.title || j.message || message;
            } else if (err && err.responseText) {
                const j = JSON.parse(err.responseText);
                message = j.detail || j.title || j.message || message;
            }
        } catch (e) { /* ignore */ }
        throw new Error(message);
    }
}

export function download(filename, text) {
    const blob = new Blob([text], { type: 'application/json' });
    const a = document.createElement('a');
    a.href = URL.createObjectURL(blob);
    a.download = filename;
    document.body.appendChild(a);
    a.click();
    setTimeout(() => { URL.revokeObjectURL(a.href); a.remove(); }, 1000);
}

export const MASK = '••••••••';

export function esc(value) {
    return String(value ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
}

export function toast(text) {
    if (window.Dashboard && Dashboard.alert) {
        Dashboard.alert({ message: text });
    } else {
        alert(text);
    }
}

export function loading(show) {
    if (!window.Dashboard) { return; }
    if (show) { Dashboard.showLoadingMsg(); } else { Dashboard.hideLoadingMsg(); }
}

export function fmtDate(iso, tz) {
    if (!iso) { return ''; }
    try {
        return new Date(iso).toLocaleString(lang === 'it' ? 'it-IT' : 'en-GB', { timeZone: tz || undefined, weekday: 'short', day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit' });
    } catch (e) {
        return iso;
    }
}

export function ensureStyles() {
    if (document.getElementById('livehub-css')) { return; }
    const link = document.createElement('link');
    link.id = 'livehub-css';
    link.rel = 'stylesheet';
    link.href = ApiClient.getUrl('web/ConfigurationPage', { name: 'LiveHub.css' });
    document.head.appendChild(link);
}
