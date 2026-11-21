/* Hermex dashboard — Inbox page. */
(function () {
    'use strict';
    var M = window.Hermex;

    var ICON_CLIP = '<svg viewBox="0 0 24 24" width="11" height="11" fill="none" stroke="currentColor" stroke-width="2.4" stroke-linecap="round" stroke-linejoin="round"><path d="M21.44 11.05 12.25 20.24a6 6 0 0 1-8.49-8.49l9.19-9.19a4 4 0 0 1 5.66 5.66l-9.2 9.19a2 2 0 0 1-2.83-2.83l8.49-8.48"/></svg>';
    var ICON_DOWNLOAD = '<svg viewBox="0 0 24 24" width="15" height="15" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4"/><path d="m7 10 5 5 5-5"/><path d="M12 15V3"/></svg>';
    function icon(paths, size) {
        return '<svg viewBox="0 0 24 24" width="' + size + '" height="' + size + '" fill="none" stroke="currentColor" ' +
            'stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">' + paths + '</svg>';
    }
    var PATH_TRASH = '<path d="M3 6h18"/><path d="M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6"/><path d="M8 6V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2"/>';
    var PATH_MAIL_OPEN = '<path d="M21.2 8.4c.5.38.8.97.8 1.6v10a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V10a2 2 0 0 1 .8-1.6l8-6a2 2 0 0 1 2.4 0l8 6Z"/><path d="m22 10-8.97 5.7a1.94 1.94 0 0 1-2.06 0L2 10"/>';
    var PATH_MAIL = '<rect x="2" y="4" width="20" height="16" rx="2"/><path d="m22 7-8.97 5.7a1.94 1.94 0 0 1-2.06 0L2 7"/>';
    var ICON_TRASH = icon(PATH_TRASH, 15);
    var ICON_FILE = '<svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z"/><path d="M14 2v6h6"/></svg>';
    var ICON_RELAY = '<svg viewBox="0 0 24 24" width="15" height="15" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M22 2 11 13"/><path d="M22 2 15 22l-4-9-9-4z"/></svg>';
    var ICON_FOLDER = '<svg viewBox="0 0 24 24" width="15" height="15" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M22 12h-6l-2 3h-4l-2-3H2"/><path d="M5.45 5.11 2 12v6a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2v-6l-3.45-6.89A2 2 0 0 0 16.76 4H7.24a2 2 0 0 0-1.79 1.11z"/></svg>';

    var state = { page: 1, search: '', unread: false, selectedId: null, mailboxId: null, totalPages: 1, detail: null, relayEnabled: false, sourceCache: {}, checksCache: {}, preview: { width: 0, dark: false }, checked: new Set(), pageIds: [], focusId: null };
    var el = {};

    function cache() {
        el.list = document.getElementById('m4d-msg-list');
        el.listEmpty = document.getElementById('m4d-list-empty');
        el.pager = document.getElementById('m4d-pager');
        el.detail = document.getElementById('m4d-detail');
        el.detailEmpty = document.getElementById('m4d-detail-empty');
        el.search = document.getElementById('m4d-search');
        el.unread = document.getElementById('m4d-unread-only');
        el.refresh = document.getElementById('m4d-refresh');
        el.clear = document.getElementById('m4d-clear');
        el.deleteMailbox = document.getElementById('m4d-delete-mailbox');
        el.selectAll = document.getElementById('m4d-select-all');
        el.selCount = document.getElementById('m4d-sel-count');
        el.selRead = document.getElementById('m4d-sel-read');
        el.selUnread = document.getElementById('m4d-sel-unread');
        el.selDelete = document.getElementById('m4d-sel-delete');
        el.selDeleteLabel = document.getElementById('m4d-sel-delete-label');
        el.selRead.innerHTML = icon(PATH_MAIL_OPEN, 15);
        el.selUnread.innerHTML = icon(PATH_MAIL, 15);
        el.selDelete.insertAdjacentHTML('afterbegin', icon(PATH_TRASH, 14));
        el.mailboxList = document.getElementById('m4d-mailbox-list');
        el.statTotal = document.getElementById('m4d-stat-total');
        el.statUnread = document.getElementById('m4d-stat-unread');
        el.statSize = document.getElementById('m4d-stat-size');
    }

    /* ---------- mailboxes ---------- */
    async function loadMailboxes() {
        try {
            renderMailboxes(await M.api('/mailboxes') || []);
        } catch (e) {
            /* non-fatal */
        }
    }

    function renderMailboxes(mailboxes) {
        if (!el.mailboxList) return;
        state.mailboxes = mailboxes;
        // The selected mailbox may have been deleted (here, in another tab, or by a reset).
        var lost = state.mailboxId != null && !mailboxes.some(function (m) { return m.id === state.mailboxId; });
        if (lost) state.mailboxId = null;
        mailboxes.sort(function (a, b) {
            if (a.address === 'INBOX') return -1;
            if (b.address === 'INBOX') return 1;
            return a.address.localeCompare(b.address);
        });
        if (state.mailboxId == null) {
            var inbox = mailboxes.find(function (m) { return m.address === 'INBOX'; });
            if (inbox) state.mailboxId = inbox.id;
        }
        if (lost) {
            state.page = 1;
            state.selectedId = null;
            showDetailEmpty();
            loadList();
        }

        el.mailboxList.innerHTML = '';
        mailboxes.forEach(function (m) {
            var li = document.createElement('li');
            li.className = 'm4d-mailbox' + (m.id === state.mailboxId ? ' m4d-mailbox--active' : '');
            li.dataset.mailboxId = m.id;
            li.title = m.address + ' — ' + m.messageCount + ' message(s)';
            var name = m.address === 'INBOX' ? 'All mail' : m.address;
            var countClass = 'm4d-mailbox__count' + (m.unreadCount ? '' : ' m4d-mailbox__count--zero');
            li.innerHTML =
                '<span class="m4d-mailbox__icon">' + ICON_FOLDER + '</span>' +
                '<span class="m4d-mailbox__name">' + M.escapeHtml(name) + '</span>' +
                '<span class="' + countClass + '">' + (m.unreadCount || 0) + '</span>';
            li.addEventListener('click', function () { selectMailbox(m.id); });
            el.mailboxList.appendChild(li);
        });
        updateMailboxActions();
    }

    function selectedMailbox() {
        return (state.mailboxes || []).find(function (m) { return m.id === state.mailboxId; }) || null;
    }

    function updateMailboxActions() {
        if (!el.deleteMailbox) return;
        var m = selectedMailbox();
        el.deleteMailbox.hidden = !m || m.address === 'INBOX';
        if (m) el.deleteMailbox.setAttribute('aria-label', 'Delete mailbox ' + m.address);
    }

    async function deleteMailbox() {
        var m = selectedMailbox();
        if (!m || m.address === 'INBOX') return;
        if (!window.confirm('Delete the mailbox ' + m.address + '?\n\nMessages that were also sent to other ' +
            'recipients stay in their mailboxes and in All mail. This cannot be undone.')) return;
        try {
            var r = await M.api('/mailboxes/' + m.id, { method: 'DELETE' });
            M.toast('Deleted ' + m.address + ' and ' + r.messagesDeleted + ' message(s).');
            state.mailboxId = null;
            state.selectedId = null;
            showDetailEmpty();
            await loadMailboxes();
            loadList();
        } catch (e) {
            M.toast('Could not delete the mailbox.', 'error');
        }
    }

    function selectMailbox(id) {
        state.mailboxId = id;
        state.page = 1;
        el.mailboxList.querySelectorAll('.m4d-mailbox').forEach(function (row) {
            row.classList.toggle('m4d-mailbox--active', Number(row.dataset.mailboxId) === id);
        });
        updateMailboxActions();
        loadList();
    }

    /* ---------- inbox list ---------- */
    async function loadList() {
        try {
            var query = '?page=' + state.page + '&pageSize=50';
            if (state.search) query += '&search=' + encodeURIComponent(state.search);
            if (state.unread) query += '&unread=true';
            if (state.mailboxId) query += '&mailbox=' + state.mailboxId;
            var result = await M.api('/messages' + query);
            renderList(result);
        } catch (e) {
            M.toast('Could not load messages.', 'error');
        }
    }

    function renderList(result) {
        var items = result.items || [];
        if (state.page > 1 && items.length === 0 && (result.totalCount || 0) > 0) {
            state.page = Math.max(1, result.totalPages || 1);
            loadList();
            return;
        }

        el.list.innerHTML = '';
        el.listEmpty.hidden = items.length > 0;
        state.pageIds = items.map(function (m) { return m.id; });
        // Selection is per page: drop ids that are no longer on it (deleted, or a new page).
        state.checked.forEach(function (id) { if (state.pageIds.indexOf(id) < 0) state.checked.delete(id); });
        items.forEach(function (m) { el.list.appendChild(renderRow(m)); });
        updateSelection();

        state.totalPages = result.totalPages || 1;
        renderPager(result);
    }

    function renderRow(m) {
        var li = document.createElement('li');
        li.className = 'm4d-msg' + (m.isRead ? '' : ' m4d-msg--unread') +
            (m.id === state.selectedId ? ' m4d-msg--selected' : '') +
            (state.checked.has(m.id) ? ' m4d-msg--checked' : '') +
            (m.id === state.focusId ? ' m4d-msg--focused' : '');
        li.dataset.id = m.id;
        li.setAttribute('role', 'option');

        var chips = '';
        if (m.hasHtml) chips += '<span class="m4d-chip m4d-chip--html">HTML</span>';
        if (m.hasAttachments) chips += '<span class="m4d-chip">' + ICON_CLIP + m.attachmentCount + '</span>';

        li.innerHTML =
            (m.isRead ? '' : '<span class="m4d-sr-only m4d-msg__unread-label">Unread. </span>') +
            '<span class="m4d-msg__from">' + M.escapeHtml(m.from || m.fromAddress || '(unknown sender)') + '</span>' +
            '<span class="m4d-msg__time">' + M.escapeHtml(M.formatRelative(m.receivedAtUtc)) + '</span>' +
            '<span class="m4d-msg__subject">' + M.escapeHtml(m.subject || '(no subject)') + '</span>' +
            '<span class="m4d-msg__meta">' + chips +
                '<span class="m4d-msg__preview">' + M.escapeHtml(m.to || '') + '</span>' +
                '<span style="margin-left:auto">' + M.escapeHtml(M.formatBytes(m.rawSize)) + '</span>' +
            '</span>' +
            (m.isRead ? '' : '<span class="m4d-msg__unread-dot"></span>');

        var check = document.createElement('input');
        check.type = 'checkbox';
        check.className = 'm4d-msg__check';
        check.checked = state.checked.has(m.id);
        check.setAttribute('aria-label', 'Select message: ' + (m.subject || '(no subject)'));
        check.addEventListener('click', function (e) { e.stopPropagation(); toggleChecked(m.id, check.checked); });
        li.insertBefore(check, li.firstChild);

        var actions = document.createElement('span');
        actions.className = 'm4d-msg__actions';
        actions.appendChild(rowButton(icon(m.isRead ? PATH_MAIL : PATH_MAIL_OPEN, 14), '',
            m.isRead ? 'Mark unread' : 'Mark read', 'm4d-icon-btn', function () { setRead([m.id], !m.isRead); }));
        // Destructive, so it carries a visible label as well as the icon.
        actions.appendChild(rowButton(icon(PATH_TRASH, 13), 'Delete', 'Delete message',
            'm4d-btn m4d-btn--danger m4d-btn--xs', function () { deleteMessage(m.id); }));
        li.appendChild(actions);

        li.addEventListener('click', function () { setFocus(m.id); selectMessage(m.id); });
        return li;
    }

    function rowButton(iconHtml, text, label, className, handler) {
        var b = document.createElement('button');
        b.type = 'button';
        b.className = className;
        b.title = label;
        b.setAttribute('aria-label', label);
        b.innerHTML = iconHtml;   // constant SVG only
        if (text) b.appendChild(document.createTextNode(text));
        b.addEventListener('click', function (e) { e.stopPropagation(); handler(); });
        return b;
    }

    // Focus follows a message id, not a position, so a list reload never moves it to another message.
    function setFocus(id) {
        state.focusId = id;
        el.list.querySelectorAll('.m4d-msg').forEach(function (row) {
            row.classList.toggle('m4d-msg--focused', Number(row.dataset.id) === id);
        });
    }

    /* ---------- selection and bulk actions ---------- */
    function toggleChecked(id, on) {
        if (on) state.checked.add(id); else state.checked.delete(id);
        var row = el.list.querySelector('.m4d-msg[data-id="' + id + '"]');
        if (row) {
            row.classList.toggle('m4d-msg--checked', on);
            var box = row.querySelector('.m4d-msg__check');
            if (box) box.checked = on;
        }
        updateSelection();
    }

    function updateSelection() {
        var n = state.checked.size;
        el.selCount.textContent = n ? n + ' selected' : '';
        el.selRead.disabled = el.selUnread.disabled = el.selDelete.disabled = n === 0;
        el.selDeleteLabel.textContent = n ? 'Delete (' + n + ')' : 'Delete';
        el.selDelete.setAttribute('aria-label', 'Delete ' + n + ' selected message' + (n === 1 ? '' : 's'));
        el.selectAll.checked = n > 0 && n === state.pageIds.length;
        el.selectAll.indeterminate = n > 0 && n < state.pageIds.length;
    }

    async function setRead(ids, isRead) {
        try {
            await M.api('/messages/bulk-read', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ ids: ids, isRead: isRead })
            });
            loadList();
            loadMailboxes();
        } catch (e) {
            M.toast('Could not update the message(s).', 'error');
        }
    }

    async function deleteChecked() {
        var ids = Array.from(state.checked);
        if (!ids.length) return;
        if (!window.confirm('Delete ' + ids.length + ' selected message' + (ids.length === 1 ? '' : 's') +
            '? This cannot be undone.')) return;
        try {
            var r = await M.api('/messages/bulk-delete', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ ids: ids })
            });
            var deleted = (r && r.deleted) || [];
            if (deleted.indexOf(state.selectedId) >= 0) { state.selectedId = null; showDetailEmpty(); }
            state.checked.clear();
            M.toast('Deleted ' + deleted.length + ' message(s).');
            loadList();
            loadMailboxes();
        } catch (e) {
            M.toast('Could not delete the messages.', 'error');
        }
    }

    /* ---------- keyboard: j/k move, x select, Enter open, Delete delete ---------- */
    function onKey(e) {
        var t = e.target;
        if (e.altKey || e.ctrlKey || e.metaKey) return;
        // Shortcuts apply only when no control has focus, so Enter/Delete keep their normal meaning
        // on buttons, links, inputs and the message preview.
        if (t && t !== document.body && !(el.list.contains(t) && t.classList.contains('m4d-msg'))) return;
        var n = state.pageIds.length;
        if (!n) return;
        var focused = state.focusId != null && state.pageIds.indexOf(state.focusId) >= 0 ? state.focusId : null;
        if (e.key === 'j' || e.key === 'k') {
            var index = focused == null ? -1 : state.pageIds.indexOf(focused);
            index = Math.max(0, Math.min(n - 1, index + (e.key === 'j' ? 1 : -1)));
            setFocus(state.pageIds[index]);
            var row = el.list.querySelector('.m4d-msg[data-id="' + state.focusId + '"]');
            if (row) row.scrollIntoView({ block: 'nearest' });
        } else if (e.key === 'x' && focused != null) {
            toggleChecked(focused, !state.checked.has(focused));
        } else if (e.key === 'Enter' && focused != null) {
            selectMessage(focused);
        } else if (e.key === 'Delete' || e.key === '#') {
            if (state.checked.size) deleteChecked();
            else if (focused != null) deleteMessage(focused);
            else return;
        } else {
            return;
        }
        e.preventDefault();
    }

    function renderPager(result) {
        el.pager.innerHTML = '';
        if (state.totalPages <= 1) return;

        function pageButton(label, disabled, handler) {
            var b = document.createElement('button');
            b.type = 'button';
            b.textContent = label;
            b.disabled = disabled;
            if (!disabled) b.addEventListener('click', handler);
            return b;
        }
        el.pager.appendChild(pageButton('‹', state.page <= 1, function () { state.page--; loadList(); }));
        var label = document.createElement('span');
        label.textContent = 'Page ' + result.page + ' of ' + state.totalPages;
        el.pager.appendChild(label);
        el.pager.appendChild(pageButton('›', state.page >= state.totalPages, function () { state.page++; loadList(); }));
    }

    /* ---------- message detail ---------- */
    function selectMessage(id) {
        state.selectedId = id;
        document.querySelectorAll('.m4d-msg').forEach(function (row) {
            row.classList.toggle('m4d-msg--selected', Number(row.dataset.id) === id);
        });
        loadDetail(id);
    }

    async function loadDetail(id) {
        try {
            var detail = await M.api('/messages/' + id);
            state.detail = detail;
            renderDetail(detail);
            markRowRead(id);
        } catch (e) {
            M.toast('Could not open the message.', 'error');
        }
    }

    function markRowRead(id) {
        var row = document.querySelector('.m4d-msg[data-id="' + id + '"]');
        if (row && row.classList.contains('m4d-msg--unread')) {
            row.classList.remove('m4d-msg--unread');
            row.querySelectorAll('.m4d-msg__unread-dot, .m4d-msg__unread-label').forEach(function (n) { n.remove(); });
        }
    }

    function detailRow(label, value) {
        return '<div class="m4d-detail__row"><span class="m4d-detail__label">' + label +
            '</span><span class="m4d-detail__value">' + M.escapeHtml(value) + '</span></div>';
    }

    function renderDetail(d) {
        el.detailEmpty.hidden = true;
        el.detail.hidden = false;

        var tabs = [];
        if (d.htmlBody != null) tabs.push({ id: 'html', label: 'HTML' });
        if (d.textBody != null) tabs.push({ id: 'text', label: 'Text' });
        tabs.push({ id: 'checks', label: 'Checks' });
        tabs.push({ id: 'structure', label: 'Structure' });
        tabs.push({ id: 'headers', label: 'Headers' });
        if (d.attachments && d.attachments.length) {
            tabs.push({ id: 'attachments', label: 'Attachments', count: d.attachments.length });
        }
        tabs.push({ id: 'source', label: 'Source' });
        if (d.transcript) tabs.push({ id: 'session', label: 'Session' });
        var activeTab = tabs[0].id;

        var fromText = d.fromDisplay && d.fromDisplay !== d.fromAddress
            ? d.fromDisplay + ' <' + d.fromAddress + '>'
            : (d.fromAddress || '(unknown sender)');

        var html = '<div class="m4d-detail__head">';
        html += '<h1 class="m4d-detail__subject">' + M.escapeHtml(d.subject || '(no subject)') + '</h1>';
        html += detailRow('From', fromText);
        html += detailRow('To', d.toDisplay || d.envelopeTo || '—');
        if (d.ccDisplay) html += detailRow('Cc', d.ccDisplay);
        html += detailRow('Date', M.formatDate(d.receivedAtUtc));
        if (d.securedWithTls) html += detailRow('Security', 'Delivered over TLS');
        html += '<div class="m4d-detail__bar">';
        html += '<a class="m4d-btn" href="' + M.API + '/messages/' + d.id + '/raw">' + ICON_DOWNLOAD + 'Download .eml</a>';
        html += '<button type="button" class="m4d-btn" data-act="unread">Mark unread</button>';
        if (state.relayEnabled)
            html += '<button type="button" class="m4d-btn" data-act="relay">' + ICON_RELAY + 'Relay</button>';
        html += '<span class="m4d-detail__bar-spacer"></span>';
        html += '<button type="button" class="m4d-btn m4d-btn--danger" data-act="delete">' + ICON_TRASH + 'Delete</button>';
        html += '</div></div>';

        if (d.warnings && d.warnings.length) {
            html += '<div class="m4d-warnings"><strong>Parser notes</strong><ul>';
            d.warnings.forEach(function (w) { html += '<li>' + M.escapeHtml(w) + '</li>'; });
            html += '</ul></div>';
        }

        html += '<div class="m4d-tabs">';
        tabs.forEach(function (t) {
            html += '<button type="button" class="m4d-tab' + (t.id === activeTab ? ' m4d-tab--active' : '') +
                '" data-tab="' + t.id + '">' + t.label +
                (t.count ? '<span class="m4d-tab__count">' + t.count + '</span>' : '') + '</button>';
        });
        html += '</div>';
        html += '<div class="m4d-detail__body" id="m4d-detail-body"></div>';

        el.detail.innerHTML = html;

        el.detail.querySelector('[data-act="delete"]').addEventListener('click', function () { deleteMessage(d.id); });
        el.detail.querySelector('[data-act="unread"]').addEventListener('click', function () { setRead([d.id], false); });
        var relayButton = el.detail.querySelector('[data-act="relay"]');
        if (relayButton) relayButton.addEventListener('click', function () { relayMessage(d.id); });
        el.detail.querySelectorAll('.m4d-tab').forEach(function (tab) {
            tab.addEventListener('click', function () {
                el.detail.querySelectorAll('.m4d-tab').forEach(function (t) {
                    t.classList.toggle('m4d-tab--active', t === tab);
                });
                showTab(tab.dataset.tab, d);
            });
        });

        showTab(activeTab, d);
    }

    function showTab(tabId, d) {
        var body = document.getElementById('m4d-detail-body');
        if (!body) return;
        body.classList.toggle('m4d-detail__body--pad', tabId !== 'html');
        body.classList.toggle('m4d-detail__body--html', tabId === 'html');

        if (tabId === 'html') {
            renderPreview(body, d);
        } else if (tabId === 'checks') {
            renderChecks(body, d.id);
        } else if (tabId === 'text') {
            body.innerHTML = '<pre class="m4d-pre">' + M.escapeHtml(d.textBody || '') + '</pre>';
        } else if (tabId === 'headers') {
            body.innerHTML = '<div id="m4d-auth"></div>' + renderHeaders(d.headers || []);
            loadChecks(d.id).then(function (c) {
                var target = document.getElementById('m4d-auth');
                // The user may have moved to another message or tab while this loaded.
                if (target && c && state.detail && state.detail.id === d.id)
                    target.innerHTML = renderAuthentication(c.authentication);
            });
        } else if (tabId === 'attachments') {
            body.innerHTML = renderAttachments(d);
        } else if (tabId === 'structure') {
            body.innerHTML = renderStructure(d.structure);
        } else if (tabId === 'session') {
            body.innerHTML = renderSession(d.transcript);
        } else if (tabId === 'source') {
            renderSource(body, d.id);
        }
    }

    /* ---------- HTML preview (ADR-0003 D8) ---------- */
    // Widths: common viewport widths (StatCounter, mid-2026, via ADR-0003 research; estimate).
    var PREVIEW_WIDTHS = [
        { width: 0, label: 'Desktop', title: 'Full width' },
        { width: 768, label: 'Tablet', title: 'Tablet, 768 px wide' },
        { width: 390, label: '390', title: 'Phone, 390 px wide' },
        { width: 360, label: '360', title: 'Phone, 360 px wide' },
    ];

    function renderPreview(body, d) {
        var bar = '<div class="m4d-preview-bar" role="toolbar" aria-label="Preview size">';
        PREVIEW_WIDTHS.forEach(function (p) {
            bar += '<button type="button" class="m4d-seg" data-width="' + p.width + '" title="' + p.title +
                '" aria-pressed="' + (state.preview.width === p.width) + '">' + p.label + '</button>';
        });
        bar += '<span class="m4d-preview-bar__spacer"></span>' +
            '<button type="button" class="m4d-seg" data-dark title="Simulates prefers-color-scheme: dark. ' +
            'Clients that force-invert colours look different." aria-pressed="' + state.preview.dark + '">Dark</button></div>';
        body.innerHTML = bar + '<div class="m4d-preview"><iframe class="m4d-html-frame" sandbox="" ' +
            'title="HTML message body" src="' + M.API + '/messages/' + d.id + '/html"></iframe></div>';

        var frame = body.querySelector('iframe');
        function apply() {
            frame.style.width = state.preview.width ? state.preview.width + 'px' : '100%';
            // color-scheme on the frame element sets the framed document's prefers-color-scheme
            // (CSS Color Adjust 1 §2.1), so the email's own dark-mode styles apply.
            frame.style.colorScheme = state.preview.dark ? 'dark' : 'light';
            frame.classList.toggle('m4d-html-frame--dark', state.preview.dark);
            body.querySelectorAll('[data-width]').forEach(function (b) {
                b.setAttribute('aria-pressed', String(Number(b.dataset.width) === state.preview.width));
            });
            body.querySelector('[data-dark]').setAttribute('aria-pressed', String(state.preview.dark));
        }
        body.querySelectorAll('[data-width]').forEach(function (b) {
            b.addEventListener('click', function () { state.preview.width = Number(b.dataset.width); apply(); });
        });
        body.querySelector('[data-dark]').addEventListener('click', function () {
            state.preview.dark = !state.preview.dark;
            apply();
        });
        apply();
    }

    /* ---------- checks and authentication (ADR-0003 D5, D13) ---------- */
    function loadChecks(id) {
        if (state.checksCache[id]) return Promise.resolve(state.checksCache[id]);
        return M.api('/messages/' + id + '/checks').then(function (c) {
            state.checksCache[id] = c;
            return c;
        }).catch(function () { return null; });
    }

    async function renderChecks(body, id) {
        body.innerHTML = '<p class="m4d-empty__hint">Running checks…</p>';
        var c = await loadChecks(id);
        // Stale: another message or another tab is showing now.
        var active = el.detail.querySelector('.m4d-tab--active');
        if (document.getElementById('m4d-detail-body') !== body || !state.detail || state.detail.id !== id ||
            !active || active.dataset.tab !== 'checks') return;
        if (!c) { body.innerHTML = '<p class="m4d-empty__hint">Could not run the checks.</p>'; return; }

        var report = c.html;
        var html = '<div class="m4d-checks">';
        if (report.hasHtml)
            html += '<p class="m4d-checks__summary">HTML size ' + M.formatBytes(report.htmlBytes) +
                ' · ' + report.checks.length + ' finding(s) · checked offline with built-in rules; links are not opened.</p>';
        else
            html += '<p class="m4d-checks__summary">This message has no HTML body. Header checks only.</p>';

        if (!report.checks.length) html += '<p class="m4d-empty__hint">No findings.</p>';
        report.checks.forEach(function (f) {
            html += '<div class="m4d-check m4d-check--' + M.escapeHtml(f.severity) + '">' +
                '<span class="m4d-check__sev">' + M.escapeHtml(f.severity) + '</span>' +
                '<div class="m4d-check__body"><div class="m4d-check__msg">' + M.escapeHtml(f.message) +
                (f.count > 1 ? ' <span class="m4d-check__count">×' + f.count + '</span>' : '') + '</div>' +
                (f.sample ? '<code class="m4d-check__sample">' + M.escapeHtml(f.sample) + '</code>' : '') +
                '</div></div>';
        });

        if (report.links && report.links.length) {
            html += '<h3 class="m4d-checks__title">Links (' + report.links.length +
                (report.linksTruncated ? '+' : '') + ')</h3><table class="m4d-headers m4d-links"><thead><tr>' +
                '<th>Text</th><th>Target</th><th>Scheme</th><th>Count</th></tr></thead><tbody>';
            report.links.forEach(function (l) {
                var scheme = M.escapeHtml(l.scheme);
                html += '<tr><td>' + M.escapeHtml(l.text || '—') + '</td><td class="m4d-links__href">' + M.escapeHtml(l.href) +
                    '</td><td><span class="m4d-scheme m4d-scheme--' + (l.scheme === 'http' ? 'warn' : 'ok') + '">' + scheme +
                    '</span></td><td>' + l.count + '</td></tr>';
            });
            html += '</tbody></table>';
        }
        body.innerHTML = html + '</div>';
    }

    function resultChip(result) {
        var r = String(result || '').toLowerCase();
        var kind = r === 'pass' ? 'ok' : (r === 'fail' || r === 'permerror' || r === 'softfail') ? 'bad' : 'neutral';
        return '<span class="m4d-auth-chip m4d-auth-chip--' + kind + '">' + M.escapeHtml(r || '?') + '</span>';
    }

    function renderAuthentication(a) {
        if (!a) return '';
        var any = a.authenticationResults.length + a.receivedSpf.length + a.dkimSignatures.length +
            a.arcAuthenticationResults.length + a.arcSignatures.length;
        if (!any) return '';
        var html = '<section class="m4d-auth" aria-label="Authentication headers"><h3 class="m4d-checks__title">Authentication</h3>' +
            '<p class="m4d-auth__note">As reported by the sending path — not verified by Hermex.</p>';
        function results(list, label) {
            list.forEach(function (h) {
                html += '<div class="m4d-auth__row"><span class="m4d-auth__label">' + label +
                    (h.instance ? ' i=' + h.instance : '') + '</span><span class="m4d-auth__by">' + M.escapeHtml(h.authServId) + '</span>';
                h.results.forEach(function (r) {
                    html += '<span class="m4d-auth__result" title="' + M.escapeHtml(r.reason || '') + '">' +
                        M.escapeHtml(r.method) + ' ' + resultChip(r.result) + '</span>';
                });
                html += '</div>';
            });
        }
        results(a.authenticationResults, 'Authentication-Results');
        a.receivedSpf.forEach(function (s) {
            html += '<div class="m4d-auth__row"><span class="m4d-auth__label">Received-SPF</span>' + resultChip(s.result) +
                (s.comment ? '<span class="m4d-auth__by">' + M.escapeHtml(s.comment) + '</span>' : '') + '</div>';
        });
        function signatures(list) {
            list.forEach(function (s) {
                var t = s.tags || {};
                html += '<div class="m4d-auth__row"><span class="m4d-auth__label">' + M.escapeHtml(s.kind) +
                    (s.instance ? ' i=' + s.instance : '') + '</span><span class="m4d-auth__by">' +
                    ['d', 's', 'a', 'cv'].filter(function (k) { return t[k]; })
                        .map(function (k) { return k + '=' + M.escapeHtml(t[k]); }).join(' · ') + '</span></div>';
                s.warnings.forEach(function (w) {
                    html += '<div class="m4d-auth__warn">' + M.escapeHtml(w) + '</div>';
                });
            });
        }
        signatures(a.dkimSignatures);
        results(a.arcAuthenticationResults, 'ARC-Authentication-Results');
        signatures(a.arcSignatures);
        if (a.truncated) html += '<div class="m4d-auth__warn">Only the first 50 headers of each kind are shown.</div>';
        return html + '</section>';
    }

    function renderStructure(node) {
        if (!node) return '<p class="m4d-empty__hint">No MIME structure available.</p>';
        return '<div class="m4d-tree">' + structureNode(node) + '</div>';
    }

    function structureNode(node) {
        var meta = [];
        if (node.fileName) meta.push(M.escapeHtml(node.fileName));
        if (node.encoding && node.encoding !== '7bit') meta.push(M.escapeHtml(node.encoding));
        if (node.charset) meta.push(M.escapeHtml(node.charset));
        if (node.size) meta.push(M.formatBytes(node.size));
        var children = (node.children || []).map(structureNode).join('');
        return '<div class="m4d-tree__node">' +
            '<div class="m4d-tree__row">' +
                '<span class="m4d-tree__role m4d-tree__role--' + M.escapeHtml(node.role || 'body') + '">' +
                    M.escapeHtml(node.role || 'body') + '</span>' +
                '<span class="m4d-tree__type">' + M.escapeHtml(node.mediaType || '') + '</span>' +
                (meta.length ? '<span class="m4d-tree__meta">' + meta.join(' &middot; ') + '</span>' : '') +
            '</div>' +
            (children ? '<div class="m4d-tree__children">' + children + '</div>' : '') +
            '</div>';
    }

    function renderSession(transcript) {
        if (!transcript) return '<p class="m4d-empty__hint">No session transcript was captured.</p>';
        var lines = transcript.split(/\r?\n/).map(function (line) {
            var cls = 'm4d-x-note';
            if (line.indexOf('S: ') === 0) cls = 'm4d-x-server';
            else if (line.indexOf('C: ') === 0) cls = 'm4d-x-client';
            return '<span class="' + cls + '">' + M.escapeHtml(line) + '</span>';
        });
        return '<pre class="m4d-pre m4d-transcript">' + lines.join('\n') + '</pre>';
    }

    function renderHeaders(headers) {
        if (!headers.length) return '<p class="m4d-empty__hint">No headers.</p>';
        var rows = headers.map(function (h) {
            return '<tr><td>' + M.escapeHtml(h.name) + '</td><td>' + M.escapeHtml(h.value) + '</td></tr>';
        }).join('');
        return '<table class="m4d-headers"><tbody>' + rows + '</tbody></table>';
    }

    function renderAttachments(d) {
        var items = (d.attachments || []).map(function (a) {
            var meta = (a.contentType || 'application/octet-stream') + ' · ' + M.formatBytes(a.size) +
                (a.isInline ? ' · inline' : '');
            return '<a class="m4d-attachment" href="' + M.API + '/messages/' + d.id + '/attachments/' + a.index + '">' +
                '<span class="m4d-attachment__icon">' + ICON_FILE + '</span>' +
                '<span class="m4d-attachment__info">' +
                '<span class="m4d-attachment__name">' + M.escapeHtml(a.fileName || ('attachment-' + a.index)) + '</span>' +
                '<span class="m4d-attachment__meta">' + M.escapeHtml(meta) + '</span>' +
                '</span>' + ICON_DOWNLOAD + '</a>';
        }).join('');
        return '<div class="m4d-attachments">' + items + '</div>';
    }

    async function renderSource(body, id) {
        if (state.sourceCache[id] != null) {
            body.innerHTML = '<pre class="m4d-pre">' + M.escapeHtml(state.sourceCache[id]) + '</pre>';
            return;
        }
        body.innerHTML = '<p class="m4d-pre">Loading source…</p>';
        try {
            var raw = await M.api('/messages/' + id + '/raw');
            state.sourceCache[id] = raw;
            body.innerHTML = '<pre class="m4d-pre">' + M.escapeHtml(raw) + '</pre>';
        } catch (e) {
            body.innerHTML = '<p class="m4d-pre">Could not load the raw source.</p>';
        }
    }

    function showDetailEmpty() {
        state.detail = null;
        el.detail.hidden = true;
        el.detail.innerHTML = '';
        el.detailEmpty.hidden = false;
    }

    /* ---------- actions ---------- */
    async function deleteMessage(id) {
        if (!window.confirm('Delete this message permanently?')) return;
        try {
            await M.api('/messages/' + id, { method: 'DELETE' });
            if (state.selectedId === id) { state.selectedId = null; showDetailEmpty(); }
            M.toast('Message deleted.');
            loadList();
        } catch (e) {
            M.toast('Could not delete the message.', 'error');
        }
    }

    async function relayMessage(id) {
        try {
            await M.api('/messages/' + id + '/relay', { method: 'POST' });
            M.toast('Message relayed to the upstream SMTP server.', 'success');
        } catch (e) {
            M.toast('Relay failed: ' + e.message, 'error');
        }
    }

    async function clearInbox() {
        // This empties the whole store, not just the mailbox on screen, so the prompt says so.
        if (!window.confirm('Delete all messages in every mailbox? This cannot be undone.\n\n' +
            'To delete one message, open it and use Delete.')) return;
        try {
            var result = await M.api('/messages', { method: 'DELETE' });
            state.selectedId = null;
            showDetailEmpty();
            M.toast('Deleted ' + (result && result.cleared != null ? result.cleared : 0) + ' message(s) from every mailbox.');
            state.page = 1;
            loadList();
        } catch (e) {
            M.toast('Could not delete the messages.', 'error');
        }
    }

    /* ---------- stats ---------- */
    function applyStats(stats) {
        if (!stats) return;
        if (el.statTotal) el.statTotal.textContent = (stats.totalMessages || 0).toLocaleString();
        if (el.statUnread) el.statUnread.textContent = (stats.unreadMessages || 0).toLocaleString();
        if (el.statSize) el.statSize.textContent = M.formatBytes(stats.totalSizeBytes || 0);
    }

    function applyStatus(status) {
        if (!status) return;
        applyStats(status.stats);
        if (status.options) state.relayEnabled = !!status.options.relayEnabled;
    }

    /* ---------- wiring ---------- */
    /* ---------- .eml import (ADR-0003 D14) ---------- */
    var IMPORT_MAX_FILES = 50; // EmlImport.MaxFiles on the server

    async function importFiles(fileList) {
        var files = Array.prototype.slice.call(fileList || []);
        if (!files.length) return;
        if (files.length > IMPORT_MAX_FILES) {
            M.toast('Import at most ' + IMPORT_MAX_FILES + ' files at a time.', 'error');
            return;
        }
        var form = new FormData();
        files.forEach(function (f) { form.append('files', f, f.name); });
        try {
            var r = await M.api('/messages/import', { method: 'POST', body: form });
            M.toast('Imported ' + r.imported + ' message' + (r.imported === 1 ? '' : 's') +
                (r.skipped ? ' (' + r.skipped + ' empty file' + (r.skipped === 1 ? '' : 's') + ' skipped)' : '') + '.', 'success');
            // The list refreshes from the messageReceived hub event (or the polling fallback) once they are stored.
        } catch (e) {
            // M.api errors read "Request failed (NNN) <body>"; the body carries error and, on 503, imported.
            var status = /\((\d{3})\)/.exec(e.message);
            var body = {};
            try { body = JSON.parse(e.message.slice(e.message.indexOf('{'))); } catch (x) { /* not JSON */ }
            var text = status && status[1] === '403' && !body.error ? 'You do not have permission to import messages.'
                : body.error || 'Import failed.';
            if (body.imported) {
                text = body.imported + ' message' + (body.imported === 1 ? ' was' : 's were') + ' imported before this: ' + text;
            }
            M.toast(text, 'error');
        }
    }

    function wireImport() {
        var button = document.getElementById('m4d-import');
        var input = document.getElementById('m4d-import-file');
        if (!button || !input) return;
        button.addEventListener('click', function () { input.click(); });
        input.addEventListener('change', function () { importFiles(input.files); input.value = ''; });

        var target = el.list.parentElement;
        var depth = 0;
        function hasFiles(e) { return e.dataTransfer && Array.prototype.indexOf.call(e.dataTransfer.types, 'Files') >= 0; }
        target.addEventListener('dragenter', function (e) {
            if (!hasFiles(e)) return;
            e.preventDefault();
            depth++;
            target.classList.add('m4d-dropping');
        });
        target.addEventListener('dragover', function (e) { if (hasFiles(e)) e.preventDefault(); });
        target.addEventListener('dragleave', function () {
            if (--depth <= 0) { depth = 0; target.classList.remove('m4d-dropping'); }
        });
        target.addEventListener('drop', function (e) {
            if (!hasFiles(e)) return;
            e.preventDefault();
            depth = 0;
            target.classList.remove('m4d-dropping');
            importFiles(e.dataTransfer.files);
        });
    }

    M.ready(function () {
        cache();
        wireImport();

        var debouncedSearch = M.debounce(function () {
            state.search = el.search.value.trim();
            state.page = 1;
            loadList();
        }, 280);
        el.search.addEventListener('input', debouncedSearch);
        el.unread.addEventListener('change', function () {
            state.unread = el.unread.checked;
            state.page = 1;
            loadList();
        });
        el.refresh.addEventListener('click', function () { loadList(); M.refreshStatus(); });
        el.clear.addEventListener('click', clearInbox);
        if (el.deleteMailbox) el.deleteMailbox.addEventListener('click', deleteMailbox);
        el.selectAll.addEventListener('change', function () {
            var on = el.selectAll.checked;
            state.pageIds.forEach(function (id) { toggleChecked(id, on); });
        });
        el.selRead.addEventListener('click', function () { setRead(Array.from(state.checked), true); });
        el.selUnread.addEventListener('click', function () { setRead(Array.from(state.checked), false); });
        el.selDelete.addEventListener('click', deleteChecked);
        document.addEventListener('keydown', onKey);
        var debouncedReload = M.debounce(function () { loadList(); loadMailboxes(); }, 350);
        function closeIfDeleted(deleted) {
            if (state.selectedId != null && (deleted || []).indexOf(state.selectedId) >= 0) {
                state.selectedId = null;
                showDetailEmpty();
            }
        }
        M.onHub('messagesChanged', function (p) { closeIfDeleted(p && p.deletedIds); debouncedReload(); });
        M.onHub('mailboxesChanged', function (p) { closeIfDeleted(p && p.deletedMessageIds); debouncedReload(); });
        M.onHub('messageReceived', debouncedReload);
        M.onHub('messageDeleted', debouncedReload);
        M.onHub('messageUpdated', function (p) {
            if (p && !p.isRead) loadList();
            loadMailboxes();
        });
        M.onHub('inboxCleared', function () {
            state.selectedId = null;
            showDetailEmpty();
            loadList();
            loadMailboxes();
        });
        M.onHub('statsChanged', applyStats);
        M.onHub('status', applyStatus);
        M.onHub('poll', function () { loadList(); loadMailboxes(); });

        loadMailboxes().then(loadList);
        M.api('/status').then(applyStatus).catch(function () { });
    });
})();
