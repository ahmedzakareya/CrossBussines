/*
 * Chat in a side drawer, the way modern systems open it: the header's chat icon slides the conversations in over the
 * page you are on (from the LEFT in Arabic, the RIGHT in English - the theme's drawer-end flips with the page), instead
 * of leaving it. The drawer hosts the one chat there is (/Chat?embed=1, _LayoutBackend's embedded mode): the same
 * conversations, mentions, files, reactions, typing and read receipts, never a second copy of them. In a drawer that
 * narrow the chat runs its own one-pane mode (list, then the conversation with a back arrow).
 *
 * Ctrl/Cmd/Shift/middle click still opens the full page, and so does the expand button in the drawer's header.
 * The iframe loads on the first open and is kept, so closing and reopening returns to the same conversation.
 */
(function () {
    'use strict';
    if (window.self !== window.top || window.CBChatDrawer) return;   // never inside the drawer itself

    var lang = (document.documentElement.getAttribute('lang') || 'en').slice(0, 2);
    var TEXT = {
        ar: { title: 'المحادثات', full: 'فتح في صفحة كاملة', close: 'إغلاق' },
        fr: { title: 'Discussions', full: 'Ouvrir en pleine page', close: 'Fermer' },
        en: { title: 'Chats', full: 'Open as a full page', close: 'Close' }
    }[lang] || null;
    TEXT = TEXT || { title: 'Chats', full: 'Open as a full page', close: 'Close' };

    var drawer = null, overlay = null, frame = null, chatUrl = null;

    function isChatLink(a) {
        if (!a || !a.href || a.target === '_blank') return false;
        try { var u = new URL(a.href, location.href); return u.origin === location.origin && /^\/chat\/?$/i.test(u.pathname); }
        catch (e) { return false; }
    }

    function build() {
        // the floating assistant would sit over the drawer's composer: it steps aside while the chat is open
        var st = document.createElement('style');
        st.textContent = 'body[data-cb-chat-open] .cb-ai-fab, body[data-cb-chat-open] .cb-ai-panel { display: none !important; }';
        document.head.appendChild(st);
        overlay = document.createElement('div');
        overlay.className = 'drawer-overlay';
        overlay.style.display = 'none';
        overlay.addEventListener('click', close);

        drawer = document.createElement('div');
        drawer.className = 'drawer drawer-end bg-body cb-chat-drawer';
        drawer.setAttribute('role', 'dialog');
        drawer.setAttribute('aria-label', TEXT.title);
        drawer.style.width = 'min(480px, 100vw)';
        drawer.innerHTML =
            '<div class="card w-100 border-0 rounded-0 d-flex flex-column h-100">' +
            '  <div class="card-header border-bottom min-h-65px ps-6 pe-4 flex-nowrap">' +
            '    <div class="card-title m-0 d-flex align-items-center">' +
            '      <span class="symbol symbol-35px me-3"><span class="symbol-label bg-light-primary"><i class="ki-outline ki-messages fs-2 text-primary"></i></span></span>' +
            '      <span class="fw-bold fs-4 text-gray-900"></span>' +
            '    </div>' +
            '    <div class="card-toolbar m-0 gap-1">' +
            '      <a class="btn btn-sm btn-icon btn-active-light-primary" data-cb-chat-full><i class="ki-outline ki-maximize fs-2"></i></a>' +
            '      <button type="button" class="btn btn-sm btn-icon btn-active-light-primary" data-cb-chat-close><i class="ki-outline ki-cross fs-1"></i></button>' +
            '    </div>' +
            '  </div>' +
            '  <div class="flex-grow-1 position-relative overflow-hidden">' +
            '    <div class="position-absolute top-50 start-50 translate-middle" data-cb-chat-wait><span class="spinner-border text-primary"></span></div>' +
            '  </div>' +
            '</div>';
        drawer.querySelector('.card-title .fw-bold').textContent = TEXT.title;
        var full = drawer.querySelector('[data-cb-chat-full]');
        full.href = chatUrl; full.title = TEXT.full;
        var x = drawer.querySelector('[data-cb-chat-close]');
        x.title = TEXT.close; x.addEventListener('click', close);
        document.body.appendChild(overlay);
        document.body.appendChild(drawer);
    }

    function open(url) {
        chatUrl = url;
        if (!drawer) build();
        if (!frame) {
            frame = document.createElement('iframe');
            frame.title = TEXT.title;
            frame.style.cssText = 'position:absolute;inset:0;width:100%;height:100%;border:0;opacity:0;transition:opacity .2s ease';
            frame.addEventListener('load', function () {
                frame.style.opacity = '1';
                var w = drawer.querySelector('[data-cb-chat-wait]'); if (w) w.remove();
            });
            frame.src = url + (url.indexOf('?') >= 0 ? '&' : '?') + 'embed=1';
            drawer.querySelector('.flex-grow-1').appendChild(frame);
        }
        overlay.style.display = '';
        // next frame, so the transform transitions instead of jumping
        requestAnimationFrame(function () { drawer.classList.add('drawer-on'); });
        document.body.setAttribute('data-cb-chat-open', 'on');
    }

    function close() {
        if (!drawer) return;
        drawer.classList.remove('drawer-on');
        overlay.style.display = 'none';
        document.body.removeAttribute('data-cb-chat-open');
    }

    document.addEventListener('click', function (e) {
        if (e.defaultPrevented || e.button !== 0 || e.ctrlKey || e.metaKey || e.shiftKey || e.altKey) return;
        var a = e.target.closest && e.target.closest('a');
        if (!isChatLink(a)) return;
        if (/^\/chat\/?$/i.test(location.pathname)) return;           // already on the chat page
        if (drawer && drawer.contains(a)) return;                      // the drawer's own "full page" link
        e.preventDefault();
        if (drawer && drawer.classList.contains('drawer-on')) close(); else open(a.href.split('#')[0]);
    }, true);

    document.addEventListener('keydown', function (e) { if (e.key === 'Escape' && drawer && drawer.classList.contains('drawer-on')) close(); });

    window.CBChatDrawer = { open: function () { open((chatUrl || '/Chat')); }, close: close };
})();
