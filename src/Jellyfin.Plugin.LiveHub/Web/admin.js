// Live Hub — administration page controller.
const PLUGIN_ID = '19816826-9c4f-49a5-9222-e00484264789';

export default function (view) {
    let lh;
    let t = k => k;
    const S = {
        providers: [],
        templates: [],
        config: null,
        wizard: { step: 1, input: {}, secrets: {}, detection: null, definition: null, preview: null },
        editor: { def: null, secrets: {}, isNew: false },
        egress: [],
        users: []
    };
    const $ = sel => view.querySelector(sel);
    const esc = v => lh.esc(v);

    // ------------------------------------------------------------------ bootstrap
    async function init() {
        lh = await import(ApiClient.getUrl('web/ConfigurationPage', { name: 'LiveHub.common.js' }));
        t = lh.t;
        lh.ensureStyles();
        const info = await lh.api('GET', 'info');
        await lh.loadStrings(info.UiLanguage);
        lh.applyI18n(view);
        $('#lhVersion').textContent = 'v' + info.Version;
        view.querySelectorAll('#lhTabs button').forEach(b => b.addEventListener('click', () => showTab(b.dataset.tab)));
        $('#lhAddApi').addEventListener('click', () => { resetWizard(); showTab('wizard'); });
        $('#lhImport').addEventListener('click', () => $('#lhImportFile').click());
        $('#lhImportFile').addEventListener('change', importFile);
        $('#lhSyncAll').addEventListener('click', () => syncNow());
        $('#lhDiagRefresh').addEventListener('click', loadDiagnostics);
        await Promise.all([loadProviders(), loadTemplates(), loadConfig()]);
        renderWizard();
        renderEditor();
    }

    function showTab(name) {
        view.querySelectorAll('#lhTabs button').forEach(x => x.classList.toggle('active', x.dataset.tab === name));
        view.querySelectorAll('.lh-panel').forEach(p => p.classList.toggle('active', p.dataset.panel === name));
        if (name === 'diagnostics') { loadDiagnostics(); }
        if (name === 'egress') { loadEgress(); }
        if (name === 'users') { loadUsers(); }
        if (name === 'settings') { renderSettings(); }
    }

    async function guarded(fn) {
        lh.loading(true);
        try { return await fn(); } catch (e) { lh.toast(e.message || String(e)); return null; } finally { lh.loading(false); }
    }

    // ------------------------------------------------------------------ providers
    async function loadProviders() {
        S.providers = await lh.api('GET', 'Admin/Providers');
        let diag = null;
        try { diag = await lh.api('GET', 'Admin/Diagnostics'); } catch (e) { /* not available yet */ }
        renderProviders(diag);
    }

    function statsFor(diag, id) {
        return diag && diag.Providers ? diag.Providers.find(p => p.ProviderId === id) : null;
    }

    function renderProviders(diag) {
        const root = $('#lhProviderList');
        if (!S.providers.length) {
            root.innerHTML = `<div class="lh-card lh-muted">${esc(t('providers.empty'))}</div>`;
            return;
        }
        root.innerHTML = `<table class="lh-table"><thead><tr>
            <th>${esc(t('field.name'))}</th><th>${esc(t('field.kind'))}</th><th>${esc(t('field.priority'))}</th>
            <th>${esc(t('providers.status'))}</th><th>${esc(t('providers.content'))}</th><th></th></tr></thead><tbody>
            ${S.providers.map(p => {
                const s = statsFor(diag, p.id);
                const state = !p.enabled ? `<span class="lh-badge">${esc(t('providers.disabled'))}</span>`
                    : s && s.LastError && s.ConsecutiveFailures > 0 ? `<span class="lh-badge err" title="${esc(s.LastError)}">${esc(t('providers.error'))}</span>`
                    : s && s.LastSuccessAt ? `<span class="lh-badge ok">OK</span>` : `<span class="lh-badge">${esc(t('providers.pending'))}</span>`;
                const content = s ? `${s.Events} ${esc(t('providers.events'))} · ${s.Channels} ${esc(t('providers.channels'))} · ${s.Streams} stream` : '';
                return `<tr data-id="${esc(p.id)}">
                    <td><b>${esc(p.name)}</b><div class="lh-muted">${esc(p.id)}${p.egressProfile ? ' · ' + esc(p.egressProfile) : ''}</div></td>
                    <td>${esc(p.kind)}</td><td>${esc(p.priority ?? 100)}</td><td>${state}${s && s.LastSyncAt ? `<div class="lh-muted">${esc(lh.fmtDate(s.LastSyncAt))}</div>` : ''}</td>
                    <td>${content}</td>
                    <td class="lh-row">
                        <button is="emby-button" class="emby-button raised" data-act="edit">${esc(t('action.edit'))}</button>
                        <button is="emby-button" class="emby-button raised" data-act="toggle">${esc(p.enabled ? t('action.disable') : t('action.enable'))}</button>
                        <button is="emby-button" class="emby-button raised" data-act="sync">${esc(t('action.sync'))}</button>
                        <button is="emby-button" class="emby-button raised" data-act="export">${esc(t('action.export'))}</button>
                        <button is="emby-button" class="emby-button raised" data-act="delete">${esc(t('action.delete'))}</button>
                    </td></tr>`;
            }).join('')}</tbody></table>`;
        root.querySelectorAll('button[data-act]').forEach(b => b.addEventListener('click', () => providerAction(b.closest('tr').dataset.id, b.dataset.act)));
    }

    async function providerAction(id, act) {
        const p = S.providers.find(x => x.id === id);
        if (act === 'edit') { openEditor(JSON.parse(JSON.stringify(p)), false); return; }
        if (act === 'toggle') { await guarded(() => lh.api('POST', `Admin/Providers/${encodeURIComponent(id)}/Enabled?enabled=${!p.enabled}`, undefined, { noJson: true })); await loadProviders(); return; }
        if (act === 'sync') { await syncNow(id); return; }
        if (act === 'export') {
            const def = await guarded(() => lh.api('GET', `Admin/Providers/${encodeURIComponent(id)}/Export`));
            if (def) { lh.download(id + '.livehub.json', JSON.stringify(def, null, 2)); }
            return;
        }
        if (act === 'delete' && confirm(t('providers.confirmDelete').replace('{0}', p.name))) {
            await guarded(() => lh.api('DELETE', `Admin/Providers/${encodeURIComponent(id)}`, undefined, { noJson: true }));
            await loadProviders();
        }
    }

    async function syncNow(id) {
        await guarded(() => lh.api('POST', 'Admin/Sync' + (id ? '?providerId=' + encodeURIComponent(id) : ''), undefined, { noJson: true }));
        lh.toast(t('providers.syncStarted'));
        setTimeout(loadProviders, 3000);
    }

    async function importFile(ev) {
        const file = ev.target.files[0];
        ev.target.value = '';
        if (!file) { return; }
        const text = await file.text();
        let json;
        try { json = JSON.parse(text); } catch (e) { lh.toast(t('editor.invalidJson')); return; }
        const res = await guarded(() => lh.api('POST', 'Admin/Import', json, { allowErrors: true }));
        if (res && res.Errors) { lh.toast(formatIssues(res.Errors)); return; }
        if (res) {
            lh.toast(t('providers.imported').replace('{0}', res.Id));
            await loadProviders();
            const def = await lh.api('GET', 'Admin/Providers/' + encodeURIComponent(res.Id));
            openEditor(def, false);
        }
    }

    async function loadTemplates() {
        S.templates = await lh.api('GET', 'Admin/Templates');
        $('#lhTemplates').innerHTML = S.templates.map((tp, i) => `<div class="lh-card lh-grow"><b>${esc(tp.Name)}</b>
            <div class="lh-muted">${esc(tp.Description || '')}</div>
            <button is="emby-button" class="emby-button raised" data-tpl="${i}">${esc(t('providers.useTemplate'))}</button></div>`).join('');
        $('#lhTemplates').querySelectorAll('button[data-tpl]').forEach(b => b.addEventListener('click', () => {
            const def = JSON.parse(S.templates[+b.dataset.tpl].Json);
            openEditor(def, true);
        }));
    }

    function formatIssues(list) {
        return (list || []).map(i => (i.Path ? i.Path + ': ' : '') + i.Message).join('\n');
    }

    // ------------------------------------------------------------------ wizard
    function resetWizard() {
        S.wizard = { step: 1, input: {}, secrets: {}, detection: null, definition: null, preview: null };
        renderWizard();
    }

    function setStep(n) {
        S.wizard.step = n;
        view.querySelectorAll('#lhWizSteps span').forEach(s => s.classList.toggle('active', +s.dataset.step === n));
        renderWizard();
    }

    function egressOptions(selected) {
        const profiles = (S.config && S.config.EgressProfiles) || [];
        return `<option value="">${esc(t('egress.default'))}</option>` + profiles.map(p => `<option value="${esc(p.Id)}" ${p.Id === selected ? 'selected' : ''}>${esc(p.Name || p.Id)}</option>`).join('');
    }

    function authFields(prefix, auth, secrets) {
        const a = auth || { type: 'none' };
        const sv = n => esc(secrets && secrets[n] !== undefined ? secrets[n] : '');
        return `<div class="selectContainer"><label class="selectLabel">${esc(t('auth.type'))}</label>
            <select is="emby-select" id="${prefix}AuthType">
                ${['none', 'apiKey', 'bearer', 'basic', 'oauth2ClientCredentials', 'cookie'].map(x => `<option value="${x}" ${a.type === x ? 'selected' : ''}>${esc(t('auth.' + x))}</option>`).join('')}
            </select></div>
            <div class="lh-row" id="${prefix}AuthFields">
                <div class="inputContainer lh-grow" data-auth="apiKey cookie"><label class="inputLabel">${esc(t('auth.name'))}</label><input is="emby-input" id="${prefix}AuthName" value="${esc(a.name || '')}" placeholder="X-Api-Key"/></div>
                <div class="selectContainer lh-grow" data-auth="apiKey"><label class="selectLabel">${esc(t('auth.in'))}</label><select is="emby-select" id="${prefix}AuthIn"><option value="header">header</option><option value="query" ${a.in === 'query' ? 'selected' : ''}>query</option></select></div>
                <div class="inputContainer lh-grow" data-auth="apiKey bearer cookie"><label class="inputLabel">${esc(t('auth.value'))}</label><input is="emby-input" type="password" id="${prefix}AuthValue" value="${sv('apiKey') || sv('token')}" autocomplete="off"/></div>
                <div class="inputContainer lh-grow" data-auth="basic"><label class="inputLabel">${esc(t('auth.username'))}</label><input is="emby-input" id="${prefix}AuthUser" value="${sv('username')}" autocomplete="off"/></div>
                <div class="inputContainer lh-grow" data-auth="basic"><label class="inputLabel">${esc(t('auth.password'))}</label><input is="emby-input" type="password" id="${prefix}AuthPass" value="${sv('password')}" autocomplete="off"/></div>
                <div class="inputContainer lh-grow" data-auth="oauth2ClientCredentials"><label class="inputLabel">Token URL</label><input is="emby-input" id="${prefix}AuthTokenUrl" value="${esc(a.tokenUrl || '')}"/></div>
                <div class="inputContainer lh-grow" data-auth="oauth2ClientCredentials"><label class="inputLabel">Client ID</label><input is="emby-input" id="${prefix}AuthClientId" value="${esc(a.clientId && !a.clientId.startsWith('{') ? a.clientId : sv('clientId'))}"/></div>
                <div class="inputContainer lh-grow" data-auth="oauth2ClientCredentials"><label class="inputLabel">Client secret</label><input is="emby-input" type="password" id="${prefix}AuthClientSecret" value="${sv('clientSecret')}" autocomplete="off"/></div>
                <div class="inputContainer lh-grow" data-auth="oauth2ClientCredentials"><label class="inputLabel">Scope</label><input is="emby-input" id="${prefix}AuthScope" value="${esc(a.scope || '')}"/></div>
            </div>`;
    }

    function bindAuthVisibility(prefix) {
        const sel = $(`#${prefix}AuthType`);
        const update = () => view.querySelectorAll(`#${prefix}AuthFields [data-auth]`).forEach(el => {
            el.style.display = el.dataset.auth.split(' ').includes(sel.value) ? '' : 'none';
        });
        sel.addEventListener('change', update);
        update();
    }

    /** Reads the auth form into { auth, secrets } using {secret.x} references (secrets are encrypted on save). */
    function readAuth(prefix) {
        const type = $(`#${prefix}AuthType`).value;
        const v = id => ($(`#${prefix}${id}`) || {}).value || '';
        const secrets = {};
        let auth = { type };
        if (type === 'apiKey') { auth = { type, in: v('AuthIn'), name: v('AuthName') || 'X-Api-Key', value: '{secret.apiKey}' }; secrets.apiKey = v('AuthValue'); }
        if (type === 'bearer') { auth = { type, value: '{secret.token}' }; secrets.token = v('AuthValue'); }
        if (type === 'cookie') { auth = { type, name: v('AuthName'), value: '{secret.cookie}' }; secrets.cookie = v('AuthValue'); }
        if (type === 'basic') { auth = { type, username: '{secret.username}', password: '{secret.password}' }; secrets.username = v('AuthUser'); secrets.password = v('AuthPass'); }
        if (type === 'oauth2ClientCredentials') {
            auth = { type, tokenUrl: v('AuthTokenUrl'), clientId: '{secret.clientId}', clientSecret: '{secret.clientSecret}', scope: v('AuthScope') || undefined };
            secrets.clientId = v('AuthClientId'); secrets.clientSecret = v('AuthClientSecret');
        }
        return { auth: type === 'none' ? null : auth, secrets };
    }

    function renderWizard() {
        const body = $('#lhWizBody');
        if (!lh) { return; }
        const w = S.wizard;
        if (w.step === 1) {
            const i = w.input;
            body.innerHTML = `<div class="lh-card">
                <div class="inputContainer"><label class="inputLabel">${esc(t('wizard.url'))}</label><input is="emby-input" id="lhWizUrl" value="${esc(i.Url || '')}" placeholder="https://api.example.com/v1/events"/>
                    <div class="fieldDescription">${esc(t('wizard.urlHelp'))}</div></div>
                ${authFields('lhWiz', i.Auth, w.secrets)}
                <div class="lh-row">
                    <div class="selectContainer lh-grow"><label class="selectLabel">${esc(t('field.egress'))}</label><select is="emby-select" id="lhWizEgress">${egressOptions(i.EgressProfile)}</select></div>
                    <label class="emby-checkbox-label lh-grow"><input type="checkbox" is="emby-checkbox" id="lhWizPrivate" ${i.AllowPrivateNetwork ? 'checked' : ''}/><span>${esc(t('field.allowPrivate'))}</span></label>
                </div>
                <details><summary>${esc(t('wizard.more'))}</summary>
                    <div class="inputContainer"><label class="inputLabel">${esc(t('wizard.headers'))}</label><textarea is="emby-textarea" class="lh-code lh-small-code" id="lhWizHeaders" placeholder="Accept: application/json">${esc(Object.entries(i.Headers || {}).map(([k, v]) => k + ': ' + v).join('\n'))}</textarea></div>
                    <div class="inputContainer"><label class="inputLabel">${esc(t('wizard.sample'))}</label><textarea is="emby-textarea" class="lh-code lh-small-code" id="lhWizSample">${esc(i.SampleResponse || '')}</textarea></div>
                    <div class="inputContainer"><label class="inputLabel">${esc(t('wizard.docs'))}</label><textarea is="emby-textarea" class="lh-code lh-small-code" id="lhWizDocs">${esc(i.Documentation || '')}</textarea></div>
                    <div class="inputContainer"><label class="inputLabel">${esc(t('wizard.openapi'))}</label><textarea is="emby-textarea" class="lh-code lh-small-code" id="lhWizOpenApi">${esc(i.OpenApiSpec || '')}</textarea></div>
                </details>
                <button is="emby-button" class="raised button-submit emby-button" id="lhWizDetect"><span>${esc(t('wizard.detect'))}</span></button>
            </div>`;
            bindAuthVisibility('lhWiz');
            $('#lhWizDetect').addEventListener('click', wizardDetect);
            return;
        }
        if (w.step === 2) {
            const d = w.detection;
            const def = w.definition;
            const ep = def && ((def.events && def.events[0]) || (def.channels && def.channels[0]));
            body.innerHTML = `<div class="lh-card"><div><span class="lh-badge">${esc(d.Format)}</span> ${esc(d.Summary)}</div>
                ${d.HttpStatus ? `<div class="lh-muted">HTTP ${esc(d.HttpStatus)}</div>` : ''}
                ${(d.Warnings || []).map(x => `<div class="lh-badge warn">!</div> ${esc(x)}`).join('<br/>')}
                ${d.Operations && d.Operations.length ? `<h4>${esc(t('wizard.operations'))}</h4>
                    <select is="emby-select" id="lhWizOp">${d.Operations.map(o => `<option value="${esc(o.Method + ' ' + o.Path)}">${esc(o.Method + ' ' + o.Path)} ${o.SuggestedRole ? '· ' + esc(o.SuggestedRole) : ''} ${o.Summary ? '— ' + esc(o.Summary) : ''}</option>`).join('')}</select>
                    <button is="emby-button" class="raised emby-button" id="lhWizUseOp"><span>${esc(t('wizard.useOperation'))}</span></button>` : ''}
            </div>
            ${ep ? `<div class="lh-card"><h4>${esc(t('wizard.mapping'))}</h4>
                <div class="inputContainer"><label class="inputLabel">Root (JSONPath)</label><input is="emby-input" id="lhMapRoot" value="${esc(ep.root || '')}"/></div>
                ${mappingRows(ep.fields || {}, d.Fields || [], 'f')}
                <h4>${esc(t('wizard.streamMapping'))}</h4>
                <div class="inputContainer"><label class="inputLabel">${esc(t('wizard.streamsPath'))}</label><input is="emby-input" id="lhMapStreamsPath" value="${esc(ep.streams ? ep.streams.path : '')}" placeholder="$.streams[*]"/></div>
                ${mappingRows((ep.streams && ep.streams.fields) || {}, d.StreamFields || [], 's')}
            </div>` : ''}
            ${d.Sample ? `<details class="lh-card"><summary>${esc(t('wizard.sampleShown'))}</summary><pre class="lh-pre">${esc(d.Sample)}</pre></details>` : ''}
            <div class="lh-row">
                <button is="emby-button" class="raised emby-button" id="lhWizBack"><span>${esc(t('action.back'))}</span></button>
                <button is="emby-button" class="raised emby-button" id="lhWizEdit" ${def ? '' : 'disabled'}><span>${esc(t('wizard.openEditor'))}</span></button>
                <button is="emby-button" class="raised button-submit emby-button" id="lhWizPreview" ${def ? '' : 'disabled'}><span>${esc(t('wizard.preview'))}</span></button>
            </div>`;
            $('#lhWizBack').addEventListener('click', () => setStep(1));
            if (def) {
                $('#lhWizPreview').addEventListener('click', async () => { applyMapping(); await wizardPreview(); });
                $('#lhWizEdit').addEventListener('click', () => { applyMapping(); openEditor(def, true, w.secrets); });
            }
            const op = $('#lhWizUseOp');
            if (op) { op.addEventListener('click', () => { S.wizard.input.OpenApiOperation = $('#lhWizOp').value; wizardDetect(true); }); }
            return;
        }
        if (w.step === 3) {
            body.innerHTML = previewHtml(w.preview) + `<div class="lh-row">
                <button is="emby-button" class="raised emby-button" id="lhWizBack"><span>${esc(t('action.back'))}</span></button>
                <button is="emby-button" class="raised emby-button" id="lhWizEdit"><span>${esc(t('wizard.openEditor'))}</span></button>
                <button is="emby-button" class="raised button-submit emby-button" id="lhWizNext" ${w.preview && w.preview.Valid ? '' : 'disabled'}><span>${esc(t('action.next'))}</span></button></div>`;
            bindPreviewActions(body, w.definition, w.secrets);
            $('#lhWizBack').addEventListener('click', () => setStep(2));
            $('#lhWizEdit').addEventListener('click', () => openEditor(w.definition, true, w.secrets));
            $('#lhWizNext').addEventListener('click', () => setStep(4));
            return;
        }
        if (w.step === 4) {
            const def = w.definition;
            body.innerHTML = `<div class="lh-card">
                <div class="inputContainer"><label class="inputLabel">${esc(t('field.name'))}</label><input is="emby-input" id="lhWizName" value="${esc(def.name)}"/></div>
                <div class="inputContainer"><label class="inputLabel">${esc(t('field.id'))}</label><input is="emby-input" id="lhWizId" value="${esc(def.id)}"/></div>
                <div class="lh-row">
                    <div class="inputContainer lh-grow"><label class="inputLabel">${esc(t('field.language'))}</label><input is="emby-input" id="lhWizLang" value="${esc((def.defaults || {}).language || '')}" placeholder="it"/></div>
                    <div class="inputContainer lh-grow"><label class="inputLabel">${esc(t('field.country'))}</label><input is="emby-input" id="lhWizCountry" value="${esc((def.defaults || {}).country || '')}" placeholder="IT"/></div>
                    <div class="inputContainer lh-grow"><label class="inputLabel">${esc(t('field.timezone'))}</label><input is="emby-input" id="lhWizTz" value="${esc((def.defaults || {}).timezone || '')}" placeholder="Europe/Rome"/></div>
                </div>
                <button is="emby-button" class="raised button-submit emby-button" id="lhWizSave"><span>${esc(t('action.save'))}</span></button></div>`;
            $('#lhWizSave').addEventListener('click', async () => {
                def.name = $('#lhWizName').value.trim();
                def.id = $('#lhWizId').value.trim();
                def.defaults = Object.assign({}, def.defaults, {
                    language: $('#lhWizLang').value.trim() || undefined,
                    country: $('#lhWizCountry').value.trim() || undefined,
                    timezone: $('#lhWizTz').value.trim() || undefined
                });
                await saveDefinition(def, w.secrets, () => { resetWizard(); showTab('providers'); });
            });
        }
    }

    function mappingRows(fields, report, prefix) {
        const names = new Set([...Object.keys(fields), ...report.map(r => r.Field)]);
        const reportOf = f => report.find(r => r.Field === f);
        const fmt = spec => typeof spec === 'string' ? spec : spec ? (spec.path || spec.template || spec.value || '') : '';
        return `<div>${[...names].filter(n => n !== 'participants').map(n => {
            const r = reportOf(n);
            const spec = fields[n];
            return `<div class="lh-map-row"><b>${esc(n)}</b>
                <input is="emby-input" data-map="${prefix}" data-field="${esc(n)}" value="${esc(fmt(spec))}"/>
                <span class="lh-muted">${r ? esc(Math.round(r.Confidence * 100) + '% · ' + (r.Sample || '')) : ''}${r && r.Format ? ' · ' + esc(r.Format) : ''}</span></div>`;
        }).join('')}</div>`;
    }

    /** Writes the edited mapping inputs back into the wizard definition. */
    function applyMapping() {
        const def = S.wizard.definition;
        const ep = def && ((def.events && def.events[0]) || (def.channels && def.channels[0]));
        if (!ep) { return; }
        const root = $('#lhMapRoot');
        if (root) { ep.root = root.value.trim() || undefined; }
        const toSpec = (old, text) => {
            if (!text) { return undefined; }
            if (old && typeof old === 'object' && (old.transforms || old.format || old.timezone)) {
                const copy = Object.assign({}, old);
                delete copy.path; delete copy.template; delete copy.value;
                if (text.startsWith('$') || text.startsWith('@')) { copy.path = text; } else if (text.includes('{')) { copy.template = text; } else { copy.value = text; }
                return copy;
            }
            return text;
        };
        view.querySelectorAll('input[data-map="f"]').forEach(inp => {
            const f = inp.dataset.field;
            const spec = toSpec(ep.fields[f], inp.value.trim());
            if (spec === undefined) { delete ep.fields[f]; } else { ep.fields[f] = spec; }
        });
        const sp = $('#lhMapStreamsPath');
        if (sp) {
            const path = sp.value.trim();
            if (!path) { delete ep.streams; } else {
                ep.streams = ep.streams || { path, fields: {} };
                ep.streams.path = path;
                view.querySelectorAll('input[data-map="s"]').forEach(inp => {
                    const f = inp.dataset.field;
                    const spec = toSpec(ep.streams.fields[f], inp.value.trim());
                    if (spec === undefined) { delete ep.streams.fields[f]; } else { ep.streams.fields[f] = spec; }
                });
            }
        }
    }

    async function wizardDetect(keepInput) {
        if (keepInput !== true) {
            const auth = readAuth('lhWiz');
            const headers = {};
            ($('#lhWizHeaders') ? $('#lhWizHeaders').value : '').split('\n').forEach(l => { const i = l.indexOf(':'); if (i > 0) { headers[l.slice(0, i).trim()] = l.slice(i + 1).trim(); } });
            S.wizard.secrets = auth.secrets;
            S.wizard.input = {
                Url: $('#lhWizUrl').value.trim(),
                Auth: auth.auth,
                Secrets: auth.secrets,
                Headers: headers,
                EgressProfile: $('#lhWizEgress').value || null,
                AllowPrivateNetwork: $('#lhWizPrivate').checked,
                SampleResponse: $('#lhWizSample').value || null,
                Documentation: $('#lhWizDocs').value || null,
                OpenApiSpec: $('#lhWizOpenApi').value || null
            };
        }
        const d = await guarded(() => lh.api('POST', 'Admin/Wizard/Detect', S.wizard.input));
        if (!d) { return; }
        S.wizard.detection = d;
        S.wizard.definition = d.Definition;
        if (d.Definition && d.Definition.secrets) {
            // The server masks secrets: keep the values typed in the form.
            Object.keys(d.Definition.secrets).forEach(k => { if (S.wizard.secrets[k] === undefined) { S.wizard.secrets[k] = ''; } });
        }
        setStep(2);
    }

    function withSecrets(def, secrets) {
        const copy = JSON.parse(JSON.stringify(def));
        if (secrets && Object.keys(secrets).length) {
            copy.secrets = Object.assign({}, copy.secrets || {});
            Object.entries(secrets).forEach(([k, v]) => { if (v !== undefined && v !== '' ) { copy.secrets[k] = v; } });
        }
        return copy;
    }

    async function wizardPreview() {
        const def = withSecrets(S.wizard.definition, S.wizard.secrets);
        const p = await guarded(() => lh.api('POST', 'Admin/Wizard/Preview', { definition: def, sample: S.wizard.input.SampleResponse || null }));
        if (!p) { return; }
        S.wizard.preview = p;
        setStep(3);
    }

    function previewHtml(p) {
        if (!p) { return ''; }
        const issues = (p.ValidationErrors || []).length ? `<div class="lh-card"><b>${esc(t('editor.errors'))}</b><pre class="lh-pre">${esc(formatIssues(p.ValidationErrors))}</pre></div>` : '';
        const counts = Object.entries(p.Counts || {}).map(([k, v]) => `<span class="lh-badge">${esc(t('count.' + k))}: ${esc(v)}</span>`).join(' ');
        const rows = (p.Rows || []).map((r, idx) => {
            const cls = f => r.Problems.includes(f) ? (f === 'start' ? 'bad' : 'miss') : '';
            return `<tr>
                <td>${r.IsChannel ? '📺' : ''} ${esc(r.Title)}<div class="lh-muted">${esc(r.Id)}</div></td>
                <td>${esc(r.Category || '')}${r.Competition ? '<div class="lh-muted">' + esc(r.Competition) + '</div>' : ''}</td>
                <td class="${cls('start')}">${r.Start ? esc(lh.fmtDate(r.Start)) : ''} ${r.Status ? `<span class="lh-badge ${r.Status === 'Live' ? 'live' : ''}">${esc(r.Status)}</span>` : ''}</td>
                <td class="${cls('language')}">${esc(r.Language || '—')} / ${esc(r.Country || '—')}</td>
                <td class="${cls('image')}">${r.Image ? '✓' : '—'}</td>
                <td class="${cls('streams')}">${r.Streams.map((s, si) => `<div>${esc(s.Kind)} ${esc(s.Quality || '')} ${esc(s.Language || '')} ${esc(s.Label || '')}
                    <button is="emby-button" class="emby-button" data-test="${idx}:${si}" title="${esc(s.Url)}">▶ ${esc(t('wizard.testPlayback'))}</button></div>`).join('')}
                    ${r.LazyStreams && !r.Streams.length ? esc(t('wizard.lazy')) : ''}</td></tr>`;
        }).join('');
        const req = (p.Requests || []).map(r => `<div>${esc(r.Status)} · ${esc(Math.round(r.Ms))} ms · ${esc(r.Items)} items · ${esc(r.Url)} ${r.Error ? '<b>' + esc(r.Error) + '</b>' : ''}</div>`).join('');
        return `${issues}<div class="lh-card">${counts}
            ${(p.Errors || []).map(e => `<div class="lh-badge err">!</div> ${esc(e)}`).join('<br/>')}
            ${(p.Warnings || []).slice(0, 20).map(e => `<div class="lh-muted">⚠ ${esc(e)}</div>`).join('')}</div>
            <div class="lh-card" style="overflow:auto"><table class="lh-table"><thead><tr><th>${esc(t('field.title'))}</th><th>${esc(t('field.category'))}</th><th>${esc(t('field.start'))}</th><th>${esc(t('field.language'))}</th><th>img</th><th>Stream</th></tr></thead><tbody>${rows}</tbody></table>
            <div class="lh-muted">${esc(t('wizard.legend'))}</div></div>
            <div id="lhTestResult"></div>
            <details class="lh-card"><summary>${esc(t('wizard.requests'))}</summary><div class="lh-pre">${req}</div></details>
            ${Object.entries(p.Samples || {}).map(([k, v]) => `<details class="lh-card"><summary>${esc(t('wizard.rawSample'))} · ${esc(k)}</summary><pre class="lh-pre">${esc(v)}</pre></details>`).join('')}`;
    }

    function bindPreviewActions(container, def, secrets) {
        const preview = S.wizard.step === 3 ? S.wizard.preview : S.editor.preview;
        container.querySelectorAll('button[data-test]').forEach(b => b.addEventListener('click', async () => {
            const [ri, si] = b.dataset.test.split(':').map(Number);
            const row = preview.Rows[ri];
            const stream = row.Streams[si];
            const out = container.querySelector('#lhTestResult');
            out.innerHTML = `<div class="lh-card lh-muted">${esc(t('wizard.testing'))}</div>`;
            const res = await guarded(() => lh.api('POST', 'Admin/TestPlayback', { definition: withSecrets(def, secrets), itemKey: row.Key, streamId: stream.Id, isChannel: row.IsChannel }));
            out.innerHTML = res ? playbackHtml(res) : '';
        }));
    }

    function playbackHtml(r) {
        const ok = r.Playable ? `<span class="lh-badge ok">${esc(t('playback.ok'))}</span>` : `<span class="lh-badge err">${esc(t('playback.ko'))}</span>`;
        return `<div class="lh-card">${ok} ${esc(r.Summary || '')}
            <table class="lh-table">
                <tr><td>${esc(t('playback.format'))}</td><td>${esc(r.Format || '')}</td></tr>
                <tr><td>${esc(t('playback.strategy'))}</td><td>${esc(r.Strategy || '')}${r.FromEmbed ? ' · ' + esc(t('playback.fromEmbed')) : ''}</td></tr>
                <tr><td>${esc(t('playback.codecs'))}</td><td>${esc((r.Codecs || []).join(', '))}</td></tr>
                <tr><td>${esc(t('playback.variants'))}</td><td>${(r.Variants || []).map(v => esc(v)).join('<br/>')}</td></tr>
                <tr><td>${esc(t('playback.headers'))}</td><td>${Object.entries(r.RequiredHeaders || {}).map(([k, v]) => esc(k + ': ' + v)).join('<br/>')}</td></tr>
                <tr><td>${esc(t('playback.latency'))}</td><td>${esc(r.LatencyMs ? Math.round(r.LatencyMs) + ' ms' : '')}</td></tr>
                <tr><td>URL</td><td style="word-break:break-all">${esc(r.ResolvedUrl || '')}</td></tr>
            </table>${(r.Steps || []).map(s => `<div class="lh-muted">· ${esc(s)}</div>`).join('')}</div>`;
    }

    async function saveDefinition(def, secrets, done) {
        const res = await guarded(() => lh.api('POST', 'Admin/Providers', withSecrets(def, secrets), { allowErrors: true }));
        if (!res) { return; }
        if (res.Errors) { lh.toast(formatIssues(res.Errors)); return; }
        lh.toast(t('editor.saved'));
        await loadProviders();
        if (done) { done(); }
    }

    // ------------------------------------------------------------------ editor (form + JSON)
    function openEditor(def, isNew, secrets) {
        S.editor = { def: JSON.parse(JSON.stringify(def)), secrets: Object.assign({}, secrets || {}), isNew, preview: null };
        renderEditor();
        showTab('editor');
    }

    function renderEditor() {
        const body = $('#lhEditorBody');
        if (!lh) { return; }
        const e = S.editor;
        if (!e.def) { body.innerHTML = `<div class="lh-card lh-muted">${esc(t('editor.empty'))}</div>`; return; }
        const d = e.def;
        const c = d.connection || {};
        const df = d.defaults || {};
        const r = d.refresh || {};
        const secretKeys = Object.keys(d.secrets || {});
        body.innerHTML = `<div class="lh-card">
            <div class="lh-row">
                <div class="inputContainer lh-grow"><label class="inputLabel">${esc(t('field.name'))}</label><input is="emby-input" data-f="name" value="${esc(d.name)}"/></div>
                <div class="inputContainer lh-grow"><label class="inputLabel">${esc(t('field.id'))}</label><input is="emby-input" data-f="id" value="${esc(d.id)}" ${e.isNew ? '' : 'readonly'}/></div>
                <div class="inputContainer"><label class="inputLabel">${esc(t('field.priority'))}</label><input is="emby-input" type="number" data-f="priority" data-num="1" value="${esc(d.priority ?? 100)}"/></div>
                <label class="emby-checkbox-label"><input type="checkbox" is="emby-checkbox" data-f="enabled" ${d.enabled !== false ? 'checked' : ''}/><span>${esc(t('field.enabled'))}</span></label>
            </div>
            <div class="lh-row">
                <div class="selectContainer lh-grow"><label class="selectLabel">${esc(t('field.kind'))}</label><select is="emby-select" data-f="kind">${['rest', 'm3u', 'xtream', 'direct'].map(k => `<option ${d.kind === k ? 'selected' : ''}>${k}</option>`).join('')}</select></div>
                <div class="selectContainer lh-grow"><label class="selectLabel">${esc(t('field.egress'))}</label><select is="emby-select" data-f="egressProfile">${egressOptions(d.egressProfile)}</select></div>
                <label class="emby-checkbox-label lh-grow"><input type="checkbox" is="emby-checkbox" data-f="allowPrivateNetwork" ${d.allowPrivateNetwork ? 'checked' : ''}/><span>${esc(t('field.allowPrivate'))}</span></label>
            </div>
            <h4>${esc(t('editor.defaults'))}</h4>
            <div class="lh-row">
                <div class="inputContainer lh-grow"><label class="inputLabel">${esc(t('field.language'))}</label><input is="emby-input" data-f="defaults.language" value="${esc(df.language || '')}" placeholder="it"/></div>
                <div class="inputContainer lh-grow"><label class="inputLabel">${esc(t('field.country'))}</label><input is="emby-input" data-f="defaults.country" value="${esc(df.country || '')}" placeholder="IT"/></div>
                <div class="inputContainer lh-grow"><label class="inputLabel">${esc(t('field.timezone'))}</label><input is="emby-input" data-f="defaults.timezone" value="${esc(df.timezone || '')}" placeholder="Europe/Rome"/></div>
                <div class="inputContainer lh-grow"><label class="inputLabel">${esc(t('field.category'))}</label><input is="emby-input" data-f="defaults.category" value="${esc(df.category || '')}"/></div>
                <div class="inputContainer"><label class="inputLabel">${esc(t('field.duration'))}</label><input is="emby-input" type="number" data-num="1" data-f="defaults.eventDurationMinutes" value="${esc(df.eventDurationMinutes || '')}"/></div>
                <div class="inputContainer"><label class="inputLabel">${esc(t('field.rating'))}</label><input is="emby-input" data-f="defaults.contentRating" value="${esc(df.contentRating || '')}" placeholder="TV-14"/></div>
            </div>
            ${d.kind === 'rest' ? `<h4>${esc(t('editor.connection'))}</h4>
            <div class="lh-row">
                <div class="inputContainer lh-grow"><label class="inputLabel">Base URL</label><input is="emby-input" data-f="connection.baseUrl" value="${esc(c.baseUrl || '')}"/></div>
                <div class="inputContainer lh-grow"><label class="inputLabel">User-Agent</label><input is="emby-input" data-f="connection.userAgent" value="${esc(c.userAgent || '')}"/></div>
                <div class="inputContainer"><label class="inputLabel">${esc(t('field.timeout'))}</label><input is="emby-input" type="number" data-num="1" data-f="connection.timeoutSeconds" value="${esc(c.timeoutSeconds || '')}"/></div>
            </div>
            ${authFields('lhEd', c.auth, e.secrets)}` : ''}
            ${secretKeys.length ? `<h4>${esc(t('editor.secrets'))}</h4><div class="lh-row">${secretKeys.map(k => `<div class="inputContainer lh-grow"><label class="inputLabel">{secret.${esc(k)}}</label><input is="emby-input" type="password" data-secret="${esc(k)}" value="${esc(e.secrets[k] !== undefined ? e.secrets[k] : (d.secrets[k] || ''))}" autocomplete="off"/></div>`).join('')}</div>` : ''}
            <h4>${esc(t('editor.refresh'))}</h4>
            <div class="lh-row">
                <div class="inputContainer"><label class="inputLabel">${esc(t('field.refreshLive'))}</label><input is="emby-input" type="number" data-num="1" data-f="refresh.liveSeconds" value="${esc(r.liveSeconds || '')}" placeholder="45"/></div>
                <div class="inputContainer"><label class="inputLabel">${esc(t('field.refreshToday'))}</label><input is="emby-input" type="number" data-num="1" data-f="refresh.todaySeconds" value="${esc(r.todaySeconds || '')}" placeholder="300"/></div>
                <div class="inputContainer"><label class="inputLabel">${esc(t('field.refreshAll'))}</label><input is="emby-input" type="number" data-num="1" data-f="refresh.allSeconds" value="${esc(r.allSeconds || '')}" placeholder="1800"/></div>
            </div>
            <h4>JSON <span class="lh-muted">(${esc(t('editor.jsonHelp'))})</span></h4>
            <textarea class="lh-code" id="lhEdJson" spellcheck="false">${esc(JSON.stringify(d, null, 2))}</textarea>
            <div class="lh-row">
                <button is="emby-button" class="raised emby-button" id="lhEdApplyJson"><span>${esc(t('editor.applyJson'))}</span></button>
                <button is="emby-button" class="raised emby-button" id="lhEdValidate"><span>${esc(t('editor.validate'))}</span></button>
                <button is="emby-button" class="raised emby-button" id="lhEdPreview"><span>${esc(t('wizard.preview'))}</span></button>
                <button is="emby-button" class="raised button-submit emby-button" id="lhEdSave"><span>${esc(t('action.save'))}</span></button>
                <a is="emby-linkbutton" class="raised emby-button" target="_blank" href="${esc(ApiClient.getUrl('LiveHub/Admin/Schema'))}"><span>provider.schema.json</span></a>
            </div>
            <div id="lhEdResult"></div>
        </div>`;
        if (d.kind === 'rest') { bindAuthVisibility('lhEd'); }
        const refreshJson = () => { formToDef(); $('#lhEdJson').value = JSON.stringify(S.editor.def, null, 2); };
        body.querySelectorAll('[data-f], [data-secret]').forEach(el => el.addEventListener('change', refreshJson));
        // The auth section rewrites connection.auth only when the admin touches it (keeps custom {secret.x} references).
        body.querySelectorAll('#lhEdAuthType, #lhEdAuthFields input, #lhEdAuthFields select').forEach(el => el.addEventListener('change', () => { S.editor.authDirty = true; refreshJson(); }));
        $('#lhEdApplyJson').addEventListener('click', () => {
            try { S.editor.def = JSON.parse($('#lhEdJson').value); renderEditor(); } catch (err) { lh.toast(t('editor.invalidJson') + ': ' + err.message); }
        });
        $('#lhEdValidate').addEventListener('click', async () => {
            syncEditor();
            const res = await guarded(() => lh.api('POST', 'Admin/Validate', withSecrets(S.editor.def, S.editor.secrets)));
            if (res) { $('#lhEdResult').innerHTML = `<div class="lh-card">${res.Valid ? `<span class="lh-badge ok">${esc(t('editor.valid'))}</span>` : `<pre class="lh-pre">${esc(formatIssues(res.Errors))}</pre>`}${(res.Warnings || []).length ? `<pre class="lh-pre">${esc(formatIssues(res.Warnings))}</pre>` : ''}</div>`; }
        });
        $('#lhEdPreview').addEventListener('click', async () => {
            syncEditor();
            const p = await guarded(() => lh.api('POST', 'Admin/Wizard/Preview', { definition: withSecrets(S.editor.def, S.editor.secrets) }));
            if (p) { S.editor.preview = p; $('#lhEdResult').innerHTML = previewHtml(p); bindPreviewActions($('#lhEdResult'), S.editor.def, S.editor.secrets); }
        });
        $('#lhEdSave').addEventListener('click', async () => {
            syncEditor();
            await saveDefinition(S.editor.def, S.editor.secrets, () => { S.editor.isNew = false; renderEditor(); });
        });
    }

    function syncEditor() {
        // JSON edits win when the JSON was modified after the last form change.
        try {
            const fromJson = JSON.parse($('#lhEdJson').value);
            if (JSON.stringify(fromJson) !== JSON.stringify(S.editor.def)) { S.editor.def = fromJson; return; }
        } catch (e) { /* keep form state */ }
        formToDef();
    }

    function formToDef() {
        const d = S.editor.def;
        view.querySelectorAll('#lhEditorBody [data-f]').forEach(el => {
            const path = el.dataset.f.split('.');
            let target = d;
            for (let i = 0; i < path.length - 1; i++) { target[path[i]] = target[path[i]] || {}; target = target[path[i]]; }
            const key = path[path.length - 1];
            let value = el.type === 'checkbox' ? el.checked : el.value.trim();
            if (el.dataset.num && value !== '') { value = Number(value); }
            if (value === '' || value === null) { delete target[key]; } else { target[key] = value; }
        });
        if (d.kind === 'rest' && $('#lhEdAuthType') && S.editor.authDirty) {
            const a = readAuth('lhEd');
            d.connection = d.connection || {};
            if (a.auth) { d.connection.auth = a.auth; } else { delete d.connection.auth; }
            Object.entries(a.secrets).forEach(([k, v]) => { S.editor.secrets[k] = v; d.secrets = d.secrets || {}; if (d.secrets[k] === undefined) { d.secrets[k] = ''; } });
        }
        view.querySelectorAll('#lhEditorBody [data-secret]').forEach(el => { S.editor.secrets[el.dataset.secret] = el.value; });
        ['defaults', 'refresh', 'connection'].forEach(k => { if (d[k] && !Object.keys(d[k]).length) { delete d[k]; } });
    }

    // ------------------------------------------------------------------ settings
    async function loadConfig() {
        S.config = await ApiClient.getPluginConfiguration(PLUGIN_ID);
    }

    const SETTINGS = [
        ['settings.general', [['UiLanguage', 'select:auto,it,en'], ['DefaultLanguage', 'text'], ['FallbackLanguages', 'text'], ['DefaultTimeZone', 'text']]],
        ['settings.sync', [['LiveRefreshSeconds', 'number'], ['TodayRefreshSeconds', 'number'], ['FullRefreshSeconds', 'number'], ['DefaultEventDurationMinutes', 'number'], ['EndedGraceMinutes', 'number'], ['UpcomingDays', 'number'], ['EnableDeduplication', 'bool'], ['DedupWindowMinutes', 'number'], ['PopularCount', 'number']]],
        ['settings.livetv', [['EnableLiveTv', 'bool'], ['ChannelNumberBase247', 'number'], ['SlotChannelNumberBase', 'number'], ['MaxSlotsPerCategory', 'number'], ['Channel247FillerPrograms', 'bool'], ['GuideRefreshMinMinutes', 'number']]],
        ['settings.playback', [['FastStart', 'bool'], ['AnalyzeDurationMs', 'number'], ['MaxStreamsPerUser', 'number'], ['RelayRequestsPerMinute', 'number'], ['FailoverStallSeconds', 'number'], ['FailoverSegmentErrors', 'number'], ['RelayTokenHours', 'number'], ['RelayIdleSeconds', 'number'], ['PreResolveEnabled', 'bool'], ['PreResolveMinutes', 'number'], ['PreResolveTopPopular', 'number']]],
        ['settings.resolver', [['HeadlessResolverEnabled', 'bool'], ['HeadlessResolverUrl', 'text']]],
        ['settings.other', [['EnableNotifications', 'bool'], ['GenerateImages', 'bool'], ['IpCheckUrl', 'text']]]
    ];

    function renderSettings() {
        const c = S.config;
        if (!c) { return; }
        const field = ([key, type]) => {
            const label = esc(t('cfg.' + key));
            const help = t('cfg.' + key + '.help');
            const helpHtml = help && help !== 'cfg.' + key + '.help' ? `<div class="fieldDescription">${esc(help)}</div>` : '';
            if (type === 'bool') { return `<div class="checkboxContainer checkboxContainer-withDescription"><label class="emby-checkbox-label"><input type="checkbox" is="emby-checkbox" data-cfg="${key}" ${c[key] ? 'checked' : ''}/><span>${label}</span></label>${helpHtml}</div>`; }
            if (type.startsWith('select:')) { return `<div class="selectContainer"><label class="selectLabel">${label}</label><select is="emby-select" data-cfg="${key}">${type.slice(7).split(',').map(o => `<option value="${o}" ${c[key] === o ? 'selected' : ''}>${o}</option>`).join('')}</select>${helpHtml}</div>`; }
            return `<div class="inputContainer"><label class="inputLabel">${label}</label><input is="emby-input" type="${type === 'number' ? 'number' : 'text'}" data-cfg="${key}" data-type="${type}" value="${esc(c[key] ?? '')}"/>${helpHtml}</div>`;
        };
        $('#lhSettingsForm').innerHTML = SETTINGS.map(([title, fields]) => `<div class="lh-card"><h3>${esc(t(title))}</h3>${fields.map(field).join('')}</div>`).join('')
            + `<div class="lh-card"><h3>${esc(t('settings.durations'))}</h3><textarea class="lh-code lh-small-code" id="lhCfgDurations">${esc((c.CategoryDurations || []).map(r => r.Match + ' = ' + r.Minutes).join('\n'))}</textarea><div class="fieldDescription">${esc(t('settings.durationsHelp'))}</div></div>`
            + `<div class="lh-card"><h3>${esc(t('settings.regions'))}</h3><textarea class="lh-code lh-small-code" id="lhCfgRegions">${esc((c.RegionRules || []).map(r => r.Match + ' = ' + r.Country + (r.Language ? ',' + r.Language : '')).join('\n'))}</textarea><div class="fieldDescription">${esc(t('settings.regionsHelp'))}</div></div>`
            + `<button is="emby-button" type="submit" class="raised button-submit block emby-button"><span>${esc(t('action.save'))}</span></button>`;
        $('#lhSettingsForm').onsubmit = async ev => {
            ev.preventDefault();
            const cfg = await ApiClient.getPluginConfiguration(PLUGIN_ID);
            view.querySelectorAll('[data-cfg]').forEach(el => {
                const k = el.dataset.cfg;
                cfg[k] = el.type === 'checkbox' ? el.checked : (el.dataset.type === 'number' ? Number(el.value) : el.value);
            });
            cfg.CategoryDurations = $('#lhCfgDurations').value.split('\n').map(l => l.split('=')).filter(p => p.length === 2 && p[0].trim()).map(p => ({ Match: p[0].trim(), Minutes: Number(p[1].trim()) || 120 }));
            cfg.RegionRules = $('#lhCfgRegions').value.split('\n').map(l => l.split('=')).filter(p => p.length === 2 && p[0].trim()).map(p => { const [country, language] = p[1].split(','); return { Match: p[0].trim(), Country: (country || '').trim(), Language: (language || '').trim() }; });
            lh.loading(true);
            try {
                const result = await ApiClient.updatePluginConfiguration(PLUGIN_ID, cfg);
                S.config = cfg;
                Dashboard.processPluginConfigurationUpdateResult(result);
            } finally { lh.loading(false); }
        };
    }

    // ------------------------------------------------------------------ egress / VPN
    async function loadEgress() {
        const body = $('#lhEgressBody');
        let data;
        try { data = await lh.api('GET', 'Admin/Egress'); } catch (e) { body.innerHTML = `<div class="lh-card lh-muted">${esc(e.message)}</div>`; return; }
        S.egress = data.Profiles || [];
        const health = data.Health || {};
        body.innerHTML = `<div class="lh-card lh-muted">${esc(t('egress.intro'))}</div>
            ${S.egress.map((p, i) => {
                const h = health[p.Id] || {};
                return `<div class="lh-card" data-idx="${i}">
                <div class="lh-row"><b>${esc(p.Name || p.Id)}</b> ${p.Id === data.DefaultProfileId ? `<span class="lh-badge">${esc(t('egress.isDefault'))}</span>` : ''}
                    ${h.LastCheck ? (h.Healthy ? `<span class="lh-badge ok">${esc(h.PublicIp || 'OK')} ${esc(h.Country || '')}</span>` : `<span class="lh-badge err" title="${esc(h.LastError || '')}">${esc(t('egress.down'))}</span>`) : ''}</div>
                <div class="lh-row">
                    <div class="inputContainer lh-grow"><label class="inputLabel">${esc(t('field.id'))}</label><input is="emby-input" data-e="Id" value="${esc(p.Id)}" ${p.Id === 'direct' ? 'readonly' : ''}/></div>
                    <div class="inputContainer lh-grow"><label class="inputLabel">${esc(t('field.name'))}</label><input is="emby-input" data-e="Name" value="${esc(p.Name)}"/></div>
                    <div class="selectContainer lh-grow"><label class="selectLabel">${esc(t('egress.type'))}</label><select is="emby-select" data-e="Type" ${p.Id === 'direct' ? 'disabled' : ''}>${['Direct', 'Http', 'Https', 'Socks5'].map(x => `<option ${p.Type === x ? 'selected' : ''}>${x}</option>`).join('')}</select></div>
                </div>
                ${p.Id === 'direct' ? '' : `<div class="lh-row">
                    <div class="inputContainer lh-grow"><label class="inputLabel">Host</label><input is="emby-input" data-e="Host" value="${esc(p.Host)}" placeholder="gluetun"/></div>
                    <div class="inputContainer"><label class="inputLabel">Port</label><input is="emby-input" type="number" data-e="Port" value="${esc(p.Port || '')}" placeholder="8888"/></div>
                    <div class="inputContainer lh-grow"><label class="inputLabel">${esc(t('auth.username'))}</label><input is="emby-input" data-e="Username" value="${esc(p.Username)}"/></div>
                    <div class="inputContainer lh-grow"><label class="inputLabel">${esc(t('auth.password'))}</label><input is="emby-input" type="password" data-e="Password" value="${esc(p.Password)}" autocomplete="off"/></div>
                </div>
                <div class="lh-row">
                    <div class="inputContainer"><label class="inputLabel">${esc(t('egress.expectedCountry'))}</label><input is="emby-input" data-e="ExpectedCountry" value="${esc(p.ExpectedCountry)}" placeholder="IT"/></div>
                    <label class="emby-checkbox-label"><input type="checkbox" is="emby-checkbox" data-e="KillSwitch" ${p.KillSwitch ? 'checked' : ''}/><span>${esc(t('egress.killSwitch'))}</span></label>
                    <div class="inputContainer lh-grow"><label class="inputLabel">${esc(t('egress.gluetunUrl'))}</label><input is="emby-input" data-e="GluetunControlUrl" value="${esc(p.GluetunControlUrl)}" placeholder="http://gluetun:8000"/></div>
                    <div class="inputContainer lh-grow"><label class="inputLabel">${esc(t('egress.gluetunKey'))}</label><input is="emby-input" type="password" data-e="GluetunApiKey" value="${esc(p.GluetunApiKey)}" autocomplete="off"/></div>
                </div>`}
                <div class="lh-row">
                    <button is="emby-button" class="emby-button raised" data-eact="check">${esc(t('egress.check'))}</button>
                    ${p.GluetunControlUrl ? `<button is="emby-button" class="emby-button raised" data-eact="gluetun">${esc(t('egress.gluetunStatus'))}</button>` : ''}
                    <button is="emby-button" class="emby-button raised" data-eact="default">${esc(t('egress.makeDefault'))}</button>
                    ${p.Id === 'direct' ? '' : `<button is="emby-button" class="emby-button raised" data-eact="remove">${esc(t('action.delete'))}</button>`}
                </div><div data-eout></div></div>`;
            }).join('')}
            <div class="lh-card"><h4>${esc(t('egress.regions'))}</h4><textarea class="lh-code lh-small-code" id="lhEgRegions">${esc((data.RegionEgress || []).map(r => r.Country + ' = ' + r.ProfileId).join('\n'))}</textarea><div class="fieldDescription">${esc(t('egress.regionsHelp'))}</div></div>
            <div class="lh-row">
                <button is="emby-button" class="raised emby-button" id="lhEgAdd"><span>${esc(t('egress.add'))}</span></button>
                <button is="emby-button" class="raised button-submit emby-button" id="lhEgSave"><span>${esc(t('action.save'))}</span></button>
            </div>`;
        S.egressDefault = data.DefaultProfileId;
        body.querySelectorAll('[data-eact]').forEach(b => b.addEventListener('click', () => egressAction(b.closest('[data-idx]'), b.dataset.eact)));
        $('#lhEgAdd').addEventListener('click', () => { readEgressForm(); S.egress.push({ Id: 'vpn' + (S.egress.length), Name: 'VPN', Type: 'Http', Host: 'gluetun', Port: 8888, KillSwitch: true, Username: '', Password: '', ExpectedCountry: '', GluetunControlUrl: 'http://gluetun:8000', GluetunApiKey: '' }); saveEgress(true); });
        $('#lhEgSave').addEventListener('click', () => saveEgress(false));
    }

    function readEgressForm() {
        view.querySelectorAll('#lhEgressBody [data-idx]').forEach(card => {
            const p = S.egress[+card.dataset.idx];
            card.querySelectorAll('[data-e]').forEach(el => { p[el.dataset.e] = el.type === 'checkbox' ? el.checked : (el.type === 'number' ? Number(el.value) : el.value); });
        });
    }

    async function saveEgress(reload) {
        readEgressForm();
        const regions = $('#lhEgRegions') ? $('#lhEgRegions').value.split('\n').map(l => l.split('=')).filter(p => p.length === 2).map(p => ({ Country: p[0].trim().toUpperCase(), ProfileId: p[1].trim() })) : [];
        const res = await guarded(() => lh.api('POST', 'Admin/Egress', { Profiles: S.egress, DefaultProfileId: S.egressDefault, RegionEgress: regions }));
        if (res !== null) { if (!reload) { lh.toast(t('editor.saved')); } await loadConfig(); await loadEgress(); }
    }

    async function egressAction(card, act) {
        const p = S.egress[+card.dataset.idx];
        const out = card.querySelector('[data-eout]');
        if (act === 'remove') { S.egress.splice(+card.dataset.idx, 1); await saveEgress(true); return; }
        if (act === 'default') { readEgressForm(); S.egressDefault = p.Id; await saveEgress(false); return; }
        await saveEgress(true);
        if (act === 'check') {
            out.innerHTML = `<div class="lh-muted">${esc(t('wizard.testing'))}</div>`;
            const r = await guarded(() => lh.api('POST', 'Admin/Egress/' + encodeURIComponent(p.Id) + '/Check'));
            const fresh = view.querySelector(`#lhEgressBody [data-idx="${S.egress.findIndex(x => x.Id === p.Id)}"] [data-eout]`);
            if (r && fresh) {
                fresh.innerHTML = `<div class="lh-card">${r.Ok ? `<span class="lh-badge ok">OK</span>` : `<span class="lh-badge err">${esc(t('egress.down'))}</span>`}
                    IP: <b>${esc(r.Ip || '—')}</b> · ${esc(t('field.country'))}: <b>${esc(r.Country || '—')}</b> ${r.Expected ? (r.CountryMatches ? '✓' : `<span class="lh-badge warn">≠ ${esc(r.Expected)}</span>`) : ''} · ${esc(Math.round(r.LatencyMs || 0))} ms
                    ${r.Error ? `<div class="lh-muted">${esc(r.Error)}</div>` : ''}</div>`;
            }
        }
        if (act === 'gluetun') {
            const r = await guarded(() => lh.api('GET', 'Admin/Egress/' + encodeURIComponent(p.Id) + '/Gluetun'));
            const fresh = view.querySelector(`#lhEgressBody [data-idx="${S.egress.findIndex(x => x.Id === p.Id)}"] [data-eout]`);
            if (r && fresh) {
                fresh.innerHTML = `<div class="lh-card">${esc(t('egress.tunnel'))}: <b>${esc(r.Status || '?')}</b> · IP <b>${esc(r.PublicIp || '—')}</b> · ${esc(r.Country || '')} ${esc(r.City || '')}
                    ${r.CanChangeCountry ? `<div class="lh-row"><input is="emby-input" id="lhGlCountry" placeholder="Italy"/><button is="emby-button" class="emby-button raised" id="lhGlSet">${esc(t('egress.changeCountry'))}</button></div>` : `<div class="lh-muted">${esc(r.Instructions || '')}</div>`}
                    ${r.Error ? `<div class="lh-muted">${esc(r.Error)}</div>` : ''}</div>`;
                const set = fresh.querySelector('#lhGlSet');
                if (set) { set.addEventListener('click', async () => { const res = await guarded(() => lh.api('POST', 'Admin/Egress/' + encodeURIComponent(p.Id) + '/Gluetun/Country', { Country: fresh.querySelector('#lhGlCountry').value })); if (res) { lh.toast(res.Message || 'OK'); } }); }
            }
        }
    }

    // ------------------------------------------------------------------ users
    async function loadUsers() {
        const body = $('#lhUsersBody');
        let users;
        try { users = await lh.api('GET', 'Admin/Users'); } catch (e) { body.innerHTML = `<div class="lh-card lh-muted">${esc(e.message)}</div>`; return; }
        S.users = users;
        body.innerHTML = `<div class="lh-card lh-muted">${esc(t('users.intro'))}</div>` + users.map((u, i) => `<div class="lh-card" data-uidx="${i}">
            <label class="emby-checkbox-label"><input type="checkbox" is="emby-checkbox" data-u="Restricted" ${u.Restricted ? 'checked' : ''}/><span><b>${esc(u.Name)}</b> — ${esc(t('users.restrict'))}</span></label>
            <div class="lh-chips">${S.providers.map(p => `<label><input type="checkbox" data-uprov="${esc(p.id)}" ${(u.AllowedProviders || []).includes(p.id) ? 'checked' : ''}/> ${esc(p.name)}</label>`).join('')}</div></div>`).join('')
            + `<button is="emby-button" class="raised button-submit emby-button" id="lhUsersSave"><span>${esc(t('action.save'))}</span></button>`;
        $('#lhUsersSave').addEventListener('click', async () => {
            view.querySelectorAll('#lhUsersBody [data-uidx]').forEach(card => {
                const u = S.users[+card.dataset.uidx];
                u.Restricted = card.querySelector('[data-u="Restricted"]').checked;
                u.AllowedProviders = [...card.querySelectorAll('[data-uprov]')].filter(x => x.checked).map(x => x.dataset.uprov);
            });
            const res = await guarded(() => lh.api('POST', 'Admin/Users', S.users, { noJson: true }));
            if (res !== null) { lh.toast(t('editor.saved')); }
        });
    }

    // ------------------------------------------------------------------ diagnostics
    async function loadDiagnostics() {
        const body = $('#lhDiagBody');
        let d;
        try { d = await lh.api('GET', 'Admin/Diagnostics'); } catch (e) { body.innerHTML = `<div class="lh-card lh-muted">${esc(e.message)}</div>`; return; }
        const c = d.Catalog || {};
        body.innerHTML = `<div class="lh-card"><h3>${esc(t('diag.catalog'))}</h3>
                <span class="lh-badge">${esc(t('count.events'))}: ${esc(c.Events)}</span> <span class="lh-badge live">LIVE: ${esc(c.Live)}</span>
                <span class="lh-badge">${esc(t('count.channels'))}: ${esc(c.Channels)}</span> <span class="lh-badge">${esc(t('count.programs'))}: ${esc(c.Programs)}</span>
                <span class="lh-badge">rev ${esc(c.Revision)}</span> ${c.LastChange ? `<span class="lh-muted">${esc(lh.fmtDate(c.LastChange))}</span>` : ''}</div>
            <div class="lh-card" style="overflow:auto"><h3>${esc(t('diag.providers'))}</h3><table class="lh-table"><thead><tr>
                <th>${esc(t('field.name'))}</th><th>${esc(t('diag.lastSync'))}</th><th>${esc(t('diag.lastOk'))}</th><th>${esc(t('diag.errors'))}</th><th>${esc(t('diag.latency'))}</th><th>${esc(t('providers.content'))}</th><th>${esc(t('field.egress'))}</th><th>Circuit</th></tr></thead><tbody>
                ${(d.Providers || []).map(p => `<tr><td>${esc(p.ProviderId)}${p.FromSnapshot ? ' <span class="lh-badge warn">snapshot</span>' : ''}</td>
                    <td>${esc(lh.fmtDate(p.LastSyncAt))}</td><td>${esc(lh.fmtDate(p.LastSuccessAt))}</td>
                    <td>${p.ConsecutiveFailures ? `<span class="lh-badge err">${esc(p.ConsecutiveFailures)}</span> ${esc(p.LastError || '')}` : esc(p.Failures + '/' + p.Requests)}</td>
                    <td>${(p.Endpoints || []).map(e => esc(e.Endpoint + ': ' + Math.round(e.AvgMs) + ' ms')).join('<br/>')}</td>
                    <td>${esc(p.Events)} / ${esc(p.Channels)} / ${esc(p.Streams)} · 304: ${esc(p.NotModified)}</td>
                    <td>${esc(p.EgressProfile)}</td><td>${esc(p.Circuit)}</td></tr>`).join('')}</tbody></table></div>
            <div class="lh-card" style="overflow:auto"><h3>${esc(t('diag.streams'))}</h3>${(d.Streams || []).length ? `<table class="lh-table"><thead><tr><th>${esc(t('field.title'))}</th><th>${esc(t('diag.user'))}</th><th>${esc(t('diag.origin'))}</th><th>${esc(t('diag.since'))}</th><th>MB</th><th>Failover</th><th>${esc(t('diag.consumers'))}</th></tr></thead><tbody>
                ${d.Streams.map(s => `<tr><td>${esc(s.Title)}</td><td>${esc(s.User || '')}</td><td>${esc(s.Origin)}</td><td>${esc(lh.fmtDate(s.StartedAt))}</td><td>${esc((s.Bytes / 1048576).toFixed(1))}</td><td>${esc(s.Failovers)}</td><td>${esc(s.Consumers)}</td></tr>`).join('')}</tbody></table>` : `<div class="lh-muted">${esc(t('diag.noStreams'))}</div>`}</div>
            <div class="lh-card"><h3>${esc(t('diag.egress'))}</h3>${Object.entries(d.Egress || {}).map(([id, h]) => `<div><b>${esc(id)}</b> ${h.Healthy ? '<span class="lh-badge ok">OK</span>' : '<span class="lh-badge err">' + esc(t('egress.down')) + '</span>'} ${esc(h.PublicIp || '')} ${esc(h.Country || '')} ${esc(h.LastError || '')}</div>`).join('')}</div>`;
    }

    view.addEventListener('viewshow', () => { init().catch(e => { console.error('LiveHub', e); }); });
}
