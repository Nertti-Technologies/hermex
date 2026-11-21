/* Hermex dashboard — Settings & status page. */
(function () {
    'use strict';
    var M = window.Hermex;

    /* ---------- helpers ---------- */
    function set(id, value) {
        var node = document.getElementById(id);
        if (node) node.textContent = (value == null || value === '') ? '—' : String(value);
    }
    function val(id, value) {
        var node = document.getElementById(id);
        if (node) node.value = value == null ? '' : String(value);
    }
    function check(id, value) {
        var node = document.getElementById(id);
        if (node) node.checked = !!value;
    }
    function readVal(id) { var n = document.getElementById(id); return n ? n.value : ''; }
    function readInt(id) { return parseInt(readVal(id), 10) || 0; }
    function readNum(id) { return parseFloat(readVal(id)) || 0; }
    function readBool(id) { var n = document.getElementById(id); return n ? n.checked : false; }

    function formatUptime(seconds) {
        seconds = Number(seconds) || 0;
        if (seconds < 60) return seconds + ' sec';
        var d = Math.floor(seconds / 86400);
        var h = Math.floor((seconds % 86400) / 3600);
        var m = Math.floor((seconds % 3600) / 60);
        var parts = [];
        if (d) parts.push(d + 'd');
        if (h) parts.push(h + 'h');
        parts.push(m + 'm');
        return parts.join(' ');
    }

    /* ---------- live status ---------- */
    function applyStatus(status) {
        if (!status) return;
        var srv = status.server || {};
        var stats = status.stats || {};

        set('m4d-set-endpoint', (srv.listenAddress || '—') + '  :  ' + (srv.listeningPort || srv.configuredPort || '—'));
        set('m4d-set-imap', srv.imapEnabled
            ? (srv.listenAddress || '—') + '  :  ' + (srv.imapListeningPort || '—') + '  (' + (srv.imapStatus || '') + ')'
            : 'Disabled');
        set('m4d-set-host', srv.host);
        set('m4d-set-uptime', srv.startedAtUtc ? formatUptime(srv.uptimeSeconds) : 'Not running');
        set('m4d-set-active', srv.activeConnections);
        set('m4d-set-received', srv.messagesReceived);
        set('m4d-set-rejected', srv.messagesRejected);
        set('m4d-set-accepted', srv.acceptedConnections);

        set('m4d-set-total', (stats.totalMessages || 0).toLocaleString());
        set('m4d-set-unread', (stats.unreadMessages || 0).toLocaleString());
        set('m4d-set-storesize', M.formatBytes(stats.totalSizeBytes || 0));
        set('m4d-set-last', stats.lastReceivedUtc ? M.formatDate(stats.lastReceivedUtc) : 'Never');
        set('m4d-set-db', srv.databasePath);

        var pill = document.getElementById('m4d-settings-status');
        if (pill) {
            var kind = 'ok';
            var label = 'Listening';
            if (srv.status === 'PortConflict') { kind = 'error'; label = 'Port conflict'; }
            else if (srv.status === 'Faulted') { kind = 'error'; label = 'Faulted'; }
            else if (srv.status !== 'Listening') { kind = 'warn'; label = srv.status || 'Unknown'; }
            pill.className = 'm4d-status m4d-status--' + kind;
            var lbl = pill.querySelector('.m4d-status__label');
            if (lbl) lbl.textContent = label;
        }

        var error = document.getElementById('m4d-set-error');
        if (error) {
            if (srv.lastError) { error.hidden = false; error.textContent = srv.lastError; }
            else { error.hidden = true; }
        }
    }

    /* ---------- settings form ---------- */
    async function loadSettings() {
        try {
            var s = await M.api('/settings');
            val('set-smtpPort', s.smtpPort);
            val('set-imapPort', s.imapPort);
            check('set-enableImap', s.enableImap);
            val('set-serverHostName', s.serverHostName);
            val('set-maxMessageSizeMb', Math.round((s.maxMessageSizeBytes || 0) / 1048576));
            check('set-requireAuthentication', s.requireAuthentication);
            check('set-captureSessionTranscript', s.captureSessionTranscript);
            val('set-retentionMaxMessages', s.retentionMaxMessages);
            val('set-retentionMaxAgeHours', s.retentionMaxAgeHours);
            val('set-dashboardTitle', s.dashboardTitle);
            val('set-defaultTheme', s.defaultTheme || 'System');
            check('set-relayEnabled', s.relayEnabled);
            check('set-relayAutomatic', s.relayAutomatic);
            val('set-relayHost', s.relayHost);
            val('set-relayPort', s.relayPort);
        } catch (e) {
            M.toast('Could not load settings.', 'error');
        }
    }

    function gather() {
        return {
            smtpPort: readInt('set-smtpPort'),
            imapPort: readInt('set-imapPort'),
            enableImap: readBool('set-enableImap'),
            serverHostName: readVal('set-serverHostName').trim(),
            maxMessageSizeBytes: Math.round(readNum('set-maxMessageSizeMb') * 1048576),
            requireAuthentication: readBool('set-requireAuthentication'),
            captureSessionTranscript: readBool('set-captureSessionTranscript'),
            retentionMaxMessages: readInt('set-retentionMaxMessages'),
            retentionMaxAgeHours: readNum('set-retentionMaxAgeHours'),
            dashboardTitle: readVal('set-dashboardTitle').trim(),
            defaultTheme: readVal('set-defaultTheme'),
            relayEnabled: readBool('set-relayEnabled'),
            relayAutomatic: readBool('set-relayAutomatic'),
            relayHost: readVal('set-relayHost').trim(),
            relayPort: readInt('set-relayPort')
        };
    }

    async function saveSettings() {
        var button = document.getElementById('m4d-settings-save');
        var result = document.getElementById('m4d-settings-result');
        button.disabled = true;
        result.className = 'm4d-form__status';
        result.textContent = 'Saving…';
        try {
            var response = await M.api('/settings', {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(gather())
            });
            result.className = 'm4d-form__status m4d-form__status--ok';
            result.textContent = response && response.listenerRestarted
                ? 'Saved — listeners are re-binding.'
                : 'Settings saved.';
            M.toast('Settings saved.', 'success');
            setTimeout(M.refreshStatus, 600);
        } catch (e) {
            result.className = 'm4d-form__status m4d-form__status--error';
            result.textContent = 'Could not save: ' + e.message;
        } finally {
            button.disabled = false;
        }
    }

    /* ---------- wiring ---------- */
    /* ---------- SMTP faults (ADR-0003 D11) ---------- */
    var STAGES = ['Connect', 'Helo', 'Auth', 'MailFrom', 'RcptTo', 'Data'];
    var STAGE_LABELS = { Connect: 'Connect', Helo: 'HELO/EHLO', Auth: 'AUTH', MailFrom: 'MAIL FROM', RcptTo: 'RCPT TO', Data: 'End of DATA' };
    var REPLIES = ['', 'drop', '421', '450', '451', '452', '550', '552', '553', '554'];
    var REPLY_LABELS = { '': 'None (delay only)', drop: 'Drop connection' };

    // TimeSpan as System.Text.Json writes it: [d.]hh:mm:ss[.fffffff]
    function spanToMs(span) {
        if (!span) return '';
        var m = /^(?:(\d+)\.)?(\d+):(\d+):(\d+)(?:\.(\d+))?$/.exec(span);
        if (!m) return '';
        var frac = m[5] ? Number(('0.' + m[5])) : 0;
        return Math.round(((((Number(m[1] || 0) * 24 + Number(m[2])) * 60 + Number(m[3])) * 60 + Number(m[4]) + frac) * 1000));
    }
    function msToSpan(ms) {
        ms = Math.max(0, Math.floor(ms));
        function pad(n, w) { n = String(n); while (n.length < w) n = '0' + n; return n; }
        return pad(Math.floor(ms / 3600000), 2) + ':' + pad(Math.floor(ms / 60000) % 60, 2) + ':' +
            pad(Math.floor(ms / 1000) % 60, 2) + '.' + pad(ms % 1000, 3);
    }

    function option(value, label, selected) {
        var o = document.createElement('option');
        o.value = value;
        o.textContent = label;
        o.selected = selected;
        return o;
    }

    function cell(row, child) {
        var td = document.createElement('td');
        td.appendChild(child);
        row.appendChild(td);
        return child;
    }

    function input(type, value, label, attrs) {
        var i = document.createElement('input');
        i.type = type;
        i.className = 'm4d-input';
        i.value = value == null ? '' : String(value);
        i.setAttribute('aria-label', label);
        Object.keys(attrs || {}).forEach(function (k) { i.setAttribute(k, attrs[k]); });
        return i;
    }

    function addRuleRow(rule) {
        rule = rule || { stage: 'Data', replyCode: 451, probability: 100 };
        var row = document.createElement('tr');

        var stage = cell(row, document.createElement('select'));
        stage.className = 'm4d-input';
        stage.setAttribute('aria-label', 'Step');
        STAGES.forEach(function (s) { stage.appendChild(option(s, STAGE_LABELS[s], s === rule.stage)); });

        var match = cell(row, input('text', rule.match, 'Address pattern', { placeholder: 'any, or *@bounce.test', maxlength: '320' }));
        function syncMatch() {
            var applies = stage.value === 'MailFrom' || stage.value === 'RcptTo';
            match.disabled = !applies;
            if (!applies) match.value = '';
        }
        stage.addEventListener('change', syncMatch);
        syncMatch();

        var reply = cell(row, document.createElement('select'));
        reply.className = 'm4d-input';
        reply.setAttribute('aria-label', 'Reply');
        var current = rule.drop ? 'drop' : (rule.replyCode ? String(rule.replyCode) : '');
        var replies = REPLIES.indexOf(current) < 0 ? REPLIES.concat([current]) : REPLIES; // per row: never grow the shared list
        replies.forEach(function (r) { reply.appendChild(option(r, REPLY_LABELS[r] || r, r === current)); });

        cell(row, input('number', spanToMs(rule.delay), 'Delay in milliseconds', { min: '0', step: '100' }));
        cell(row, input('number', rule.probability == null ? 100 : rule.probability, 'Chance in percent', { min: '0', max: '100' }));
        cell(row, input('number', rule.everyNth, 'Every Nth match', { min: '1', placeholder: '—' }));

        var remove = document.createElement('button');
        remove.type = 'button';
        remove.className = 'm4d-icon-btn';
        remove.title = 'Remove rule';
        remove.setAttribute('aria-label', 'Remove rule');
        remove.innerHTML = '<svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" aria-hidden="true"><path d="M18 6 6 18M6 6l12 12" /></svg>';
        remove.addEventListener('click', function () { row.remove(); });
        cell(row, remove);

        document.getElementById('m4d-faults-rules').appendChild(row);
    }

    function readRules() {
        return Array.prototype.map.call(document.querySelectorAll('#m4d-faults-rules tr'), function (row) {
            var f = row.querySelectorAll('select, input');
            var reply = f[2].value;
            var delay = f[3].value === '' ? null : Number(f[3].value);
            return {
                stage: f[0].value,
                match: f[1].value.trim() || null,
                drop: reply === 'drop',
                replyCode: reply && reply !== 'drop' ? Number(reply) : null,
                delay: delay == null || delay === 0 ? null : msToSpan(delay),
                probability: f[4].value === '' ? 100 : Number(f[4].value),
                everyNth: f[5].value === '' ? null : Number(f[5].value)
            };
        });
    }

    function applyFaults(state) {
        var section = document.getElementById('m4d-faults');
        section.hidden = false;
        document.getElementById('m4d-faults-enabled').checked = !!state.enabled;
        var badge = document.getElementById('m4d-faults-state');
        badge.textContent = state.enabled ? 'Active' : 'Off';
        badge.className = 'm4d-level m4d-level--' + (state.enabled ? 'warning' : 'info');
        document.getElementById('m4d-faults-header-off').hidden = !!state.allowHeaderTrigger;

        var note = document.getElementById('m4d-faults-note');
        var reason = !state.allowedHere
            ? 'Fault injection is available only in the Development environment unless the host sets Faults.AllowOutsideDevelopment.'
            : !state.runtimeEditable ? 'The host does not allow settings to be changed at runtime; these are the configured rules.' : '';
        note.textContent = reason;
        note.hidden = !reason;
        document.getElementById('m4d-faults-fields').disabled = !!reason;

        document.getElementById('m4d-faults-rules').innerHTML = '';
        (state.rules || []).forEach(addRuleRow);
    }

    async function loadFaults() {
        try { applyFaults(await M.api('/faults')); } catch (e) { /* not an Admin: the panel stays hidden */ }
    }

    async function saveFaults() {
        var result = document.getElementById('m4d-faults-result');
        try {
            var state = await M.api('/faults', {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ enabled: document.getElementById('m4d-faults-enabled').checked, rules: readRules() })
            });
            applyFaults(state);
            result.textContent = '';
            M.toast(state.enabled ? 'Faults applied.' : 'Fault injection is off.', 'success');
        } catch (e) {
            var message = e.message;
            var json = message.indexOf('{') >= 0 ? message.slice(message.indexOf('{')) : '';
            try { message = JSON.parse(json).error || JSON.parse(json).detail || message; } catch (x) { /* keep raw */ }
            result.textContent = message;
        }
    }

    /* ---------- reset ---------- */
    // The page renders ResetRequest.ConfirmationText (HermexApiEndpoints) into data-confirm-word.
    function resetWord() {
        var input = document.getElementById('m4d-reset-confirm');
        return input ? input.dataset.confirmWord : null;
    }

    async function resetHermex() {
        var input = document.getElementById('m4d-reset-confirm');
        var button = document.getElementById('m4d-reset');
        var result = document.getElementById('m4d-reset-result');
        var word = resetWord();
        if (!word || input.value !== word) return;
        button.disabled = true;
        try {
            var r = await M.api('/reset', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ confirm: word })
            });
            input.value = '';
            result.textContent = 'Deleted ' + r.messagesDeleted + ' message(s), ' + r.mailboxesDeleted +
                ' mailbox(es) and ' + r.logsDeleted + ' log entr' + (r.logsDeleted === 1 ? 'y' : 'ies') + '.';
            M.toast('Hermex was reset.', 'success');
            M.refreshStatus();
        } catch (e) {
            result.textContent = '';
            M.toast('Reset failed: ' + e.message, 'error');
            button.disabled = input.value !== word;
        }
    }

    M.ready(function () {
        var resetInput = document.getElementById('m4d-reset-confirm');
        var resetButton = document.getElementById('m4d-reset');
        if (resetInput && resetButton) {
            resetInput.addEventListener('input', function () { resetButton.disabled = resetInput.value !== resetWord(); });
            resetButton.addEventListener('click', resetHermex);
        }

        // Save/Reset are not rendered when the host has locked runtime settings.
        var save = document.getElementById('m4d-settings-save');
        var reload = document.getElementById('m4d-settings-reload');
        if (save) save.addEventListener('click', saveSettings);
        if (reload) reload.addEventListener('click', loadSettings);

        var addRule = document.getElementById('m4d-faults-add');
        if (addRule) addRule.addEventListener('click', function () { addRuleRow(); });
        var saveFaultsButton = document.getElementById('m4d-faults-save');
        if (saveFaultsButton) saveFaultsButton.addEventListener('click', saveFaults);
        loadFaults();

        M.onHub('status', applyStatus);
        M.onHub('poll', function () { M.refreshStatus(); });

        loadSettings();
        M.refreshStatus();
        setInterval(function () { M.refreshStatus(); }, 5000);
    });
})();
