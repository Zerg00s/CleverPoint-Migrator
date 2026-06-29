// Column-resize jitter fix for FluentDataGrid.
//
// FluentUI lays the grid out with `fr` units, but the moment you start dragging a
// column separator it rewrites the whole template from `fr` to integer-pixel
// clientWidth values. The rounded pixel sum rarely equals the container width, so
// the grid reflows once on the first drag (the "jump"), and the fixed first column
// (the checkbox) can balloon because its measured width is off.
//
// The fix: keep every resizable grid pinned to its already-resolved PIXEL widths
// from the moment it renders, so a drag has nothing to convert. getComputedStyle
// always returns the resolved pixels for the fr template, so assigning that back is
// visually a no-op. The catch is that Blazor re-applies the fr template on every
// re-render, so we also watch the grid's `style` attribute and re-pin whenever it
// reverts to fr/minmax. We never touch a grid whose template is already pure pixels
// (so we never fight a real user resize).

(function () {
    function hasFlex(t) { return !t || t === 'none' || t.indexOf('fr') !== -1 || t.indexOf('minmax') !== -1; }

    function pin(grid) {
        if (!grid.classList || !grid.classList.contains('grid')) return;
        // Only act while the inline template is still flexible (fr/minmax) or unset.
        // Once it's pixels we leave it alone - that's either our pin or a user resize.
        const inline = grid.style.gridTemplateColumns;
        if (inline && !hasFlex(inline)) return;
        const resolved = getComputedStyle(grid).gridTemplateColumns;
        if (hasFlex(resolved)) return;            // not laid out yet; try again later
        if (resolved === inline) return;
        grid.style.gridTemplateColumns = resolved; // pin to pixels (no visual change)
    }

    function scan(root) {
        const scope = root instanceof Element ? root : document;
        if (scope.matches && scope.matches('.fluent-data-grid.grid')) pin(scope);
        scope.querySelectorAll && scope.querySelectorAll('.fluent-data-grid.grid').forEach(pin);
    }

    function start() {
        scan(document);
        const mo = new MutationObserver(muts => {
            for (const m of muts) {
                if (m.type === 'attributes' && m.target.classList && m.target.classList.contains('grid')) {
                    pin(m.target);
                } else if (m.type === 'childList') {
                    for (const n of m.addedNodes) if (n.nodeType === 1) scan(n);
                }
            }
        });
        mo.observe(document.body, {
            childList: true, subtree: true,
            attributes: true, attributeFilter: ['style'],
        });
        // A window resize legitimately needs new pixel widths: let grids relax back to
        // their fr template for one frame, then re-pin to the new resolved pixels.
        let raf = 0;
        window.addEventListener('resize', () => {
            if (raf) cancelAnimationFrame(raf);
            document.querySelectorAll('.fluent-data-grid.grid').forEach(g => {
                if (g.dataset.cpUserResized !== '1') g.style.gridTemplateColumns = '';
            });
            raf = requestAnimationFrame(() => scan(document));
        });
        // Remember which grids the user has hand-resized so the window-resize relax
        // above doesn't throw their chosen widths away.
        document.addEventListener('pointerdown', e => {
            const h = e.target.closest && e.target.closest('.actual-resize-handle, .resize-handle');
            const grid = h && h.closest('.fluent-data-grid.grid');
            if (grid) grid.dataset.cpUserResized = '1';
        }, true);
    }

    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start);
    else start();
})();
