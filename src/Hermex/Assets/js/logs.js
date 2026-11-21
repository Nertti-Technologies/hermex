/* Hermex dashboard — Logs page. */
(function () {
    'use strict';
    var M = window.Hermex;

    var LEVEL_KEYS = ['debug', 'info', 'warning', 'error'];
    var LEVEL_NAMES = ['Debug', 'Info', 'Warning', 'Error'];
    var MAX_ENTRIES = 500;

    var state = { minLevel: 0, entries: [] };
    var el = {};

    function cache() {
        el.body = document.getElementById('m4d-log-body');
        el.empty = document.getElementById('m4d-log-empty');
        el.level = document.getElementById('m4d-log-level');
        el.refresh = document.getElementById('m4d-log-refresh');
        el.clear = document.getElementById('m4d-log-clear');
    }

    async function loadLogs() {
        try {
            var logs = await M.api('/logs?limit=' + MAX_ENTRIES);
            state.entries = logs || [];
            render();
        } catch (e) {
            M.toast('Could not load logs.', 'error');
        }
    }

    function render() {
        var filtered = state.entries.filter(function (l) {
            return (l.level || 0) >= state.minLevel;
        });
        el.empty.hidden = filtered.length > 0;
        el.body.innerHTML = filtered.map(rowHtml).join('');
    }

    function rowHtml(l) {
        var level = Number(l.level) || 0;
        var key = LEVEL_KEYS[level] || 'info';
        var name = LEVEL_NAMES[level] || 'Info';
        return '<tr>' +
            '<td class="m4d-mono">' + M.escapeHtml(M.formatDate(l.timestampUtc)) + '</td>' +
            '<td><span class="m4d-level m4d-level--' + key + '">' + name + '</span></td>' +
            '<td>' + M.escapeHtml(l.category || '') + '</td>' +
            '<td>' + M.escapeHtml(l.message || '') + '</td>' +
            '<td class="m4d-mono">' + M.escapeHtml(l.sessionId || '—') + '</td>' +
            '</tr>';
    }

    async function clearLogs() {
        if (!window.confirm('Delete every diagnostic log entry?')) return;
        try {
            await M.api('/logs', { method: 'DELETE' });
            state.entries = [];
            render();
            M.toast('Logs cleared.');
        } catch (e) {
            M.toast('Could not clear the logs.', 'error');
        }
    }

    /* ---------- webhook deliveries (ADR-0003 D4) ---------- */
    var OUTCOME_LEVEL = { Delivered: 'info', Failed: 'warning', Blocked: 'error', Disabled: 'error', Dropped: 'error', Skipped: 'warning', Vetoed: 'warning' };

    async function loadWebhooks() {
        var section = document.getElementById('m4d-webhooks');
        var w;
        try { w = await M.api('/webhooks/deliveries'); } catch (e) { return; }
        if (!w || !w.enabled) { section.hidden = true; return; }
        section.hidden = false;

        var counts = [];
        if (w.dropped) counts.push(w.dropped + ' dropped (queue full)');
        if (w.skipped) counts.push(w.skipped + ' skipped (endpoint paused or disabled)');
        document.getElementById('m4d-webhooks-dropped').textContent = counts.join(' · ');
        document.getElementById('m4d-webhooks-endpoints').innerHTML = w.endpoints.map(function (e) {
            var active = e.state === 'Active';
            return '<div class="m4d-webhooks__endpoint"><span class="m4d-level m4d-level--' + (active ? 'info' : 'error') + '">' +
                M.escapeHtml(e.state) + '</span><span class="m4d-mono">#' + e.index + ' ' + M.escapeHtml(e.target) + '</span>' +
                (e.consecutiveFailures ? '<span class="m4d-webhooks__fails">' + e.consecutiveFailures + ' failure(s) in a row</span>' : '') +
                (active ? '' : '<button type="button" class="m4d-btn m4d-btn--sm" data-resume="' + e.index + '">Resume</button>') +
                '</div>';
        }).join('');
        document.querySelectorAll('[data-resume]').forEach(function (b) {
            b.addEventListener('click', async function () {
                try {
                    await M.api('/webhooks/' + Number(b.dataset.resume) + '/resume', { method: 'POST' });
                    M.toast('Endpoint resumed.');
                    loadWebhooks();
                } catch (e) { M.toast('Could not resume the endpoint.', 'error'); }
            });
        });

        document.getElementById('m4d-webhooks-body').innerHTML = w.deliveries.length ? w.deliveries.map(function (d) {
            return '<tr><td class="m4d-mono">' + M.escapeHtml(M.formatDate(d.atUtc)) + '</td>' +
                '<td><span class="m4d-level m4d-level--' + (OUTCOME_LEVEL[d.outcome] || 'info') + '">' + M.escapeHtml(d.outcome) + '</span></td>' +
                '<td class="m4d-mono">#' + d.endpoint + '</td><td class="m4d-mono">' + d.messageId + '</td>' +
                '<td>' + d.attempt + '</td><td>' + (d.statusCode == null ? '—' : d.statusCode) + '</td>' +
                '<td>' + d.durationMs + ' ms</td><td>' + M.escapeHtml(d.error || '') + '</td></tr>';
        }).join('') : '<tr><td colspan="8" class="m4d-empty__hint">No deliveries yet.</td></tr>';
    }

    M.ready(function () {
        cache();

        el.level.addEventListener('change', function () {
            state.minLevel = Number(el.level.value) || 0;
            render();
        });
        el.refresh.addEventListener('click', function () { loadLogs(); loadWebhooks(); });
        el.clear.addEventListener('click', clearLogs);

        M.onHub('logAdded', function (entry) {
            if (!entry) return;
            state.entries.unshift(entry);
            if (state.entries.length > MAX_ENTRIES) state.entries.pop();
            render();
        });
        M.onHub('poll', function () { loadLogs(); loadWebhooks(); });

        loadLogs();
        loadWebhooks();
        // Deliveries have no hub event; refresh while the panel is shown and the tab is visible.
        // 5 s (estimate) keeps state changes such as a pause visible without load.
        setInterval(function () {
            if (!document.getElementById('m4d-webhooks').hidden && document.visibilityState === 'visible') loadWebhooks();
        }, 5000);
    });
})();
