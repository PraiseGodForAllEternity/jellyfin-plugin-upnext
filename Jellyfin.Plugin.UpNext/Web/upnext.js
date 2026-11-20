/*
 * Up Next for Jellyfin - web overlay.
 *
 * When a movie reaches its credits, the video shrinks to the top-left corner and a
 * card counts down to the next film. Written in plain ES5 so older smart TV
 * browsers (webOS, Tizen) can run it.
 */
(function () {
    'use strict';

    if (window.__upNextLoaded) {
        return;
    }
    window.__upNextLoaded = true;

    var TICKS = 10000000;
    var TICK_MS = 500;
    var POLL_MS = 3000;
    var KEY_BACK = [8, 27, 461, 10009];
    var KEY_ENTER = 13;
    var KEY_DIRS = { 37: 'left', 38: 'up', 39: 'right', 40: 'down' };

    var state = {
        itemId: null,
        session: null,
        sessionAt: 0,
        lastPoll: 0,
        polling: false,
        info: null,
        video: null,
        phase: 'idle', // idle | card | ended
        dismissed: null, // item id the viewer waved off (Watch Credits / Back)
        leaving: false,
        handled: false,
        countdown: null,
        lastPos: 0,
        lastRuntime: 0
    };

    // ---------------------------------------------------------------- helpers

    function api() {
        var client = window.ApiClient;
        if (!client || typeof client.accessToken !== 'function' || !client.accessToken()) {
            return null;
        }
        return client;
    }

    function request(method, path, body) {
        var client = api();
        if (!client) {
            return Promise.reject(new Error('Not signed in'));
        }
        var options = { type: method, url: client.getUrl('UpNext/' + path) };
        if (method === 'GET') {
            options.dataType = 'json';
        }
        if (body) {
            options.data = JSON.stringify(body);
            options.contentType = 'application/json';
            options.dataType = 'json';
        }
        return client.ajax(options);
    }

    function imageUrl(id, type, maxWidth) {
        var client = api();
        return client ? client.getUrl('Items/' + id + '/Images/' + type + '/0', { maxWidth: maxWidth, quality: 85 }) : '';
    }

    function escapeHtml(text) {
        return String(text == null ? '' : text)
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;')
            .replace(/'/g, '&#39;');
    }

    // A background-image declaration that's safe inside a double-quoted style attribute.
    function bgStyle(url) {
        var safe = String(url || '').replace(/["'\\\n\r()]/g, function (c) {
            return '\\' + c.charCodeAt(0).toString(16) + ' ';
        });
        return escapeHtml('background-image:url("' + safe + '")');
    }

    function formatRuntime(ticks) {
        if (!ticks) {
            return '';
        }
        var minutes = Math.round(ticks / TICKS / 60);
        var h = Math.floor(minutes / 60);
        var m = minutes % 60;
        return h ? h + 'h ' + m + 'm' : m + 'm';
    }

    function findVideo() {
        var video = document.querySelector('.videoPlayerContainer video.htmlvideoplayer');
        return video && video.isConnected !== false ? video : null;
    }

    // -------------------------------------------------------------- the loop

    function tick() {
        if (!api()) {
            return;
        }

        injectPreferencesLink();

        var video = findVideo();
        if (video !== state.video) {
            attachVideo(video);
        }

        if (!video) {
            // Some players tear down the video at the end without firing "ended".
            if (state.itemId && state.phase !== 'ended' && state.lastRuntime && state.lastPos >= state.lastRuntime - 5) {
                onVideoEnded();
            }
            if (state.phase === 'card') {
                closeCard();
            }
            if (state.phase === 'idle') {
                state.itemId = null;
                state.info = null;
                state.lastPos = 0;
                state.lastRuntime = 0;
            }
            return;
        }

        pollSession();

        var info = state.info;
        if (!info || info.ItemId !== state.itemId || !isActiveFor(info)) {
            return;
        }

        var runtime = (info.RunTimeTicks || 0) / TICKS;
        var pos = currentSeconds(video, runtime);
        if (!runtime || pos == null) {
            return;
        }
        state.lastPos = pos;
        state.lastRuntime = runtime;

        var triggerAt = info.CreditsStartTicks
            ? info.CreditsStartTicks / TICKS
            : runtime - info.FallbackSecondsBeforeEnd;
        // Never pop up in the first half, even for short videos or odd settings.
        triggerAt = Math.max(triggerAt, runtime / 2);

        if (state.phase === 'idle') {
            if (pos >= triggerAt && pos < runtime - 1 && state.dismissed !== state.itemId && !state.handled) {
                openCard(false);
            }
        } else if (state.phase === 'card' && pos < triggerAt - 5) {
            // Viewer seeked back into the movie.
            closeCard();
        }
    }

    function isActiveFor(info) {
        return info.Mode !== 'Off' && info.Next && !(state.session && state.session.QueueHasNext);
    }

    function currentSeconds(video, runtime) {
        // Direct play and HLS both expose the full timeline; trust the element then.
        if (isFinite(video.duration) && video.duration > 0 && Math.abs(video.duration - runtime) < 90) {
            return video.currentTime;
        }
        var s = state.session;
        if (!s || s.PositionTicks == null) {
            return null;
        }
        var pos = s.PositionTicks / TICKS;
        if (!s.IsPaused) {
            pos += (s.ReportAgeMs + (Date.now() - state.sessionAt)) / 1000;
        }
        return pos;
    }

    function pollSession() {
        var now = Date.now();
        if (state.polling || now - state.lastPoll < POLL_MS) {
            return;
        }
        state.polling = true;
        state.lastPoll = now;

        request('GET', 'Session').then(function (s) {
            state.polling = false;
            state.session = s;
            state.sessionAt = Date.now();

            var id = s && s.IsMovie ? s.ItemId : null;
            if (id !== state.itemId) {
                onItemChanged(id);
            }
        }, function () {
            state.polling = false;
        });
    }

    function onItemChanged(id) {
        if (state.phase !== 'idle') {
            closeCard();
        }
        state.itemId = id;
        state.info = null;
        state.dismissed = null;
        state.leaving = false;
        state.handled = false;
        state.lastPos = 0;
        state.lastRuntime = 0;

        if (!id || (state.session && state.session.QueueHasNext)) {
            return;
        }

        request('GET', 'Next/' + id).then(function (info) {
            if (state.itemId !== id) {
                return;
            }
            state.info = info;
            if (info.Mode !== 'Off') {
                // Tell the server this device has the overlay, so it doesn't autoplay too.
                request('POST', 'Claim/' + id).catch(function () {});
                preloadImages(info);
            }
        }, function () {});
    }

    function preloadImages(info) {
        if (!info.Next) {
            return;
        }
        var img = new Image();
        img.src = imageUrl(info.Next.Id, info.Next.HasBackdrop ? 'Backdrop' : 'Primary', 1920);
    }

    function attachVideo(video) {
        if (state.video) {
            state.video.removeEventListener('ended', onVideoEnded);
        }
        state.video = video;
        if (video) {
            video.addEventListener('ended', onVideoEnded);
        }
    }

    function onVideoEnded() {
        var info = state.info;
        if (!info || info.ItemId !== state.itemId || !isActiveFor(info) ||
            state.leaving || state.handled || state.phase === 'ended') {
            return;
        }
        state.lastPos = 0;

        if (state.phase === 'card' && info.Mode === 'Autoplay') {
            // Credits are over: no reason to keep waiting.
            playItem(info.Next.Id);
            return;
        }

        // They watched the credits (or the card never showed): offer what's next on an end screen.
        if (state.phase === 'card') {
            closeCard();
        }
        openCard(true);
    }

    // -------------------------------------------------------------- the card

    function openCard(ended) {
        var info = state.info;
        ensureStyles();
        removeOverlay();

        state.phase = ended ? 'ended' : 'card';
        // The backdrop sits under jellyfin's video layer (z-index 1000) and the
        // controls sit over it, so the shrunken video shows between the two.
        var parts = renderCard(info, ended);
        var bg = document.createElement('div');
        bg.id = 'upnext-bg';
        bg.innerHTML = parts.bg;
        document.body.appendChild(bg);

        var root = document.createElement('div');
        root.id = 'upnext-overlay';
        root.className = ended ? 'upnext-ended' : 'upnext-credits';
        root.innerHTML = parts.fg;
        document.body.appendChild(root);

        if (!ended) {
            document.documentElement.classList.add('upnext-active');
        }

        wireCard(root);

        if (info.Mode === 'Autoplay') {
            startCountdown(info.CountdownSeconds, info.Next.Id);
        }

        setTimeout(function () {
            var primary = root.querySelector('.upnext-play');
            if (primary) {
                primary.focus();
            }
        }, 50);
    }

    function renderCard(info, ended) {
        var next = info.Next;
        var backdrop = imageUrl(next.Id, next.HasBackdrop ? 'Backdrop' : 'Primary', 1920);
        var meta = [];
        if (next.Year) {
            meta.push(escapeHtml(next.Year));
        }
        if (next.OfficialRating) {
            meta.push('<span class="upnext-rating">' + escapeHtml(next.OfficialRating) + '</span>');
        }
        if (next.RunTimeTicks) {
            meta.push(formatRuntime(next.RunTimeTicks));
        }

        var label = info.CollectionName
            ? 'Next in ' + escapeHtml(info.CollectionName)
            : 'Up Next';

        var title = next.HasLogo
            ? '<img class="upnext-logo" alt="' + escapeHtml(next.Name) + '" src="' + escapeHtml(imageUrl(next.Id, 'Logo', 600)) + '">'
            : '<h2 class="upnext-title">' + escapeHtml(next.Name) + '</h2>';

        var playLabel = info.Mode === 'Autoplay' ? 'Play Now' : 'Play';

        var bg = '<div class="upnext-backdrop' + (ended ? ' upnext-still' : '') + '" style="' + bgStyle(backdrop) + '"></div>' +
            '<div class="upnext-shade"></div>';

        var html = '';

        if (!ended) {
            html += '<button type="button" class="upnext-mini upnext-focusable" data-action="credits" aria-label="Watch credits">' +
                '<span class="upnext-mini-label"><span class="material-icons fullscreen" aria-hidden="true"></span>Watch Credits</span>' +
                '</button>';
        }

        html += '<button type="button" class="upnext-gear upnext-focusable" data-action="settings" aria-label="Up Next settings">' +
            '<span class="material-icons settings" aria-hidden="true"></span></button>';

        html += '<div class="upnext-panel">';
        html += '<div class="upnext-info">';
        html += '<div class="upnext-label">' + label + '</div>';
        html += title;
        html += '<div class="upnext-meta">' + meta.join('<span class="upnext-dot">&bull;</span>') + '</div>';
        if (next.Overview) {
            html += '<p class="upnext-overview">' + escapeHtml(next.Overview) + '</p>';
        }
        html += '<div class="upnext-buttons">';
        html += '<button type="button" class="upnext-btn upnext-play upnext-focusable" data-action="play" data-id="' + escapeHtml(next.Id) + '">' +
            '<span class="upnext-fill"></span>' +
            '<span class="upnext-btn-text"><span class="material-icons play_arrow" aria-hidden="true"></span>' +
            playLabel + '<span class="upnext-count"></span></span></button>';
        html += '<button type="button" class="upnext-btn upnext-secondary upnext-focusable" data-action="browse">' +
            '<span class="upnext-btn-text">Back to Browse</span></button>';
        html += '</div>';
        html += '</div>';

        if (info.Suggestions && info.Suggestions.length) {
            html += '<div class="upnext-more"><div class="upnext-more-label">More Like This</div><div class="upnext-tiles">';
            for (var i = 0; i < info.Suggestions.length; i++) {
                var s = info.Suggestions[i];
                var thumb = imageUrl(s.Id, s.HasBackdrop ? 'Backdrop' : 'Primary', 480);
                html += '<button type="button" class="upnext-tile upnext-focusable" data-action="play" data-id="' + escapeHtml(s.Id) + '" title="' + escapeHtml(s.Name) + '">' +
                    '<span class="upnext-tile-img" style="' + bgStyle(thumb) + '"></span>' +
                    '<span class="upnext-tile-name">' + escapeHtml(s.Name) + (s.Year ? ' <span class="upnext-tile-year">' + escapeHtml(s.Year) + '</span>' : '') + '</span>' +
                    '</button>';
            }
            html += '</div></div>';
        }

        html += '</div>';
        return { bg: bg, fg: html };
    }

    function wireCard(root) {
        root.addEventListener('click', function (e) {
            var target = e.target.closest ? e.target.closest('[data-action]') : null;
            if (!target) {
                return;
            }
            e.preventDefault();
            e.stopPropagation();
            var action = target.getAttribute('data-action');
            if (action === 'play') {
                playItem(target.getAttribute('data-id'));
            } else if (action === 'credits') {
                watchCredits();
            } else if (action === 'browse') {
                backToBrowse();
            } else if (action === 'settings') {
                stopCountdown();
                openSettings();
            }
        });
    }

    function startCountdown(seconds, nextId) {
        stopCountdown();
        var root = document.getElementById('upnext-overlay');
        if (!root) {
            return;
        }
        var fill = root.querySelector('.upnext-fill');
        var count = root.querySelector('.upnext-count');
        var total = Math.max(1, seconds) * 1000;
        var started = Date.now();

        function step() {
            var left = Math.max(0, total - (Date.now() - started));
            if (fill) {
                fill.style.width = (100 - (left / total) * 100) + '%';
            }
            if (count) {
                count.textContent = ' · ' + Math.ceil(left / 1000);
            }
            if (left <= 0) {
                stopCountdown();
                playItem(nextId);
            }
        }

        step();
        state.countdown = setInterval(step, 200);
    }

    function stopCountdown() {
        if (state.countdown) {
            clearInterval(state.countdown);
            state.countdown = null;
        }
        var count = document.querySelector('#upnext-overlay .upnext-count');
        if (count) {
            count.textContent = '';
        }
        var fill = document.querySelector('#upnext-overlay .upnext-fill');
        if (fill) {
            fill.style.width = '0';
        }
    }

    function closeCard() {
        stopCountdown();
        removeOverlay();
        document.documentElement.classList.remove('upnext-active');
        state.phase = 'idle';
    }

    function removeOverlay() {
        var ids = ['upnext-overlay', 'upnext-bg'];
        for (var i = 0; i < ids.length; i++) {
            var old = document.getElementById(ids[i]);
            if (old && old.parentNode) {
                old.parentNode.removeChild(old);
            }
        }
    }

    // ---------------------------------------------------------------- actions

    function playItem(id) {
        if (!id) {
            return;
        }
        state.handled = true;
        state.dismissed = state.itemId;
        closeCard();
        request('POST', 'Play/' + id).catch(function () {
            // Couldn't reach the player remotely: open the movie page instead.
            window.location.hash = '#/details?id=' + id;
        });
    }

    function watchCredits() {
        state.dismissed = state.itemId;
        closeCard();
    }

    function backToBrowse() {
        var wasPlaying = state.phase === 'card';
        state.leaving = true;
        state.dismissed = state.itemId;
        closeCard();
        if (wasPlaying) {
            request('POST', 'Stop').catch(function () {
                window.history.back();
            });
        }
    }

    // ------------------------------------------------------ remote / keyboard

    // One listener on window (capture) so we see keys before jellyfin's own navigation.
    function onKeyDown(e) {
        var settings = document.getElementById('upnext-settings');
        if (settings) {
            onSettingsKey(e, settings);
            return;
        }
        var overlay = document.getElementById('upnext-overlay');
        if (!overlay) {
            return;
        }
        var code = e.keyCode;
        if (KEY_BACK.indexOf(code) !== -1) {
            if (code === 8 && /input|textarea/i.test((e.target && e.target.tagName) || '')) {
                return;
            }
            swallow(e);
            if (state.phase === 'card') {
                watchCredits();
            } else {
                state.leaving = true;
                closeCard();
            }
        } else if (code === KEY_ENTER) {
            var focused = document.activeElement;
            if (focused && overlay.contains(focused)) {
                swallow(e);
                focused.click();
            }
        } else if (KEY_DIRS[code]) {
            swallow(e);
            moveFocus(overlay, KEY_DIRS[code]);
        }
    }

    function swallow(e) {
        e.preventDefault();
        e.stopPropagation();
        if (e.stopImmediatePropagation) {
            e.stopImmediatePropagation();
        }
    }

    function moveFocus(container, dir) {
        var items = Array.prototype.slice.call(container.querySelectorAll('.upnext-focusable'));
        var current = document.activeElement;
        if (!current || items.indexOf(current) === -1) {
            if (items.length) {
                items[0].focus();
            }
            return;
        }
        var from = center(current);
        var best = null;
        var bestScore = Infinity;
        for (var i = 0; i < items.length; i++) {
            if (items[i] === current || !items[i].offsetParent) {
                continue;
            }
            var to = center(items[i]);
            var dx = to.x - from.x;
            var dy = to.y - from.y;
            var ok = (dir === 'left' && dx < -4) || (dir === 'right' && dx > 4) ||
                (dir === 'up' && dy < -4) || (dir === 'down' && dy > 4);
            if (!ok) {
                continue;
            }
            // Favor straight lines over diagonals.
            var along = dir === 'left' || dir === 'right' ? Math.abs(dx) : Math.abs(dy);
            var across = dir === 'left' || dir === 'right' ? Math.abs(dy) : Math.abs(dx);
            var score = along + across * 2;
            if (score < bestScore) {
                bestScore = score;
                best = items[i];
            }
        }
        if (best) {
            best.focus();
        }
    }

    function center(el) {
        var r = el.getBoundingClientRect();
        return { x: r.left + r.width / 2, y: r.top + r.height / 2 };
    }

    // ---------------------------------------------------------------- settings

    function openSettings() {
        ensureStyles();
        request('GET', 'Preferences').then(renderSettings, function () {
            renderSettings(null);
        });
    }

    function renderSettings(prefs) {
        removeSettingsDialog();
        prefs = prefs || { Mode: 'Autoplay', CountdownSeconds: 20, UsesDefaultMode: true, UsesDefaultCountdown: true, DefaultMode: 'Autoplay', DefaultCountdownSeconds: 20 };

        var modes = [
            ['Autoplay', 'Autoplay with countdown', 'Shrinks the movie at the credits and starts the next one.'],
            ['SuggestionsOnly', 'Show Up Next, no autoplay', 'Shows the card at the credits but waits for you.'],
            ['Off', 'Off', 'Movies end normally.']
        ];
        var counts = [5, 10, 15, 20, 30, 45, 60];
        var currentCount = parseInt(prefs.CountdownSeconds, 10) || 20;
        if (counts.indexOf(currentCount) === -1) {
            // Admin may have set a value that isn't in the list; keep it selectable.
            counts.push(currentCount);
            counts.sort(function (a, b) { return a - b; });
        }

        var html = '<div class="upnext-dialog" role="dialog" aria-modal="true" aria-label="Up Next settings">';
        html += '<h2>Up Next</h2>';
        html += '<div class="upnext-field-label">When a movie reaches its credits</div>';
        for (var i = 0; i < modes.length; i++) {
            var checked = prefs.Mode === modes[i][0] ? ' checked' : '';
            html += '<label class="upnext-option upnext-sfocus"><input type="radio" name="upnext-mode" value="' + modes[i][0] + '"' + checked + '>' +
                '<span><strong>' + modes[i][1] + '</strong><small>' + modes[i][2] + '</small></span></label>';
        }
        html += '<label class="upnext-field-label" for="upnext-count">Countdown</label>';
        html += '<select id="upnext-count" class="upnext-select upnext-sfocus">';
        for (var c = 0; c < counts.length; c++) {
            html += '<option value="' + counts[c] + '"' + (counts[c] === currentCount ? ' selected' : '') + '>' + counts[c] + ' seconds</option>';
        }
        html += '</select>';
        var defaultName = { Autoplay: 'Autoplay', SuggestionsOnly: 'Show Up Next, no autoplay', Off: 'Off' }[prefs.DefaultMode] || 'Autoplay';
        html += '<p class="upnext-hint">Server default: ' + defaultName +
            ', ' + (parseInt(prefs.DefaultCountdownSeconds, 10) || 20) + ' seconds.</p>';
        html += '<div class="upnext-dialog-buttons">';
        html += '<button type="button" class="upnext-btn upnext-play upnext-sfocus" data-do="save"><span class="upnext-btn-text">Save</span></button>';
        html += '<button type="button" class="upnext-btn upnext-secondary upnext-sfocus" data-do="reset"><span class="upnext-btn-text">Use defaults</span></button>';
        html += '<button type="button" class="upnext-btn upnext-secondary upnext-sfocus" data-do="cancel"><span class="upnext-btn-text">Cancel</span></button>';
        html += '</div></div>';

        var wrap = document.createElement('div');
        wrap.id = 'upnext-settings';
        wrap.innerHTML = html;
        document.body.appendChild(wrap);

        wrap.addEventListener('click', function (e) {
            if (e.target === wrap) {
                closeSettings();
                return;
            }
            var btn = e.target.closest ? e.target.closest('[data-do]') : null;
            if (!btn) {
                return;
            }
            var what = btn.getAttribute('data-do');
            if (what === 'cancel') {
                closeSettings();
            } else if (what === 'reset') {
                saveSettings({ Mode: null, CountdownSeconds: null });
            } else {
                var picked = wrap.querySelector('input[name="upnext-mode"]:checked');
                saveSettings({
                    Mode: picked ? picked.value : null,
                    CountdownSeconds: parseInt(wrap.querySelector('#upnext-count').value, 10)
                });
            }
        });

        var labels = wrap.querySelectorAll('.upnext-option');
        for (var l = 0; l < labels.length; l++) {
            labels[l].setAttribute('tabindex', '0');
        }
        var first = wrap.querySelector('.upnext-option input:checked');
        (first ? first.parentNode : wrap.querySelector('.upnext-sfocus')).focus();
    }

    function onSettingsKey(e, wrap) {
        var target = e.target || {};
        var inSelect = /select/i.test(target.tagName || '');
        var code = e.keyCode;
        if (KEY_BACK.indexOf(code) !== -1) {
            if (code === 8 && inSelect) {
                return;
            }
            swallow(e);
            closeSettings();
        } else if ((code === 38 || code === 40) && !inSelect) {
            swallow(e);
            var list = Array.prototype.slice.call(wrap.querySelectorAll('.upnext-sfocus'));
            var at = list.indexOf(document.activeElement);
            var to = list[Math.max(0, Math.min(list.length - 1, at + (code === 40 ? 1 : -1)))];
            if (to) {
                to.focus();
            }
        } else if ((code === 37 || code === 39) && !inSelect) {
            swallow(e);
            var buttons = Array.prototype.slice.call(wrap.querySelectorAll('.upnext-dialog-buttons button'));
            var i = buttons.indexOf(document.activeElement);
            if (i !== -1) {
                buttons[Math.max(0, Math.min(buttons.length - 1, i + (code === 39 ? 1 : -1)))].focus();
            }
        } else if (code === KEY_ENTER) {
            var active = document.activeElement;
            if (active && active.classList.contains('upnext-option')) {
                swallow(e);
                active.querySelector('input').checked = true;
            } else if (active && /button/i.test(active.tagName) && wrap.contains(active)) {
                swallow(e);
                active.click();
            }
        }
    }

    function saveSettings(body) {
        request('POST', 'Preferences', body).then(function (prefs) {
            if (state.info) {
                state.info.Mode = prefs.Mode;
                state.info.CountdownSeconds = prefs.CountdownSeconds;
            }
            if (state.phase !== 'idle' && prefs.Mode === 'Off') {
                closeCard();
            }
            closeSettings();
            toast('Up Next settings saved');
        }, function () {
            toast('Couldn\'t save Up Next settings');
        });
    }

    function removeSettingsDialog() {
        var el = document.getElementById('upnext-settings');
        if (el && el.parentNode) {
            el.parentNode.removeChild(el);
        }
    }

    function closeSettings() {
        removeSettingsDialog();
        var play = document.querySelector('#upnext-overlay .upnext-play');
        if (play) {
            play.focus();
            // Opening settings paused the countdown; pick it back up.
            var info = state.info;
            if (info && info.Next && info.Mode === 'Autoplay' && !state.countdown && state.phase !== 'idle') {
                startCountdown(info.CountdownSeconds, info.Next.Id);
            }
        }
    }

    function toast(text) {
        var el = document.createElement('div');
        el.className = 'upnext-toast';
        el.textContent = text;
        document.body.appendChild(el);
        setTimeout(function () {
            if (el.parentNode) {
                el.parentNode.removeChild(el);
            }
        }, 2500);
    }

    // Adds "Up Next" under Settings, next to Display, Home, Playback...
    function injectPreferencesLink() {
        var display = document.querySelector('a.lnkDisplayPreferences');
        if (!display || display.parentNode.querySelector('.lnkUpNextPreferences')) {
            return;
        }
        var link = display.cloneNode(true);
        link.className = link.className.replace('lnkDisplayPreferences', 'lnkUpNextPreferences');
        link.setAttribute('href', '#');
        var icon = link.querySelector('.listItemIcon');
        if (icon) {
            icon.classList.remove('tv');
            icon.classList.add('skip_next');
        }
        var text = link.querySelector('.listItemBodyText');
        if (text) {
            text.textContent = 'Up Next';
        }
        link.addEventListener('click', function (e) {
            e.preventDefault();
            e.stopPropagation();
            openSettings();
        });
        display.parentNode.insertBefore(link, display.nextSibling);
    }

    // ------------------------------------------------------------------ styles

    function ensureStyles() {
        if (document.getElementById('upnext-style')) {
            return;
        }
        var css = [
            // Jellyfin only lifts the video above the page in some layouts; force it between our two layers.
            '.upnext-active .videoPlayerContainer{z-index:1000!important;transform-origin:0 0;transform:translate(4vw,6vh) scale(.38);transition:transform .6s cubic-bezier(.2,.8,.2,1);border-radius:10px;overflow:hidden;box-shadow:0 12px 40px rgba(0,0,0,.7)}',
            '.videoPlayerContainer{transition:transform .45s ease}',
            '.upnext-active #videoOsdPage,.upnext-active .skinHeader,.upnext-active .upNextContainer{visibility:hidden!important}',
            '#upnext-bg{position:fixed;top:0;left:0;right:0;bottom:0;z-index:999;overflow:hidden;background:#000}',
            '#upnext-overlay{position:fixed;top:0;left:0;right:0;bottom:0;z-index:1001;color:#fff;font-family:inherit;pointer-events:none}',
            '#upnext-overlay *{box-sizing:border-box}',
            '#upnext-overlay button{pointer-events:auto;font-family:inherit}',
            '.upnext-backdrop{position:absolute;top:0;left:0;right:0;bottom:0;background-size:cover;background-position:center top;transform:scale(1.04);animation:upnext-fade .8s ease both}',
            '.upnext-shade{position:absolute;top:0;left:0;right:0;bottom:0;background:linear-gradient(90deg,rgba(0,0,0,.92) 0%,rgba(0,0,0,.7) 45%,rgba(0,0,0,.25) 100%),linear-gradient(0deg,rgba(0,0,0,.9) 0%,rgba(0,0,0,0) 55%)}',
            '.upnext-mini{position:absolute;left:4vw;top:6vh;width:38vw;height:38vh;z-index:2;background:transparent;border:2px solid transparent;border-radius:10px;cursor:pointer;padding:0;display:flex;align-items:flex-end;justify-content:flex-start;outline:none}',
            '.upnext-mini:hover,.upnext-mini:focus{border-color:#fff}',
            '.upnext-mini-label{margin:.7em;padding:.35em .8em;background:rgba(0,0,0,.65);border-radius:4px;font-size:.95em;display:flex;align-items:center;gap:.4em;opacity:0;transition:opacity .2s}',
            '.upnext-mini:hover .upnext-mini-label,.upnext-mini:focus .upnext-mini-label{opacity:1}',
            '.upnext-gear{position:absolute;top:3vh;right:3vw;z-index:3;background:rgba(0,0,0,.4);border:2px solid transparent;color:#fff;border-radius:50%;width:2.8em;height:2.8em;cursor:pointer;display:flex;align-items:center;justify-content:center;outline:none}',
            '.upnext-gear:hover,.upnext-gear:focus{border-color:#fff;background:rgba(255,255,255,.15)}',
            '.upnext-panel{position:absolute;left:4vw;right:4vw;bottom:6vh;z-index:3;display:flex;align-items:flex-end;justify-content:space-between;gap:3vw;animation:upnext-rise .6s ease both}',
            '.upnext-info{max-width:46vw}',
            '.upnext-label{text-transform:uppercase;letter-spacing:.14em;font-size:.9em;font-weight:600;color:#cfcfcf;margin-bottom:.6em}',
            '.upnext-title{font-size:2.6em;line-height:1.1;margin:0 0 .25em;font-weight:700;text-shadow:0 2px 12px rgba(0,0,0,.6)}',
            '.upnext-logo{max-width:min(28em,40vw);max-height:16vh;display:block;margin-bottom:.6em;filter:drop-shadow(0 2px 10px rgba(0,0,0,.6))}',
            '.upnext-meta{color:#ddd;font-size:1em;margin-bottom:.7em;display:flex;align-items:center;flex-wrap:wrap}',
            '.upnext-dot{margin:0 .5em;color:#888}',
            '.upnext-rating{border:1px solid #aaa;padding:0 .35em;border-radius:3px;font-size:.85em}',
            '.upnext-overview{color:#e6e6e6;font-size:1.02em;line-height:1.45;margin:0 0 1.2em;display:-webkit-box;-webkit-line-clamp:3;-webkit-box-orient:vertical;overflow:hidden}',
            '.upnext-buttons{display:flex;gap:.8em;flex-wrap:wrap}',
            '.upnext-btn{position:relative;overflow:hidden;border:2px solid transparent;border-radius:6px;padding:.75em 1.6em;font-size:1.05em;font-weight:600;cursor:pointer;outline:none;transition:transform .15s}',
            '.upnext-btn-text{position:relative;display:flex;align-items:center;gap:.35em;white-space:nowrap}',
            '.upnext-play{background:#fff;color:#000}',
            '.upnext-fill{position:absolute;left:0;top:0;bottom:0;width:0;background:rgba(0,0,0,.22);transition:width .2s linear}',
            '.upnext-secondary{background:rgba(109,109,110,.7);color:#fff}',
            '.upnext-play:focus{outline:3px solid #fff;outline-offset:3px}',
            '.upnext-btn:hover,.upnext-btn:focus{transform:scale(1.04);border-color:#fff}',
            '.upnext-more{flex-shrink:0;max-width:44vw}',
            '.upnext-more-label{font-size:.95em;font-weight:600;color:#ddd;margin-bottom:.6em}',
            '.upnext-tiles{display:flex;gap:.8em}',
            '.upnext-tile{width:13vw;min-width:9em;background:none;border:2px solid transparent;border-radius:6px;padding:0;color:#fff;cursor:pointer;text-align:left;outline:none;transition:transform .15s}',
            '.upnext-tile:hover,.upnext-tile:focus{border-color:#fff;transform:scale(1.05)}',
            '.upnext-tile-img{display:block;width:100%;padding-top:56.25%;background-size:cover;background-position:center;border-radius:4px;background-color:#222}',
            '.upnext-tile-name{display:block;padding:.4em .3em;font-size:.88em;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}',
            '.upnext-tile-year{color:#aaa}',
            '.upnext-still{transform:none}',
            '#upnext-settings{position:fixed;top:0;left:0;right:0;bottom:0;z-index:1002;background:rgba(0,0,0,.6);display:flex;align-items:center;justify-content:center;padding:16px}',
            '.upnext-dialog{background:#181818;color:#fff;border-radius:10px;padding:1.6em;width:100%;max-width:30em;max-height:90vh;overflow:auto;box-shadow:0 10px 40px rgba(0,0,0,.6)}',
            '.upnext-dialog h2{margin:0 0 1em;font-size:1.5em}',
            '.upnext-field-label{display:block;font-weight:600;margin:1em 0 .5em;color:#ccc}',
            '.upnext-option{display:flex;gap:.8em;align-items:flex-start;padding:.7em .8em;border:2px solid #333;border-radius:6px;margin-bottom:.5em;cursor:pointer;outline:none}',
            '.upnext-option:focus,.upnext-option:hover{border-color:#fff}',
            '.upnext-option input{margin-top:.25em}',
            '.upnext-option small{display:block;color:#aaa;font-weight:400;margin-top:.2em}',
            '.upnext-select{width:100%;padding:.6em;background:#2a2a2a;color:#fff;border:2px solid #444;border-radius:6px;font-size:1em}',
            '.upnext-select:focus{border-color:#fff;outline:none}',
            '.upnext-hint{color:#999;font-size:.9em}',
            '.upnext-dialog-buttons{display:flex;gap:.6em;flex-wrap:wrap;margin-top:1.2em}',
            '.upnext-dialog .upnext-play{background:#fff;color:#000}',
            '.upnext-toast{position:fixed;left:50%;bottom:8vh;transform:translateX(-50%);z-index:1003;background:#323232;color:#fff;padding:.8em 1.4em;border-radius:6px;box-shadow:0 4px 16px rgba(0,0,0,.5)}',
            '@keyframes upnext-fade{from{opacity:0}to{opacity:1}}',
            '@keyframes upnext-rise{from{opacity:0;transform:translateY(20px)}to{opacity:1;transform:none}}',
            '@media (max-width:800px){',
            '.upnext-active .videoPlayerContainer{transform:translate(4vw,4vh) scale(.6)}',
            '.upnext-mini{left:4vw;top:4vh;width:60vw;height:60vh;max-height:34vh}',
            '.upnext-panel{flex-direction:column;align-items:stretch;bottom:3vh;gap:1em}',
            '.upnext-info,.upnext-more{max-width:none}',
            '.upnext-title{font-size:1.7em}',
            '.upnext-overview{display:none}',
            '.upnext-tiles{overflow-x:auto}',
            '.upnext-tile{width:38vw;min-width:0;flex-shrink:0}',
            '.upnext-btn{flex:1;justify-content:center}',
            '.upnext-btn-text{justify-content:center}',
            '}',
            // Phones held sideways: little height to share between the video and the card.
            '@media (max-height:500px){',
            '.upnext-active .videoPlayerContainer{transform:translate(3vw,4vh) scale(.3)}',
            '.upnext-mini{left:3vw;top:4vh;width:30vw;height:30vh;max-height:none}',
            '.upnext-panel{bottom:4vh;flex-direction:row;align-items:flex-end}',
            '.upnext-label{margin-bottom:.3em;font-size:.8em}',
            '.upnext-title{font-size:1.5em}',
            '.upnext-logo{max-height:12vh}',
            '.upnext-overview{display:none}',
            '.upnext-meta{margin-bottom:.5em}',
            '.upnext-btn{padding:.55em 1.2em;flex:none}',
            '.upnext-info,.upnext-more{max-width:48vw}',
            '.upnext-tile{width:11vw;min-width:7em}',
            '}',
            '@media (max-width:800px) and (orientation:portrait){',
            '.upnext-active .videoPlayerContainer{transform:translate(4vw,4vh) scale(.5)}',
            '.upnext-mini{width:50vw;height:50vh;max-height:none}',
            '}'
        ].join('\n');
        var style = document.createElement('style');
        style.id = 'upnext-style';
        style.textContent = css;
        document.head.appendChild(style);
    }

    window.addEventListener('keydown', onKeyDown, true);
    setInterval(tick, TICK_MS);
})();
