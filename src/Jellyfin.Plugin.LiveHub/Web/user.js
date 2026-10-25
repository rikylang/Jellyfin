// Live Hub — personal preferences page (saved per user on the server: valid on every device).
export default function (view) {
    let lh;
    let data;

    const $ = sel => view.querySelector(sel);

    function chips(name, options, selected) {
        const set = new Set(selected || []);
        return `<div class="lh-chips">${options.map(o => {
            const id = typeof o === 'string' ? o : o.Id;
            const label = typeof o === 'string' ? o : o.Name;
            return `<label><input type="checkbox" data-chip="${name}" value="${lh.esc(id)}" ${set.has(id) ? 'checked' : ''}/> ${lh.esc(label)}</label>`;
        }).join('')}</div>`;
    }

    function checked(name) {
        return [...view.querySelectorAll(`[data-chip="${name}"]`)].filter(x => x.checked).map(x => x.value);
    }

    function select(id, options, value) {
        return `<select is="emby-select" id="${id}">${options.map(o => `<option value="${lh.esc(o.Id)}" ${o.Id === value ? 'selected' : ''}>${lh.esc(o.Name)}</option>`).join('')}</select>`;
    }

    function render() {
        const t = lh.t;
        const p = data.Preferences;
        const n = p.Notifications || {};
        const languages = [{ Id: '', Name: t('user.serverDefault') }].concat(data.Languages);
        const quality = ['Auto', 'HdOnly', 'PreferSd', 'MaxBitrate'].map(q => ({ Id: q, Name: t('quality.' + q) }));
        const sort = ['Time', 'Popularity', 'Name'].map(s => ({ Id: s, Name: t('sort.' + s) }));
        const zones = [{ Id: '', Name: t('user.serverDefault') + ' (' + data.TimeZone + ')' }].concat(data.TimeZones.map(z => ({ Id: z, Name: z })));
        $('#lhUserRoot').innerHTML = `
            <div class="lh-card"><h3>${lh.esc(t('user.languageSection'))}</h3>
                <div class="selectContainer"><label class="selectLabel">${lh.esc(t('user.language'))}</label>${select('lhPrefLang', languages, p.Language || '')}</div>
                <div class="inputLabel">${lh.esc(t('user.fallback'))}</div>${chips('fallback', data.Languages, p.FallbackLanguages)}
                <label class="emby-checkbox-label"><input type="checkbox" is="emby-checkbox" id="lhPrefOnlyLang" ${p.OnlyMyLanguage ? 'checked' : ''}/><span>${lh.esc(t('user.onlyMyLanguage'))}</span></label>
            </div>
            <div class="lh-card"><h3>${lh.esc(t('user.quality'))}</h3>
                <div class="selectContainer">${select('lhPrefQuality', quality, p.Quality || 'Auto')}</div>
                <div class="inputContainer"><label class="inputLabel">${lh.esc(t('user.maxBitrate'))}</label><input is="emby-input" type="number" id="lhPrefMaxBitrate" min="200" value="${lh.esc(p.MaxBitrateKbps || '')}"/></div>
            </div>
            <div class="lh-card"><h3>${lh.esc(t('user.region'))}</h3>
                <div class="selectContainer"><label class="selectLabel">${lh.esc(t('user.timezone'))}</label>${select('lhPrefTz', zones, p.TimeZone || '')}</div>
                <div class="inputLabel">${lh.esc(t('user.countries'))}</div>${chips('country', data.Countries, p.Countries)}
            </div>
            <div class="lh-card"><h3>${lh.esc(t('user.sources'))}</h3><div class="fieldDescription">${lh.esc(t('user.sourcesHelp'))}</div>${chips('source', data.Providers, p.Sources)}</div>
            <div class="lh-card"><h3>${lh.esc(t('user.favorites'))}</h3>
                <div class="inputLabel">${lh.esc(t('user.favCategories'))}</div>${chips('favcat', data.Categories, p.FavoriteCategories)}
                <div class="inputContainer"><label class="inputLabel">${lh.esc(t('user.favTeams'))}</label><input is="emby-input" id="lhPrefTeams" value="${lh.esc((p.FavoriteTeams || []).join(', '))}"/><div class="fieldDescription">${lh.esc(t('user.favTeamsHelp'))}</div></div>
                <div class="inputLabel">${lh.esc(t('user.favChannels'))}</div>${chips('favch', data.Channels, p.FavoriteChannels)}
            </div>
            <div class="lh-card"><h3>${lh.esc(t('user.hidden'))}</h3><div class="fieldDescription">${lh.esc(t('user.hiddenHelp'))}</div>${chips('hidecat', data.Categories, (p.Hidden || []).filter(h => h.startsWith('cat:')).map(h => h.slice(4)))}</div>
            <div class="lh-card"><h3>${lh.esc(t('user.display'))}</h3><div class="selectContainer"><label class="selectLabel">${lh.esc(t('user.sort'))}</label>${select('lhPrefSort', sort, p.Sort || 'Time')}</div></div>
            ${data.NotificationsAvailable ? `<div class="lh-card"><h3>${lh.esc(t('user.notifications'))}</h3>
                <label class="emby-checkbox-label"><input type="checkbox" is="emby-checkbox" id="lhNotifyOn" ${n.Enabled !== false ? 'checked' : ''}/><span>${lh.esc(t('user.notifyEnabled'))}</span></label>
                <label class="emby-checkbox-label"><input type="checkbox" is="emby-checkbox" id="lhNotifyCat" ${n.Categories !== false ? 'checked' : ''}/><span>${lh.esc(t('user.notifyCategories'))}</span></label>
                <label class="emby-checkbox-label"><input type="checkbox" is="emby-checkbox" id="lhNotifyTeams" ${n.Teams !== false ? 'checked' : ''}/><span>${lh.esc(t('user.notifyTeams'))}</span></label>
                <div class="inputContainer"><label class="inputLabel">${lh.esc(t('user.notifyLead'))}</label><input is="emby-input" type="number" id="lhNotifyLead" min="0" max="120" value="${lh.esc(n.LeadMinutes || 0)}"/></div>
                <div class="fieldDescription">${lh.esc(t('user.notifyHelp'))}</div></div>` : ''}
            <button is="emby-button" class="raised button-submit emby-button" id="lhPrefSave"><span>${lh.esc(t('action.save'))}</span></button>`;
        $('#lhPrefSave').addEventListener('click', save);
    }

    async function save() {
        const p = data.Preferences;
        const otherHidden = (p.Hidden || []).filter(h => !h.startsWith('cat:'));
        const prefs = Object.assign({}, p, {
            Language: $('#lhPrefLang').value || null,
            FallbackLanguages: checked('fallback'),
            OnlyMyLanguage: $('#lhPrefOnlyLang').checked,
            Quality: $('#lhPrefQuality').value,
            MaxBitrateKbps: Number($('#lhPrefMaxBitrate').value) || null,
            TimeZone: $('#lhPrefTz').value || null,
            Countries: checked('country'),
            Sources: checked('source'),
            FavoriteCategories: checked('favcat'),
            FavoriteTeams: $('#lhPrefTeams').value.split(',').map(s => s.trim()).filter(Boolean),
            FavoriteChannels: checked('favch'),
            Hidden: otherHidden.concat(checked('hidecat').map(c => 'cat:' + c)),
            Sort: $('#lhPrefSort').value,
        });
        if ($('#lhNotifyOn')) {
            prefs.Notifications = {
                Enabled: $('#lhNotifyOn').checked,
                Categories: $('#lhNotifyCat').checked,
                Teams: $('#lhNotifyTeams').checked,
                Channels: true,
                LeadMinutes: Number($('#lhNotifyLead').value) || 0,
            };
        }
        lh.loading(true);
        try {
            data.Preferences = await lh.api('POST', 'User/Preferences', prefs);
            lh.toast(lh.t('user.saved'));
        } catch (e) {
            lh.toast(e.message);
        } finally {
            lh.loading(false);
        }
    }

    async function init() {
        lh = await import(ApiClient.getUrl('web/ConfigurationPage', { name: 'LiveHub.common.js' }));
        lh.ensureStyles();
        const info = await lh.api('GET', 'info');
        await lh.loadStrings(info.UiLanguage);
        lh.applyI18n(view);
        lh.loading(true);
        try {
            data = await lh.api('GET', 'User/Preferences');
            render();
        } catch (e) {
            $('#lhUserRoot').innerHTML = `<div class="lh-card lh-muted">${lh.esc(e.message)}</div>`;
        } finally {
            lh.loading(false);
        }
    }

    view.addEventListener('viewshow', () => { init().catch(e => console.error('LiveHub', e)); });
}
